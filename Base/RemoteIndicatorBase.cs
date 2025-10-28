using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using RemoteIndicator.ATAS.Communication;
using RemoteIndicator.ATAS.Utilities;
using RemoteIndicator.ATAS.Monitoring;
using RemoteIndicator.ATAS.UI;
using System.Windows.Input;
using Utils.Common.Logging;
using Element = IndicatorService.IndicatorElement;

namespace RemoteIndicator.ATAS.Base
{
    /// <summary>
    /// Abstract base class for remote indicators v4.1
    ///
    /// v4.1 Architecture Improvements:
    /// - Lazy initialization: Wait for InstrumentInfo/ChartInfo availability in OnCalculate
    /// - TimeFrame conversion: Use TimeframeConverter.ToProto() to get resolution/numUnits from main thread
    /// - symbol/resolution/numUnits: Obtained from main thread before creating proxy (avoids ATAS API in background)
    /// - Periodic reconnection: Check IsConnected() every 5 seconds
    /// - No RedrawChart(): Rely on OnRender() natural trigger (max 1 tick delay)
    /// - Cache fully locked: GetCachedElements() returns copy
    /// - Layer separation: Proxy handles ZMQ, plugin handles lifecycle
    ///
    /// Solves v4.0 Critical Issues:
    /// - C1: Cache read/write fully locked (IndicatorChannelProxy)
    /// - C5: symbol/resolution/numUnits from main thread, passed to proxy constructor
    /// - C7: Periodic IsConnected() check enables reconnection
    /// - C8: Null checks on all TryReceive results
    /// - H1: Configuration change triggers reconnection (via _isInitialized = false)
    /// - H2: Request throttling (1 second, existing)
    ///
    /// Usage Pattern:
    /// 1. OnCalculate: Check CanInitialize() (includes TimeFrame parse validation)
    /// 2. OnCalculate: Use TimeframeConverter.ToProto() to convert ATAS TimeFrame → Proto format
    /// 3. OnCalculate: Create proxy with resolution/numUnits, start worker thread
    /// 4. OnCalculate: Periodic connection check (every 5s)
    /// 5. OnRender: Trigger request if needed (non-blocking)
    /// 6. OnRender: Render cached elements
    /// </summary>
    public abstract class RemoteIndicatorBase : Indicator, IMonitorablePlugin
    {
        #region Extension Points (Subclasses must implement)

        /// <summary>
        /// Indicator type identifier (e.g., "extreme_price")
        /// </summary>
        protected abstract string IndicatorType { get; }

        /// <summary>
        /// Render elements on chart
        /// </summary>
        /// <param name="context">Rendering context</param>
        /// <param name="elements">Elements to render</param>
        protected abstract void RenderElements(RenderContext context, List<Element> elements);

        /// <summary>
        /// Get optional request parameters (override if needed)
        /// </summary>
        protected virtual Dictionary<string, string> GetRequestParameters()
        {
            return new Dictionary<string, string>();
        }

        #endregion

        #region Configuration

        private string _serviceHost = "119.247.58.107";
        private int _servicePort = 5555;
        private bool _enableDebugLog = true;
        private bool _enableManualSteppingMode = false;
        private bool _showFutureMask = true;

        [Display(Name = "Service Host", GroupName = "Connection", Order = 10)]
        public string ServiceHost
        {
            get => _serviceHost;
            set
            {
                if (_serviceHost != value)
                {
                    _serviceHost = value;
                    // Configuration changed, trigger reconnection
                    if (_isInitialized)
                    {
                        Log("Service host changed, will reconnect");
                        CleanupProxy(); // Properly cleanup old proxy
                        _isInitialized = false; // Force re-initialization
                    }
                }
            }
        }

        [Display(Name = "Service Port", GroupName = "Connection", Order = 20)]
        public int ServicePort
        {
            get => _servicePort;
            set
            {
                if (_servicePort != value)
                {
                    _servicePort = value;
                    // Configuration changed, trigger reconnection
                    if (_isInitialized)
                    {
                        Log("Service port changed, will reconnect");
                        CleanupProxy(); // Properly cleanup old proxy
                        _isInitialized = false; // Force re-initialization
                    }
                }
            }
        }

