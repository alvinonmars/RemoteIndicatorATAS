using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;
using IndicatorService;

namespace RemoteIndicator.ATAS.Communication
{
    /// <summary>
    /// Indicator Channel Proxy - Manages Channel A communication (REQ-REP pattern)
    ///
    /// Architecture (v4.1):
    /// - Layer 1 component: Independent ZMQ communication management
    /// - Thread isolation: Socket created and used only in WorkerLoop thread
    /// - Lazy construction: symbol/timeframe passed from main thread (avoids ATAS API access)
    /// - Thread-safe cache: All reads/writes locked, returns copies
    /// - Clear lifecycle: Start() → WorkerLoop → Stop()
    ///
    /// Solves v4.0 Issues:
    /// - C1: Cache read/write fully locked
    /// - C5: symbol/timeframe from constructor (main thread), not accessed in background
    /// - C7: IsConnected() enables reconnection detection
    /// - H1: Configuration changes trigger reconnection in plugin layer
    ///
    /// Usage Pattern:
    /// 1. RemoteIndicatorBase.OnCalculate: Use TimeframeConverter.ToProto() to get resolution/numUnits
    /// 2. RemoteIndicatorBase.OnCalculate: Create proxy with symbol/resolution/numUnits from main thread
    /// 3. Start(): Create socket and start WorkerLoop
    /// 4. RemoteIndicatorBase.OnRender: RequestIndicator(tickTimeMs) → non-blocking enqueue
    /// 5. WorkerLoop: Dequeue → Send request → Receive response → Update cache
    /// 6. RemoteIndicatorBase.OnRender: GetCachedElements() → locked read
    /// 7. RemoteIndicatorBase periodic check: IsConnected() → detect disconnect → Stop() + recreate
    /// </summary>
    public class IndicatorChannelProxy : IDisposable
    {
        #region Request Info Structure

        private struct RequestInfo
        {
            public long TickTimeMs;
            public DateTime EnqueuedAt;
        }

        #endregion

        #region Configuration (Immutable, passed from main thread)

        private readonly string _host;
        private readonly int _port;
        private readonly string _symbol;
        private readonly string _resolution;      // Proto format: "SECOND", "MIN", "H", "D", "VOLUME", "TICK"
        private readonly int _numUnits;           // Period unit count
        private readonly string _indicatorType;

        #endregion

        #region Request Queue (Thread-safe)

        // BlockingCollection provides thread-safe enqueue/dequeue
        // Bounded capacity prevents memory exhaustion if service is slow
        private BlockingCollection<RequestInfo>? _requestQueue;
        private const int MaxQueueSize = 10;

        #endregion

        #region Cache (Thread-safe)

        private List<IndicatorElement> _cachedElements = new();
        private readonly object _cacheLock = new object();
        private DateTime _lastResponseTime = DateTime.MinValue;

        #endregion

        #region Background Thread

        private RequestSocket? _socket;
        private Task? _workerTask;
        private CancellationTokenSource? _cts;
        private volatile bool _isConnected = false;

        #endregion

        #region Logging & Callbacks

