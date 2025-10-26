using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;
using IndicatorService;

namespace RemoteIndicator.ATAS.Communication
{
    /// <summary>
    /// Data Pusher - Manages Channel B communication (PUSH pattern)
    ///
    /// Architecture (v4.1):
    /// - Layer 1 component: Independent ZMQ communication management
    /// - One-way push: Fire-and-forget, no response expected
    /// - Thread isolation: Socket created and used only in PushLoop thread
    /// - Lazy construction: symbol/resolution/numUnits passed from main thread (avoids ATAS API access)
    /// - Clear lifecycle: Start() → PushLoop → Stop()
    ///
    /// Purpose (v4.0 design):
    /// - Push realtime completed bars to Service (Channel B, port 5556)
    /// - Service caches pushed bars for future indicator requests
    /// - Optimizes performance: 90%+ requests hit Service cache (P95 < 1s)
    /// - Fire-and-forget: No ACK required, DataPuller (Channel C) provides fallback
    ///
    /// Solves v4.0 Issues:
    /// - C2: Proper Stop() waits for thread completion
    /// - C3: No RestartServer, plugin layer handles reconnection
    /// - C4: Socket created and used only in PushLoop (thread isolation)
    /// - C5: symbol/resolution/numUnits from constructor (main thread, not accessed in background)
    ///
    /// Usage Pattern:
    /// 1. DataTerminal.OnCalculate: Use TimeframeConverter.ToProto() to get resolution/numUnits
    /// 2. DataTerminal.OnCalculate: Create pusher with symbol/resolution/numUnits from main thread
    /// 3. Start(): Create socket and start PushLoop
    /// 4. DataTerminal.OnCalculate: Detect completed bar → EnqueueBar() → non-blocking
    /// 5. PushLoop: Dequeue → Push to Service (fire-and-forget)
    /// 6. DataTerminal periodic check: IsConnected() → detect disconnect → Stop() + recreate
    /// </summary>
    public class DataPusher : IDisposable
    {
        #region Configuration (Immutable, passed from main thread)

        private readonly string _host;
        private readonly int _port;
        private readonly string _symbol;
        private readonly string _resolution;      // Proto format: "SECOND", "MIN", "H", "D", "VOLUME", "TICK"
        private readonly int _numUnits;           // Period unit count

        #endregion

        #region Push Queue (Thread-safe)

        // ConcurrentQueue: Thread-safe, lock-free enqueue/dequeue
        // Note: Unlike IndicatorChannelProxy, we use unbounded queue here
        // because bar completion is infrequent (e.g., every 5 minutes)
        // and losing bars would require expensive Service cache miss
        private ConcurrentQueue<BarData>? _pushQueue;

        #endregion

        #region Background Thread

        private PushSocket? _socket;
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
        /// <param name="port">Service port (Channel B, typically 5556)</param>
        /// <param name="symbol">Symbol (from InstrumentInfo.Instrument on main thread)</param>
        /// <param name="resolution">Resolution in Proto format (from TimeframeConverter.ToProto on main thread)</param>
        /// <param name="numUnits">Period unit count (from TimeframeConverter.ToProto on main thread)</param>
        /// <param name="logger">Optional logger callback</param>
        /// <remarks>
        /// CRITICAL: symbol/resolution/numUnits MUST be obtained from main thread before construction
        /// Use TimeframeConverter.ToProto() to convert ATAS ChartType+TimeFrame → resolution+numUnits
        /// This avoids background thread accessing ATAS API (violates thread safety)
        /// </remarks>
        public DataPusher(
            string host,
            int port,
            string symbol,
            string resolution,
            int numUnits,
            Action<string>? logger = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
            _numUnits = numUnits > 0 ? numUnits : throw new ArgumentException("NumUnits must be positive", nameof(numUnits));
            _logger = logger;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Start pusher - Create socket and start worker thread
        /// </summary>
        /// <remarks>
        /// Thread-safe: Can be called from main thread
        /// Socket creation happens in PushLoop thread (thread isolation)
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
                // Create push queue
                _pushQueue = new ConcurrentQueue<BarData>();

                // Create cancellation token
                _cts = new CancellationTokenSource();

                // Start worker thread
                _workerTask = Task.Run(() => PushLoop(_cts.Token));

                Log($"DataPusher started (symbol={_symbol}, resolution={_resolution}, numUnits={_numUnits})");
            }
            catch (Exception ex)
            {
                Log($"Start error: {ex.Message}");
                _isConnected = false;
            }
        }

        /// <summary>
        /// Enqueue bar for push - Non-blocking
        /// </summary>
        /// <param name="barData">Bar data to push</param>
        /// <remarks>
        /// Thread-safe: Can be called from main thread (OnCalculate)
        /// Non-blocking: Returns immediately, bar processed in PushLoop
        /// </remarks>
        public void EnqueueBar(BarData barData)
        {
            if (_pushQueue == null)
            {
                Log("Push queue not initialized, ignoring enqueue");
                return;
            }

            _pushQueue.Enqueue(barData);
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
        /// Stop pusher - Stop worker thread and cleanup
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
                _pushQueue = null;

                _cts?.Dispose();
                _cts = null;

                _workerTask = null;
                _isConnected = false;

                Log("DataPusher stopped");
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

        #region Push Loop (Background Thread)

        /// <summary>
        /// Push loop - Process push queue and send to Service
        /// </summary>
        /// <param name="token">Cancellation token</param>
        /// <remarks>
        /// Runs in background thread
        /// Socket created and used only in this thread (thread isolation)
        /// Fire-and-forget: No response expected from Service
        /// </remarks>
        private void PushLoop(CancellationToken token)
        {
            Log("PushLoop started");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Create socket in worker thread (thread isolation)
                    // Socket recreated on each connection attempt (handles failures)
                    _socket = new PushSocket();
                    _socket.Connect($"tcp://{_host}:{_port}");
                    _isConnected = true;
                    Log($"Connected to {_host}:{_port}");

                    // Inner loop: push bars until socket fails
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            // Dequeue bar (non-blocking)
                            if (_pushQueue!.TryDequeue(out var barData))
                            {
                                // Push to Service (fire-and-forget)
                                // Caller MUST set all fields correctly before enqueuing
                                _socket.SendFrame(barData.ToByteArray());

                                _isConnected = true;
                                Log($"Pushed bar: {barData.Symbol} {barData.Resolution}({barData.NumUnits}) @ {barData.CloseTimeMs}");
                            }
                            else
                            {
                                // Queue empty, sleep to avoid CPU spin
                                Thread.Sleep(100);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (!token.IsCancellationRequested)
                            {
                                Log($"Push error: {ex.Message}");
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

            Log("PushLoop stopped");
        }

        #endregion

        #region Utilities

        private void Log(string message)
        {
            _logger?.Invoke($"[DataPusher] {message}");
        }

        #endregion
    }
}