        [Display(Name = "Enable Manual Stepping Mode", GroupName = "Testing", Order = 5)]
        public bool EnableManualSteppingMode
        {
            get => _enableManualSteppingMode;
            set
            {
                if (_enableManualSteppingMode != value)
                {
                    _enableManualSteppingMode = value;

                    if (value)
                    {
                        _observationBarInitialized = false;
                        Log("Manual stepping mode enabled");
                    }
                    else
                    {
                        Log("Manual stepping mode disabled");
                    }

                    RedrawChart();
                }
            }
        }

        [Display(Name = "Show Future Mask", GroupName = "Testing", Order = 6)]
        public bool ShowFutureMask
        {
            get => _showFutureMask;
            set
            {
                if (_showFutureMask != value)
                {
                    _showFutureMask = value;
                    RedrawChart();
                }
            }
        }

        [Display(Name = "Enable Debug Log", GroupName = "Debug", Order = 100)]
        public bool EnableDebugLog
        {
            get => _enableDebugLog;
            set => _enableDebugLog = value;
        }

        /// <summary>
        /// Manual trigger to show Control Panel
        /// Note: This is a clickable button in ATAS indicator settings
        /// </summary>
        [Display(Name = "📊 Show Control Panel", GroupName = "Debug", Order = 110)]
        public bool ShowControlPanel
        {
            get => false; // Always return false (button behavior)
            set
            {
                if (value) // When user clicks (sets to true)
                {
                    try
                    {
                        this.LogInfo("[ShowControlPanel] Opening Control Panel...");
                        UI.RemoteIndicatorControlPanel.Show();
                    }
                    catch (Exception ex)
                    {
                        this.LogError("[ShowControlPanel] Failed to open Control Panel", ex);
                    }
                }
            }
        }

        #endregion

        #region Private Fields (v4.1)

        // Communication layer proxy
        private IndicatorChannelProxy? _proxy;

        // Initialization state
        private bool _isInitialized = false;

        // Main thread synchronization context (for triggering redraw from background thread)
        private SynchronizationContext? _mainThreadContext;

        // Connection check (periodic)
        private DateTime _lastConnectionCheck = DateTime.MinValue;
        private const double ConnectionCheckIntervalSeconds = 5.0;

        // Request throttling - observation point based
        private int _lastObservationBar = -1;
        private DateTime _lastRequestTime = DateTime.MinValue;

        // Manual stepping mode state
        private int _observationBar = -1;
        private bool _observationBarInitialized = false;

        // Request retry mechanism
        private volatile int _pendingRequestBar = -1;
        private long _pendingRequestTickTime = -1;
        private long _detectedTimeBeforeRequest = 0;
        private int _attemptCount = 0;
        private System.Threading.Timer? _requestTimer;

        // IMonitorablePlugin Implementation Fields
        private long _indicatorRequests;
        private long _elementsReceived;
        private long _sendFailures;
        private long _receiveFailures;
        private string _lastError = "";
        private DateTime? _lastErrorTime;
        private DateTime? _connectedSince;
        private DateTime? _lastActiveTime;
        private string? _cachedSymbol;
        private TimeframeConverter.ProtoTimeframe? _cachedProtoTf;

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor - Initialize ATAS indicator properties
        /// CRITICAL: These properties MUST be set in constructor, not in OnInitialize()
        /// </summary>
        protected RemoteIndicatorBase() : base(true)
        {
            // ATAS rendering setup (REQUIRED for custom drawing indicators)
            EnableCustomDrawing = true;        // Enable custom rendering via OnRender()
            DrawAbovePrice = false;            // Draw on price chart (not above/below)
            DenyToChangePanel = true;          // Lock to current panel
            DataSeries[0].IsHidden = true;     // Hide default data series (we use custom drawing)

            // Subscribe to drawing events
            SubscribeToDrawingEvents(
                DrawingLayouts.Historical |
                DrawingLayouts.LatestBar |
                DrawingLayouts.Final
            );
        }

        #endregion

        #region Lifecycle

        protected override void OnInitialize()
        {
            base.OnInitialize();

            // Capture main thread SynchronizationContext for cross-thread RedrawChart
            _mainThreadContext = SynchronizationContext.Current;
            if (_mainThreadContext != null)
            {
                this.LogInfo($"[{IndicatorType}] SynchronizationContext captured: {_mainThreadContext.GetType().Name}");
            }
            else
            {
                this.LogInfo($"[{IndicatorType}] Warning: No SynchronizationContext available");
            }

            // v4.1: No longer create socket here
            // Initialization deferred to OnCalculate() when InstrumentInfo available

            // Register to Control Panel
            try
            {
                PluginRegistry.Register(this);
                this.LogInfo($"[{IndicatorType}] Registered to Control Panel");
            }
            catch (Exception ex)
            {
                this.LogError($"[{IndicatorType}] Failed to register to Control Panel", ex);
            }

            this.LogInfo($"[{IndicatorType}] v4.1 OnInitialize (lazy init in OnCalculate)");
        }

