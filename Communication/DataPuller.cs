using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;
using IndicatorService;

namespace RemoteIndicator.ATAS.Communication
{
    /// <summary>
    /// Data Puller - Manages Channel C communication (REP pattern)
    ///
    /// Architecture (v4.1):
    /// - Layer 1 component: Independent ZMQ communication management
    /// - Reactive response: Service sends REQ, ATAS responds with REP
    /// - Thread isolation: Socket created and used only in RepLoop thread
    /// - Interface decoupling: Query cache via IBarCacheProvider (avoids direct access)
    /// - Clear lifecycle: Start() → RepLoop → Stop()
    ///
    /// Purpose (v4.0 design):
    /// - Respond to Service data requests (Channel C, port 5557)
    /// - Provides fallback when Service cache miss (e.g., first request, Service restart)
    /// - Guarantees 100% data availability (Channel B push may fail, Channel C always responds)
    /// - Query from cache: Fast response (<10ms for range query)
    ///
    /// Key Design (Interface Decoupling):
    /// - DataPuller does NOT access _barCache directly
    /// - DataPuller uses IBarCacheProvider interface
    /// - DataTerminal implements IBarCacheProvider
    /// - Benefit: Background thread (RepLoop) does NOT access ATAS API
    ///
    /// Solves v4.0 Issues:
    /// - C2: Proper Stop() waits for thread completion
    /// - C3: No RestartServer, plugin layer handles reconnection
    /// - C4: Socket created and used only in RepLoop (thread isolation)
    /// - C5: Cache access via interface, no ATAS API access in background
    ///
    /// Usage Pattern:
    /// 1. DataTerminal.OnCalculate: Create puller with IBarCacheProvider implementation
    /// 2. Start(): Create socket and start RepLoop
    /// 3. Service sends DataRequest → RepLoop receives
    /// 4. RepLoop queries cache via IBarCacheProvider.QueryBars() (thread-safe)
    /// 5. RepLoop sends DataResponse back to Service
    /// 6. DataTerminal periodic check: IsConnected() → detect disconnect → Stop() + recreate
    /// </summary>
    public class DataPuller : IDisposable
    {
        #region Configuration (Immutable)

        private readonly string _host;
        private readonly int _port;
        private readonly IBarCacheProvider _cacheProvider;

        #endregion

        #region Background Thread

        private ResponseSocket? _socket;
        private Task? _workerTask;
        private CancellationTokenSource? _cts;
        private volatile bool _isConnected = false;

        #endregion

        #region Logging

        private Action<string>? _logger;

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor - Initialize with immutable configuration
        /// </summary>
        /// <param name="host">Service host IP</param>
        /// <param name="port">Service port (Channel C, typically 5557)</param>
        /// <param name="cacheProvider">Cache provider interface (implemented by DataTerminal)</param>
        /// <param name="logger">Optional logger callback</param>
        /// <remarks>
        /// CRITICAL: cacheProvider MUST be thread-safe (lock cache during query)
        /// RepLoop (background thread) calls cacheProvider.QueryBars()
        /// DataTerminal (main thread) updates cache in OnCalculate
        /// </remarks>
        public DataPuller(
            string host,
            int port,
            IBarCacheProvider cacheProvider,
            Action<string>? logger = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _cacheProvider = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
            _logger = logger;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Start puller - Create socket and start worker thread
        /// </summary>
        /// <remarks>
        /// Thread-safe: Can be called from main thread
        /// Socket creation happens in RepLoop thread (thread isolation)
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
                // Create cancellation token
                _cts = new CancellationTokenSource();

                // Start worker thread
                _workerTask = Task.Run(() => RepLoop(_cts.Token));

                Log("DataPuller started");
            }
            catch (Exception ex)
            {
                Log($"Start error: {ex.Message}");
                _isConnected = false;
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
        /// Stop puller - Stop worker thread and cleanup
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
                _cts?.Dispose();
                _cts = null;

                _workerTask = null;
                _isConnected = false;

                Log("DataPuller stopped");
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

        #region Rep Loop (Background Thread)

        /// <summary>
        /// Rep loop - Listen for DataRequest and respond from cache
        /// </summary>
        /// <param name="token">Cancellation token</param>
        /// <remarks>
        /// Runs in background thread
        /// Socket created and used only in this thread (thread isolation)
        /// Queries cache via interface (no direct ATAS API access)
        /// </remarks>
        private void RepLoop(CancellationToken token)
        {
            Log("RepLoop started");

            try
            {
                // Create socket in worker thread (thread isolation)
                _socket = new ResponseSocket();
                _socket.Connect($"tcp://{_host}:{_port}");
                _isConnected = true;
                Log($"Connected to {_host}:{_port}");

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Listen for DataRequest (with timeout)
                        bool received = _socket.TryReceiveFrameBytes(
                            TimeSpan.FromMilliseconds(100),
                            out byte[]? requestBytes
                        );

                        if (!received || requestBytes == null)
                        {
                            continue; // Timeout, retry
                        }

                        // CRITICAL: REP socket requires send after every recv
                        // Build response in try-catch, always send
                        DataResponse response;
                        try
                        {
                            // Parse DataRequest
                            var request = DataRequest.Parser.ParseFrom(requestBytes);
                            Log($"DataRequest received: [{request.StartTickTimeMs} - {request.EndTickTimeMs}], resolution={request.Resolution}({request.NumUnits})");

                            // Query cache via interface (thread-safe, includes timeframe validation)
                            response = _cacheProvider.QueryBars(request);
                        }
                        catch (Exception ex)
                        {
                            Log($"Request processing error: {ex.Message}, sending empty response");
                            // MUST send response even on error (REP socket requirement)
                            response = new DataResponse
                            {
                                RequestId = "",
                                Symbol = "",
                                BarsCollected = 0,
                                DebugInfo = $"Error: {ex.Message}"
                            };
                        }

                        // Send DataResponse (always, even on error)
                        _socket.SendFrame(response.ToByteArray());

                        _isConnected = true;
                        Log($"DataResponse sent: {response.BarsCollected} bars");
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal cancellation, exit loop
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Fatal error (e.g., socket send failed)
                        if (!token.IsCancellationRequested)
                        {
                            Log($"Fatal error in RepLoop: {ex.Message}");
                            _isConnected = false;
                            break; // Exit loop, socket likely corrupted
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"RepLoop initialization error: {ex.Message}");
                _isConnected = false;
            }
            finally
            {
                // Cleanup socket (in worker thread, thread isolation)
                try
                {
                    _socket?.Dispose();
                    _socket = null;
                }
                catch (Exception ex)
                {
                    Log($"Socket dispose error: {ex.Message}");
                }

                Log("RepLoop stopped");
            }
        }

        #endregion

        #region Utilities

        private void Log(string message)
        {
            _logger?.Invoke($"[DataPuller] {message}");
        }

        #endregion
    }
}