        private Action<string>? _logger;
        private Action? _onDataUpdatedCallback;

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor - Initialize with immutable configuration
        /// </summary>
        /// <param name="host">Service host IP</param>
        /// <param name="port">Service port (Channel A, typically 5555)</param>
        /// <param name="symbol">Symbol (from InstrumentInfo.Instrument on main thread)</param>
        /// <param name="resolution">Resolution in Proto format (from TimeframeConverter.ToProto on main thread)</param>
        /// <param name="numUnits">Period unit count (from TimeframeConverter.ToProto on main thread)</param>
        /// <param name="indicatorType">Indicator type (e.g., "extreme_price")</param>
        /// <param name="logger">Optional logger callback</param>
        /// <param name="onDataUpdated">Optional callback invoked when data is updated (called from worker thread)</param>
        /// <remarks>
        /// CRITICAL: symbol/resolution/numUnits MUST be obtained from main thread before construction
        /// Use TimeframeConverter.ToProto() to convert ATAS ChartType+TimeFrame → resolution+numUnits
        /// This avoids background thread accessing ATAS API (violates thread safety)
        /// </remarks>
        public IndicatorChannelProxy(
            string host,
            int port,
            string symbol,
            string resolution,
            int numUnits,
            string indicatorType,
            Action<string>? logger = null,
            Action? onDataUpdated = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
            _numUnits = numUnits > 0 ? numUnits : throw new ArgumentException("NumUnits must be positive", nameof(numUnits));
            _indicatorType = indicatorType ?? throw new ArgumentNullException(nameof(indicatorType));
            _logger = logger;
            _onDataUpdatedCallback = onDataUpdated;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Start proxy - Create socket and start worker thread
        /// </summary>
        /// <remarks>
        /// Thread-safe: Can be called from main thread
        /// Socket creation happens in WorkerLoop thread (thread isolation)
        /// </remarks>
        public void Start()
        {
            if (_workerTask != null)
            {
                Log("Already started, ignoring Start() call");
                return;
            }

            try
            {
                // Create request queue
                _requestQueue = new BlockingCollection<RequestInfo>(MaxQueueSize);

                // Create cancellation token
                _cts = new CancellationTokenSource();

                // Start worker thread
                _workerTask = Task.Run(() => WorkerLoop(_cts.Token));

                Log($"IndicatorChannelProxy started (symbol={_symbol}, resolution={_resolution}, numUnits={_numUnits})");
            }
            catch (Exception ex)
            {
                Log($"Start error: {ex.Message}");
                _isConnected = false;
            }
        }

        /// <summary>
        /// Request indicator - Non-blocking enqueue
        /// </summary>
        /// <param name="tickTimeMs">Reference tick time (Unix milliseconds UTC)</param>
        /// <remarks>
        /// Thread-safe: Can be called from main thread (OnRender)
        /// Non-blocking: Returns immediately, request processed in WorkerLoop
        /// </remarks>
        public void RequestIndicator(long tickTimeMs)
        {
            if (_requestQueue == null)
            {
                Log("Request queue not initialized, ignoring request");
                return;
            }

            var requestInfo = new RequestInfo
            {
                TickTimeMs = tickTimeMs,
                EnqueuedAt = DateTime.Now
            };

            // Try to add to queue (non-blocking)
            if (!_requestQueue.TryAdd(requestInfo))
            {
                Log($"Request queue full ({MaxQueueSize}), dropping request");
            }
        }

        /// <summary>
        /// Get cached elements - Thread-safe read
        /// </summary>
        /// <returns>Copy of cached elements</returns>
        /// <remarks>
        /// Thread-safe: Locks cache during read
        /// Returns copy: Caller can safely iterate without lock
        /// Called from main thread (OnRender)
        /// </remarks>
        public List<IndicatorElement> GetCachedElements()
        {
            lock (_cacheLock)
            {
                return new List<IndicatorElement>(_cachedElements);
            }
        }

        //clear cache (for testing)
        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _cachedElements.Clear();
            }
        }

        /// <summary>
        /// Check connection status
        /// </summary>
        /// <returns>True if connected</returns>
        /// <remarks>
        /// Thread-safe: volatile field read
        /// Used by plugin layer for periodic reconnection check
        /// </remarks>
        public bool IsConnected()
        {
            return _isConnected;
        }

        /// <summary>
        /// Stop proxy - Stop worker thread and cleanup
        /// </summary>
        /// <remarks>
        /// Thread-safe: Can be called from main thread
        /// Waits for worker thread to finish (up to 5 seconds)
        /// </remarks>
        public void Stop()
        {
            try
            {
                // Signal cancellation
                _cts?.Cancel();

                // Wait for worker thread (with timeout)
                if (_workerTask != null && !_workerTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    Log("WARNING: Worker thread did not stop gracefully");
                }

                // Cleanup
                _requestQueue?.Dispose();
                _requestQueue = null;

                _cts?.Dispose();
                _cts = null;

                _workerTask = null;
                _isConnected = false;

                Log("IndicatorChannelProxy stopped");
            }
            catch (Exception ex)
            {
                Log($"Stop error: {ex.Message}");
            }
        }

        /// <summary>
        /// Dispose - Stop and cleanup
        /// </summary>
        public void Dispose()
        {
            Stop();
        }