        protected override void OnDispose()
        {
            this.LogInfo($"[{IndicatorType}] OnDispose called");

            // Cleanup retry timer
            _requestTimer?.Dispose();
            _requestTimer = null;

            // Unregister from Control Panel
            try
            {
                PluginRegistry.Unregister(PluginId);
            }
            catch (Exception ex)
            {
                Log($"Failed to unregister: {ex.Message}");
            }

            CleanupProxy();

            base.OnDispose();
        }

        public override bool ProcessKeyDown(KeyEventArgs e)
        {
            if (!_enableManualSteppingMode)
                return false;

            // Fix for CS1955: Use the 'SystemKey' property directly instead of invoking it as a method
            if (e.Key == Key.Right) // Fix for CS0103: Ensure 'Keys' is resolved by adding the correct namespace
            {
                MoveObservationPoint(+1);
                return false;
            }

            if (e.Key == Key.Left) // Fix for CS0103: Ensure 'Keys' is resolved by adding the correct namespace
            {
                MoveObservationPoint(-1);
                return false;
            }
            return false;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // ==========================================================
            // Phase 1: Lazy Initialization
            // ==========================================================
            if (!_isInitialized)
            {
                // Check if we can initialize (InstrumentInfo/ChartInfo available)
                if (!CanInitialize())
                {
                    return; // Not ready yet, wait for next OnCalculate
                }

                try
                {
                    // Get symbol from ATAS API (main thread, safe)
                    string symbol = InstrumentInfo!.Instrument!;

                    // Get ChartType and TimeFrame from ATAS API (main thread, safe)
                    var chartType = ChartInfo!.ChartType;
                    string timeFrameStr = ChartInfo.TimeFrame?.ToString() ?? string.Empty;

                    // Convert to Proto format using TimeframeConverter
                    var protoTf = TimeframeConverter.ToProto(chartType, timeFrameStr);

                    // Cache for PluginId (CRITICAL: avoid ATAS API access from property getter)
                    _cachedSymbol = symbol;
                    _cachedProtoTf = protoTf;

                    // Create and start proxy (pass symbol/resolution/numUnits from main thread)
                    _proxy = new IndicatorChannelProxy(
                        _serviceHost,
                        _servicePort,
                        symbol,
                        protoTf.Resolution,
                        protoTf.NumUnits,
                        IndicatorType,
                        Log,
                        OnProxyDataUpdated  // Callback for auto-redraw when data arrives
                    );

                    _proxy.Start();
                    _isInitialized = true;

                    // Update connection status
                    UpdateConnectionStatus(true);

                    this.LogInfo($"[{IndicatorType}] ✓ Initialized (symbol={symbol}, chartType={chartType}, resolution={protoTf.Resolution}, numUnits={protoTf.NumUnits})");
                }
                catch (Exception ex)
                {
                    this.LogError($"[{IndicatorType}] Initialization error", ex);
                    return;
                }
            }

            // ==========================================================
            // Phase 2: Periodic Connection Check (every 5 seconds)
            // ==========================================================
            if (ShouldCheckConnection())
            {
                if (_proxy != null && !_proxy.IsConnected())
                {
                    Log("Connection lost, will reinitialize");

                    // Cleanup proxy
                    CleanupProxy();

                    // Mark uninitialized (will recreate on next OnCalculate)
                    _isInitialized = false;
                }
            }

            // Remote indicators don't perform local calculations
            // All computation is done on the remote service
            // Rendering is handled in OnRender()
        }

        #endregion

        #region Rendering

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            // Guard: Proxy not initialized yet
            if (_proxy == null)
            {
                return;
            }

            // ==========================================================
            // Phase 0: Initialize observation point (manual stepping mode)
            // ==========================================================
            if (_enableManualSteppingMode && !_observationBarInitialized)
            {
                _observationBar = ChartInfo.PriceChartContainer.LastVisibleBarNumber - 1;
                _lastObservationBar = -1;
                _observationBarInitialized = true;
                Log($"Observation point initialized to bar {_observationBar}");
            }

            // ==========================================================
            // Phase 1: Request with retry mechanism
            // ==========================================================
            int currentObs = GetCurrentObservationBar();

            if (currentObs != _lastObservationBar)
            {
                _lastObservationBar = currentObs;

                // Avoid duplicate requests
                if (currentObs == _pendingRequestBar)
                    return;

                // Cancel old timer if exists
                _requestTimer?.Dispose();

                // Prepare new request
                var candle = GetCandle(currentObs);
                if (candle == null)
                    return;

                _pendingRequestBar = currentObs;
                _pendingRequestTickTime = DateTimeHelper.ToUnixMs(candle.LastTime);

                // Record current detected_tick_time before sending request
                _detectedTimeBeforeRequest = _proxy?.GetCachedDetectedTickTime() ?? 0;

                // Start with attempt 0 (will send on first timer callback)
                _attemptCount = 0;

                Log($"Scheduled request for bar {currentObs} (will send in 100ms, candle time: {candle.Time:yyyy-MM-dd HH:mm:ss} {candle.LastTime:yyyy-MM-dd HH:mm:ss})");

                // Start timer: send first request in 100ms (give data push time)
                _requestTimer = new System.Threading.Timer(CheckAndRetry, null, 100, Timeout.Infinite);
            }

            // ==========================================================
            // Phase 2: Render cached elements
            // ==========================================================
            // Note: GetCachedElements() is thread-safe (locks internally, returns copy)
            var elements = _proxy.GetCachedElements();

            if (elements.Count > 0)
            {
                // Only log on first render or when element count changes
                // (Avoid log spam - OnRender called every tick with DrawingLayouts.Final)
                RenderElements(context, elements);
                //_proxy.ClearCache(); // Clear cache after rendering
            }

            // ==========================================================
            // Phase 3: Render future mask and observation marker (manual stepping mode)
            // ==========================================================
            if (_showFutureMask)
            {
                RenderFutureMask(context);
                RenderObservationMarker(context);
            }
        }

        #endregion

        #region Request Trigger