        #endregion

        #region Worker Loop (Background Thread)

        /// <summary>
        /// Worker loop - Process request queue and manage socket
        /// </summary>
        /// <param name="token">Cancellation token</param>
        /// <remarks>
        /// Runs in background thread
        /// Socket created and used only in this thread (thread isolation)
        /// Processes requests from queue, updates cache with lock
        /// </remarks>
        private void WorkerLoop(CancellationToken token)
        {
            Log("WorkerLoop started");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Create socket in worker thread (thread isolation)
                    // Socket recreated on each connection attempt (handles timeout recovery)
                    _socket = new RequestSocket();
                    _socket.Connect($"tcp://{_host}:{_port}");
                    _isConnected = true;
                    Log($"Connected to {_host}:{_port}");

                    // Inner loop: process requests until socket fails
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            // Dequeue request (blocking with timeout)
                            if (!_requestQueue!.TryTake(out var requestInfo, 100, token))
                            {
                                continue; // Timeout or cancelled, retry
                            }

                            // Build IndicatorRequest (using pre-stored resolution/numUnits)
                            var request = new IndicatorRequest
                            {
                                RequestId = Guid.NewGuid().ToString(),
                                Symbol = _symbol,
                                SentAtMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                                TickTimeMs = requestInfo.TickTimeMs,
                                IndicatorType = _indicatorType,
                                Resolution = _resolution,
                                NumUnits = _numUnits
                            };

                            // Send request
                            _socket.SendFrame(request.ToByteArray());

                            // Receive response (with timeout)
                            bool received = _socket.TryReceiveFrameBytes(
                                TimeSpan.FromSeconds(5),
                                out byte[]? responseBytes
                            );

                            if (!received || responseBytes == null)
                            {
                                Log("Request timeout - REQ socket corrupted, recreating");
                                _isConnected = false;
                                // CRITICAL: REQ socket enters invalid state after timeout
                                // Must break to recreate socket
                                break; // Exit inner loop to recreate socket
                            }

                            // Parse response
                            var response = IndicatorResponse.Parser.ParseFrom(responseBytes);

                            // Update cache (with lock)
                            lock (_cacheLock)
                            {
                                _cachedElements = response.Elements.ToList();
                                _lastResponseTime = DateTime.Now;
                            }

                            // Notify data updated (called from worker thread)
                            _onDataUpdatedCallback?.Invoke();

                            _isConnected = true; // Mark as connected on successful response
                            var detectedTickTimeMs = response.DetectedTickTimeMs;
                            DateTime detectedTime = DateTimeOffset.FromUnixTimeMilliseconds(detectedTickTimeMs).UtcDateTime;
                            DateTime tickTime = DateTimeOffset.FromUnixTimeMilliseconds(requestInfo.TickTimeMs).UtcDateTime;
                            Log($"Request response received: {response.Elements.Count} elements | TickTime={tickTime:yyyy-MM-dd HH:mm:ss}, DetectedTime={detectedTime:yyyy-MM-dd HH:mm:ss}");
                        }
                        catch (OperationCanceledException)
                        {
                            // Normal cancellation, exit all loops
                            return;
                        }
                        catch (Exception ex)
                        {
                            if (!token.IsCancellationRequested)
                            {
                                Log($"Request processing error: {ex.Message}");
                                _isConnected = false;
                                break; // Exit inner loop to recreate socket
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Socket creation error: {ex.Message}");
                    _isConnected = false;
                }
                finally
                {
                    // Cleanup socket before recreating
                    try
                    {
                        _socket?.Dispose();
                        _socket = null;
                    }
                    catch (Exception ex)
                    {
                        Log($"Socket dispose error: {ex.Message}");
                    }
                }

                // Wait before reconnecting (avoid tight loop)
                if (!token.IsCancellationRequested)
                {
                    Log("Waiting 1 second before reconnecting...");
                    Thread.Sleep(1000);
                }
            }

            Log("WorkerLoop stopped");
        }

        #endregion

        #region Utilities

        private void Log(string message)
        {
            _logger?.Invoke($"[IndicatorChannelProxy] {message}");
        }

        #endregion
    }
}