        /// <summary>
        /// Trigger async request - Get tick time from main thread, enqueue to proxy
        /// </summary>
        /// <param name="visibleBar">Current visible bar index</param>
        /// <remarks>
        /// Main thread: Get candle and tick time (ATAS API access)
        /// Non-blocking: RequestIndicator() enqueues, returns immediately
        /// </remarks>
        private void TriggerAsyncRequest(int visibleBar)
        {
            try
            {
                // Get reference candle (UI thread safe)
                var candle = GetCandle(visibleBar);
                if (candle == null)
                {
                    return;
                }

                // Get tick time (main thread) - ensure UTC
                long tickTimeMs = DateTimeHelper.ToUnixMs(candle.LastTime);

                // Enqueue request (non-blocking)
                _proxy?.RequestIndicator(tickTimeMs);
            }
            catch (Exception ex)
            {
                Log($"TriggerAsyncRequest error: {ex.Message}");
            }
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Cleanup proxy - Stop and dispose
        /// </summary>
        /// <remarks>
        /// Reusable cleanup logic for OnDispose, config change, reconnection
        /// </remarks>
        private void CleanupProxy()
        {
            if (_proxy != null)
            {
                try
                {
                    _proxy.Stop();
                    _proxy.Dispose();
                    _proxy = null;
                    Log("Proxy cleaned up");
                }
                catch (Exception ex)
                {
                    Log($"CleanupProxy error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Check if can initialize (InstrumentInfo/ChartInfo available and TimeFrame parseable)
        /// </summary>
        /// <returns>True if can initialize</returns>
        /// <remarks>
        /// v4.1 Enhancement: Also validates that TimeFrame can be successfully converted to Proto format
        /// This prevents initialization on unsupported chart types (Renko, Range, etc.)
        /// </remarks>
        private bool CanInitialize()
        {
            // 1. InstrumentInfo available
            if (InstrumentInfo == null || string.IsNullOrEmpty(InstrumentInfo.Instrument))
            {
                return false;
            }

            // 2. ChartInfo available
            if (ChartInfo == null)
            {
                return false;
            }

            // 3. CurrentBar > 0 (ATAS ready)
            if (CurrentBar <= 0)
            {
                return false;
            }

            // 4. TimeFrame can be parsed (v4.1 critical check)
            try
            {
                var chartType = ChartInfo.ChartType;
                var timeFrameStr = ChartInfo.TimeFrame?.ToString();

                if (string.IsNullOrEmpty(timeFrameStr))
                {
                    Log($"ChartInfo.TimeFrame is null or empty");
                    return false;
                }

                // 5. Verify chart type is supported
                if (!TimeframeConverter.IsSupportedChartType(chartType))
                {
                    Log($"Unsupported chart type: {chartType}. Supported: TimeFrame, Seconds, Volume, Tick");
                    return false;
                }

                // 6. Verify TimeFrame can be successfully converted
                var protoTf = TimeframeConverter.ToProto(chartType, timeFrameStr);

                // Success - all checks passed
                return true;
            }
            catch (Exception ex)
            {
                Log($"CanInitialize TimeFrame validation failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if should perform connection check
        /// </summary>
        /// <returns>True if should check</returns>
        private bool ShouldCheckConnection()
        {
            if ((DateTime.Now - _lastConnectionCheck).TotalSeconds >= ConnectionCheckIntervalSeconds)
            {
                _lastConnectionCheck = DateTime.Now;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Find bar index by tick time
        /// Uses binary search for efficiency
        /// </summary>
        protected int FindBarByTickTime(long tickTimeMs)
        {
            // Convert to UTC DateTime for comparison
            var targetTime = DateTimeHelper.FromUnixMs(tickTimeMs);

            int left = 0;
            int right = Math.Max(0, CurrentBar);

            int result = -1;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                var candle = GetCandle(mid);

                if (candle == null) break;

                // Check if target time is within this bar's range (ensure UTC for comparison)
                var candleStartUtc = DateTimeHelper.EnsureUtc(candle.Time);
                var candleEndUtc = DateTimeHelper.EnsureUtc(candle.LastTime);

                if (targetTime >= candleStartUtc && targetTime <= candleEndUtc)
                {
                    return mid;
                }

                if (targetTime < candleStartUtc)
                {
                    right = mid - 1;
                }
                else
                {
                    result = mid; // Keep track of closest bar
                    left = mid + 1;
                }
            }

            return result;
        }

        /// <summary>
        /// Render future data mask (manual stepping mode)
        /// </summary>
        private void RenderFutureMask(RenderContext context)
        {
            if (!_showFutureMask)
                return;

            int firstVisibleBar = ChartInfo.PriceChartContainer.FirstVisibleBarNumber;
            int lastVisibleBar = ChartInfo.PriceChartContainer.LastVisibleBarNumber;

            //int maskStartBar = _observationBar + 1;
            int maskStartBar = GetCurrentObservationBar() + 1;
            if (maskStartBar > lastVisibleBar)
            {
                maskStartBar = lastVisibleBar;
            }

            maskStartBar = Math.Max(maskStartBar, firstVisibleBar);

            int x1 = ChartInfo.GetXByBar(maskStartBar);
            int x2 = ChartInfo.GetXByBar(lastVisibleBar) + (int)ChartInfo.PriceChartContainer.BarsWidth;

            if (x2 <= x1)
            {
                return;
            }

            int y1 = 0;
            int y2 = ChartInfo.PriceChartContainer.Region.Height;

            var maskColor = System.Drawing.Color.FromArgb(255, 40, 40, 40);
            var rect = new System.Drawing.Rectangle(x1, y1, x2 - x1, y2 - y1);
            context.FillRectangle(maskColor, rect);
        }

        /// <summary>
        /// Render observation point marker line (manual stepping mode)
        /// </summary>
        private void RenderObservationMarker(RenderContext context)
        {
            int firstVisibleBar = ChartInfo.PriceChartContainer.FirstVisibleBarNumber;
            int lastVisibleBar = ChartInfo.PriceChartContainer.LastVisibleBarNumber;
            int currentObs = GetCurrentObservationBar();

            if (currentObs < firstVisibleBar || currentObs > lastVisibleBar)
            {
                return;
            }

            int markerX = ChartInfo.GetXByBar(currentObs) + (int)ChartInfo.PriceChartContainer.BarsWidth / 2;

            int y1 = 0;
            int y2 = ChartInfo.PriceChartContainer.Region.Height;

            var markerColor = System.Drawing.Color.FromArgb(255, 255, 165, 0);
            var markerPen = new OFT.Rendering.Tools.RenderPen(markerColor, 4);
            context.DrawLine(markerPen, markerX, y1, markerX, y2);
        }

        /// <summary>
        /// Move observation point (manual stepping mode)
        /// </summary>
        /// <param name="delta">+1 for forward, -1 for backward</param>
        private void MoveObservationPoint(int delta)
        {
            int newObs = _observationBar + delta;

            if (newObs < 0)
            {
                _observationBar = 0;
                Log("Already at the first bar");
                return;
            }

            if (newObs >= CurrentBar)
            {
                _observationBar = CurrentBar - 1;
                Log($"Already at the latest bar CurrentBar: {CurrentBar} LastVisibleBarNumber: {LastVisibleBarNumber} _observationBar: {_observationBar}");
                return;
            }

            _observationBar = newObs;
            Log($"Observation point moved to bar {_observationBar}");

            RedrawChart();
        }

        /// <summary>
        /// Get current observation bar index
        /// Returns the bar that represents "current observable point" for triggering requests
        /// </summary>
        /// <returns>Observation bar index</returns>
        /// <remarks>
        /// Live mode: LastVisibleBarNumber (user's current focus position)
        /// Manual stepping mode: _observationBar (user-controlled position)
        /// </remarks>
        private int GetCurrentObservationBar()
        {
            if (_enableManualSteppingMode)
            {
                return _observationBar;
            }
            else
            {
                return LastVisibleBarNumber - 1;
            }
        }

        /// <summary>
        /// Callback invoked when proxy receives new data (called from worker thread)
        /// Uses SynchronizationContext to post RedrawChart to main thread
        /// </summary>
        private void OnProxyDataUpdated()
        {
            // Post to main thread via SynchronizationContext
            _mainThreadContext?.Post(_ =>
            {
                RedrawChart();
                Log("Data updated, triggered redraw");
            }, null);
        }

        /// <summary>
        /// Check if data updated and retry if needed (Timer callback)
        /// </summary>
        /// <param name="state">Timer state (unused)</param>
        private void CheckAndRetry(object? state)
        {
            try
            {
                // First call: send initial request (after 100ms delay)
                if (_attemptCount == 0)
                {
                    _attemptCount = 1;
                    _proxy?.RequestIndicator(_pendingRequestTickTime);

                    Log($"Request sent for bar {_pendingRequestBar} (attempt 1/3)");

                    // Schedule check in 300ms
                    _requestTimer?.Change(300, Timeout.Infinite);
                    return;
                }

                // Subsequent calls: check result and retry if needed
                long detectedTimeNow = _proxy?.GetCachedDetectedTickTime() ?? 0;

                if (detectedTimeNow != _detectedTimeBeforeRequest)
                {
                    // Success - data updated
                    Log($"Request succeeded for bar {_pendingRequestBar} (attempt {_attemptCount}/3)");
                    _pendingRequestBar = -1;
                    return;
                }

                // Data not updated yet
                if (_attemptCount < 3)
                {
                    // Retry: send request again
                    _attemptCount++;
                    _proxy?.RequestIndicator(_pendingRequestTickTime);

                    Log($"Request retry for bar {_pendingRequestBar} (attempt {_attemptCount}/3)");

                    // Schedule next check in 300ms
                    _requestTimer?.Change(300, Timeout.Infinite);
                }
                else
                {
                    // Give up after 3 attempts
                    Log($"Request failed for bar {_pendingRequestBar} after 3 attempts");
                    _pendingRequestBar = -1;
                }
            }
            catch (Exception ex)
            {
                Log($"CheckAndRetry error: {ex.Message}");
            }
        }

        private void Log(string message)
        {
            if (_enableDebugLog)
            {
                this.LogInfo($"[{IndicatorType}] {message}");
            }
        }

        #endregion

        #region IMonitorablePlugin Implementation

        /// <summary>
        /// Plugin unique ID
        /// Format: RemoteIndicator_IndicatorType_Symbol_ChartType_Timeframe
        /// Example: "RemoteIndicator_extreme_price_ES_TIMEFRAME_M5"
        /// </summary>
        public string PluginId
        {
            get
            {
                if (string.IsNullOrEmpty(_cachedSymbol) || !_cachedProtoTf.HasValue)
                    return $"RemoteIndicator_{IndicatorType}_Unknown";

                var symbol = _cachedSymbol;
                var chartType = ChartInfo?.ChartType ?? "Unknown";
                var resolution = _cachedProtoTf.Value.Resolution;
                var numUnits = _cachedProtoTf.Value.NumUnits;

                return $"RemoteIndicator_{IndicatorType}_{symbol}_{chartType}_{resolution}{numUnits}";
            }
        }

        /// <summary>
        /// Display name for UI
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(_cachedSymbol) || !_cachedProtoTf.HasValue)
                    return $"{IndicatorType} (initializing...)";

                var symbol = _cachedSymbol;
                var resolution = _cachedProtoTf.Value.Resolution;
                var numUnits = _cachedProtoTf.Value.NumUnits;

                return $"{IndicatorType} ({symbol}/{resolution}{numUnits})";
            }
        }

        /// <summary>
        /// Plugin type
        /// </summary>
        public PluginType Type => PluginType.RemoteIndicator;

        /// <summary>
        /// Is connected to Service
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return _proxy?.IsConnected() == true;
            }
        }

        /// <summary>
        /// Connection established time
        /// </summary>
        public DateTime? ConnectedSince
        {
            get => _connectedSince;
            private set => _connectedSince = value;
        }

        /// <summary>
        /// Last active time
        /// </summary>
        public DateTime? LastActiveTime
        {
            get => _lastActiveTime;
            private set => _lastActiveTime = value;
        }

        /// <summary>
        /// Get statistics snapshot
        /// </summary>
        public PluginStatistics GetStatistics()
        {
            return new PluginStatistics
            {
                MessagesSent = Interlocked.Read(ref _indicatorRequests),
                MessagesReceived = Interlocked.Read(ref _elementsReceived),
                SendFailures = Interlocked.Read(ref _sendFailures),
                ReceiveFailures = Interlocked.Read(ref _receiveFailures),
                IndicatorRequests = Interlocked.Read(ref _indicatorRequests),
                ElementsReceived = Interlocked.Read(ref _elementsReceived),
                LastError = _lastError,
                LastErrorTime = _lastErrorTime
            };
        }

        /// <summary>
        /// Status changed event
        /// </summary>
        public event Action<IMonitorablePlugin, string> StatusChanged;

        /// <summary>
        /// Execute custom action
        /// </summary>
        public string ExecuteAction(string actionType, Dictionary<string, object> parameters)
        {
            try
            {
                switch (actionType)
                {
                    case "Reconnect":
                        CleanupProxy();
                        _isInitialized = false;
                        _connectedSince = null;
                        StatusChanged?.Invoke(this, "Reconnect triggered");
                        return "Reconnection triggered";

                    case "ClearStats":
                        Interlocked.Exchange(ref _indicatorRequests, 0);
                        Interlocked.Exchange(ref _elementsReceived, 0);
                        Interlocked.Exchange(ref _sendFailures, 0);
                        Interlocked.Exchange(ref _receiveFailures, 0);
                        _lastError = "";
                        _lastErrorTime = null;
                        return "Statistics cleared";

                    case "ShowControlPanel":
                        RemoteIndicatorControlPanel.Show();
                        return "Control panel opened";

                    default:
                        return $"Unknown action: {actionType}";
                }
            }
            catch (Exception ex)
            {
                return $"Action failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Update connection status and trigger event
        /// </summary>
        private void UpdateConnectionStatus(bool connected)
        {
            bool wasConnected = ConnectedSince.HasValue;

            if (connected && !wasConnected)
            {
                ConnectedSince = DateTime.Now;
                LastActiveTime = DateTime.Now;
                StatusChanged?.Invoke(this, "Connected to Service");
            }
            else if (!connected && wasConnected)
            {
                ConnectedSince = null;
                StatusChanged?.Invoke(this, "Disconnected from Service");
            }

            if (connected)
            {
                LastActiveTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Increment indicator requests counter
        /// </summary>
        protected void IncrementIndicatorRequests()
        {
            Interlocked.Increment(ref _indicatorRequests);
            LastActiveTime = DateTime.Now;
        }

        /// <summary>
        /// Increment elements received counter
        /// </summary>
        protected void IncrementElementsReceived(int count)
        {
            Interlocked.Add(ref _elementsReceived, count);
            LastActiveTime = DateTime.Now;
        }

        /// <summary>
        /// Record error
        /// </summary>
        protected void RecordError(string error)
        {
            _lastError = error;
            _lastErrorTime = DateTime.Now;
            Interlocked.Increment(ref _sendFailures);
            StatusChanged?.Invoke(this, $"Error: {error}");
        }

        #endregion
    }
}
