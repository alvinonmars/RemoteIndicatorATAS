using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ATAS.Indicators;
using ATAS.DataFeedsCore;
using Google.Protobuf;
using OFT.Attributes;
using IndicatorService;
using RemoteIndicator.ATAS.Communication;
using RemoteIndicator.ATAS.Utilities;
using RemoteIndicator.ATAS.Monitoring;
using RemoteIndicator.ATAS.UI;
using Utils.Common.Logging;

namespace RemoteIndicator.ATAS.DataTerminal
{
    /// <summary>
    /// Data provider plugin for Remote Indicator Service v4.1
    ///
    /// v4.1 Architecture:
    /// - Implements IBarCacheProvider interface (provides QueryBars to DataPuller)
    /// - Uses DataPusher component (Channel B - proactive push of realtime bars)
    /// - Uses DataPuller component (Channel C - reactive response to DataRequest)
    /// - Delayed initialization: Wait for InstrumentInfo/ChartInfo in OnCalculate
    /// - TimeFrame conversion: Use TimeframeConverter.ToProto() on main thread
    /// - Timeframe validation: Verify DataRequest matches current chart
    ///
    /// Channel Architecture:
    ///   - Channel B (PUSH via DataPusher): Realtime completed bars → Service cache
    ///   - Channel C (REP via DataPuller): Service requests → QueryBars() → historical data
    ///
    /// Lifecycle:
    /// 1. OnCalculate: Check CanInitialize() (InstrumentInfo/ChartInfo/TimeFrame ready)
    /// 2. OnCalculate: Convert ATAS TimeFrame → Proto (resolution/numUnits)
    /// 3. OnCalculate: Create DataPusher/DataPuller with proto format
    /// 4. OnCalculate: Detect completed bar → cache + push if realtime
    /// 5. DataPuller callback: QueryBars() → validate timeframe → return cached bars
    /// </summary>
    [Category("Community")]
    [DisplayName("DataTerminal v4.1")]
    public class DataTerminal : Indicator, IBarCacheProvider, IMonitorablePlugin
    {
        #region Configuration

        private string _serviceHost = "119.247.58.107";
        private bool _enableDebugLog = true;
        private int _maxCacheSize = 100000;

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
                        CleanupComponents();
                        _isInitialized = false;
                    }
                }
            }
        }

        [Display(Name = "Max Cache Size", GroupName = "Data", Order = 20)]
        public int MaxCacheSize
        {
            get => _maxCacheSize;
            set => _maxCacheSize = Math.Max(1000, Math.Min(1000000, value));
        }

        [Display(Name = "Enable Debug Log", GroupName = "Debug", Order = 100)]
        public bool EnableDebugLog
        {
            get => _enableDebugLog;
            set => _enableDebugLog = value;
        }

        #endregion

        #region Cached Bar Structure

        private struct CachedBar
        {
            public DateTime OpenTime;
            public DateTime CloseTime;
            public double Open;
            public double High;
            public double Low;
            public double Close;
            public double Volume;
            public List<PriceLevel> Levels;
        }

        #endregion

        #region Private Fields

        // Cache
        private SortedDictionary<DateTime, CachedBar> _barCache = new();
        private readonly object _cacheLock = new();

        // Realtime Boundary
        private DateTime? _realtimeBoundary = null;
        private readonly object _boundaryLock = new();

        // Bar Completion Detection
        private int _lastProcessedBar = 0;

        // v4.1 Communication Components (replaces direct socket management)
        private DataPusher? _pusher;
        private DataPuller? _puller;

        // v4.1 Initialization State
        private bool _isInitialized = false;
        private DateTime _lastConnectionCheck = DateTime.MinValue;
        private const double ConnectionCheckIntervalSeconds = 5.0;

        // v4.1 Cached data from main thread (avoid ATAS API access in background thread)
        private string? _cachedSymbol = null;
        private TimeframeConverter.ProtoTimeframe? _cachedProtoTf = null;

        // IMonitorablePlugin Implementation Fields
        private long _barsPushed;
        private long _barsQueried;
        private long _sendFailures;
        private long _receiveFailures;
        private string _lastError = "";
        private DateTime? _lastErrorTime;
        private DateTime? _connectedSince;
        private DateTime? _lastActiveTime;

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor - Initialize ATAS indicator properties for data provider mode
        /// </summary>
        public DataTerminal() : base(true)
        {
            // Data provider setup (no custom drawing, just data processing)
            DenyToChangePanel = true;          // Lock to current panel
            DataSeries[0].IsHidden = true;     // Hide default data series (we don't draw anything)

            // Note: No need for EnableCustomDrawing or SubscribeToDrawingEvents
            // because DataTerminal is a data provider, not a rendering indicator
        }

        #endregion

        #region Lifecycle

        protected override void OnInitialize()
        {
            base.OnInitialize();

            // v4.1: No longer create communication components here
            // Initialization deferred to OnCalculate() when InstrumentInfo/ChartInfo available

            // Reset boundary state
            lock (_boundaryLock)
            {
                _realtimeBoundary = null;
            }

            // Reset bar tracking
            _lastProcessedBar = -1;

            // Register to Control Panel
            try
            {
                PluginRegistry.Register(this);
                this.LogInfo("[DataTerminal] Registered to Control Panel");
            }
            catch (Exception ex)
            {
                this.LogError("[DataTerminal] Failed to register to Control Panel", ex);
            }

            this.LogInfo("[DataTerminal] v4.1 OnInitialize (lazy init in OnCalculate)");
        }

        protected override void OnDispose()
        {
            try
            {
                // Unregister from Control Panel
                PluginRegistry.Unregister(PluginId);

                // v4.1: Cleanup communication components
                CleanupComponents();

                Log("DataTerminal disposed");
            }
            catch (Exception ex)
            {
                Log($"Dispose error: {ex}");
            }

            base.OnDispose();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            try
            {
                // ==========================================================
                // Phase 1: Lazy Initialization (v4.1)
                // ==========================================================
                if (!_isInitialized)
                {
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

                        // Cache for QueryBars (CRITICAL: avoid ATAS API access in background thread)
                        _cachedSymbol = symbol;
                        _cachedProtoTf = protoTf;

                        // Create DataPusher (Channel B - port 5556)
                        _pusher = new DataPusher(
                            _serviceHost,
                            5556,
                            symbol,
                            protoTf.Resolution,
                            protoTf.NumUnits,
                            Log
                        );

                        // Create DataPuller (Channel C - port 5557)
                        _puller = new DataPuller(
                            _serviceHost,
                            5557,
                            this, // Implement IBarCacheProvider
                            Log
                        );

                        // Start both components
                        _pusher.Start();
                        _puller.Start();

                        _isInitialized = true;

                        this.LogInfo($"[DataTerminal] ✓ Initialized (symbol={symbol}, chartType={chartType}, resolution={protoTf.Resolution}, numUnits={protoTf.NumUnits})");
                    }
                    catch (Exception ex)
                    {
                        this.LogError("[DataTerminal] Initialization error", ex);
                        return;
                    }
                }

                // ==========================================================
                // Phase 2: Periodic Connection Check (v4.1)
                // ==========================================================
                if (ShouldCheckConnection())
                {
                    if ((_pusher != null && !_pusher.IsConnected()) ||
                        (_puller != null && !_puller.IsConnected()))
                    {
                        Log("Connection lost, will reinitialize");
                        CleanupComponents();
                        _isInitialized = false;
                        return;
                    }
                }

                lock (_boundaryLock)
                {
                    if (_realtimeBoundary == null)
                        _realtimeBoundary = UtcTime;
                }

                // ==========================================================
                // Phase 3: Bar Completion Detection (existing logic)
                // ==========================================================
                if (bar <= _lastProcessedBar)
                {
                    // Current bar still forming, skip processing
                    return;
                }

                // Bar index increased: previous bar is now complete
                int completedBarIndex = bar - 1;
                _lastProcessedBar = bar;

                // Skip if no completed bar (first call with bar=0)
                if (completedBarIndex < 0)
                {
                    return;
                }

                // ==========================================================
                // Phase 4: Collect and Cache Completed Bar
                // ==========================================================
                var candle = GetCandle(completedBarIndex);
                if (candle == null) return;

                // CRITICAL: ATAS returns UTC but Kind=Unspecified, use EnsureUtc to fix
                var closeTime = DateTimeHelper.EnsureUtc(candle.LastTime);

                // Build cached bar
                var cachedBar = new CachedBar
                {
                    OpenTime = DateTimeHelper.EnsureUtc(candle.Time),
                    CloseTime = closeTime,
                    Open = (double)candle.Open,
                    High = (double)candle.High,
                    Low = (double)candle.Low,
                    Close = (double)candle.Close,
                    Volume = (double)candle.Volume,
                    Levels = new List<PriceLevel>()
                };

                // Collect footprint data
                var priceLevels = candle.GetAllPriceLevels();
                foreach (var level in priceLevels)
                {
                    cachedBar.Levels.Add(new PriceLevel
                    {
                        Price = (double)level.Price,
                        BidVol = (double)level.Bid,
                        AskVol = (double)level.Ask
                    });
                }

                // Update cache
                lock (_cacheLock)
                {
                    _barCache[closeTime] = cachedBar;

                    // FIFO eviction if cache exceeds limit
                    while (_barCache.Count > _maxCacheSize)
                    {
                        var oldestKey = _barCache.Keys.First();
                        _barCache.Remove(oldestKey);
                    }
                }

                // ==========================================================
                // Phase 5: Push Realtime Bar (if past boundary)
                // ==========================================================
                bool shouldPush = false;
                lock (_boundaryLock)
                {
                    if (_realtimeBoundary == null)
                    {
                        // Historical replay in progress, only cache
                        return;
                    }

                    if (closeTime <= _realtimeBoundary.Value)
                    {
                        // Historical bar, only cache
                        return;
                    }

                    // Realtime completed bar, should push
                    shouldPush = true;
                }

                if (shouldPush && _pusher != null && _cachedProtoTf.HasValue)
                {
                    // Safety check: InstrumentInfo must be available
                    // (Should always be true since _isInitialized, but defensive check)
                    string symbol = InstrumentInfo?.Instrument;
                    if (string.IsNullOrEmpty(symbol))
                    {
                        Log("ERROR: Cannot push bar, symbol unavailable");
                        return; // Critical: don't push invalid data
                    }

                    // Build BarData protobuf for push (v4.1: using cached proto timeframe)
                    var barData = new BarData
                    {
                        Symbol = symbol,
                        Resolution = _cachedProtoTf.Value.Resolution,
                        NumUnits = _cachedProtoTf.Value.NumUnits,
                        OpenTimeMs = DateTimeHelper.ToUnixMs(cachedBar.OpenTime),
                        CloseTimeMs = DateTimeHelper.ToUnixMs(cachedBar.CloseTime),
                        Open = cachedBar.Open,
                        High = cachedBar.High,
                        Low = cachedBar.Low,
                        Close = cachedBar.Close,
                        Volume = cachedBar.Volume
                    };

                    // Add footprint levels
                    barData.Levels.AddRange(cachedBar.Levels);

                    // v4.1: Use DataPusher component (instead of direct queue)
                    _pusher.EnqueueBar(barData);
                    IncrementBarsPushed();
                }
            }
            catch (Exception ex)
            {
                Log($"OnCalculate error: {ex.Message}");
                RecordError(ex.Message);
            }
        }
        protected override void OnNewTrade(MarketDataArg trade)
        {
            try
            {
                lock (_boundaryLock)
                {
                    if (_realtimeBoundary == null)
                    {
                        // First real tick detected - ensure UTC
                        _realtimeBoundary = DateTimeHelper.EnsureUtc(trade.Time);
                        Log($"✓ Realtime boundary established: {_realtimeBoundary.Value:yyyy-MM-dd HH:mm:ss} UTC");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"OnNewTrade error: {ex.Message}");
            }

            base.OnNewTrade(trade);
        }

        #endregion

        #region IBarCacheProvider Implementation (v4.1)

        /// <summary>
        /// Query bars from cache - Called by DataPuller component (background thread)
        /// </summary>
        /// <param name="request">DataRequest from Service</param>
        /// <returns>DataResponse with cached bars</returns>
        /// <remarks>
        /// CRITICAL: This method runs in background thread (DataPuller.RepLoop)
        /// MUST NOT access ATAS API (InstrumentInfo/ChartInfo) - use cached data only
        /// </remarks>
        public DataResponse QueryBars(DataRequest request)
        {
            var response = new DataResponse
            {
                RequestId = request.RequestId,
                Symbol = request.Symbol ?? ""
            };

            try
            {
                // CRITICAL: Use cached data, NOT ATAS API (background thread safety)
                if (string.IsNullOrEmpty(_cachedSymbol) || !_cachedProtoTf.HasValue)
                {
                    response.DebugInfo = "DataTerminal not initialized";
                    Log("QueryBars rejected: not initialized");
                    return response;
                }

                // v4.1: Validate symbol match
                if (!string.IsNullOrEmpty(request.Symbol) && request.Symbol != _cachedSymbol)
                {
                    response.DebugInfo = $"Symbol mismatch: requested {request.Symbol}, chart shows {_cachedSymbol}";
                    Log($"QueryBars rejected: {response.DebugInfo}");
                    return response;
                }

                // v4.1: Validate timeframe match (use cached proto timeframe)
                if (!string.IsNullOrEmpty(request.Resolution))
                {
                    if (request.Resolution != _cachedProtoTf.Value.Resolution ||
                        request.NumUnits != _cachedProtoTf.Value.NumUnits)
                    {
                        response.DebugInfo = $"Timeframe mismatch: Chart={_cachedProtoTf.Value.Resolution}({_cachedProtoTf.Value.NumUnits}), Request={request.Resolution}({request.NumUnits})";
                        Log($"QueryBars rejected: {response.DebugInfo}");
                        return response;
                    }
                }

                // Convert timestamps using helper (already returns UtcDateTime)
                var startTime = DateTimeHelper.FromUnixMs(request.StartTickTimeMs);
                var endTime = DateTimeHelper.FromUnixMs(request.EndTickTimeMs);

                // Query cache for bars in range
                lock (_cacheLock)
                {
                    var barsInRange = _barCache
                        .Where(kv => kv.Key >= startTime && kv.Key <= endTime)
                        .OrderBy(kv => kv.Key)
                        .Select(kv => kv.Value);

                    foreach (var cachedBar in barsInRange)
                    {
                        // Use cached symbol/timeframe (safe for background thread)
                        var barData = new BarData
                        {
                            Symbol = _cachedSymbol,
                            Resolution = _cachedProtoTf.Value.Resolution,
                            NumUnits = _cachedProtoTf.Value.NumUnits,
                            OpenTimeMs = DateTimeHelper.ToUnixMs(cachedBar.OpenTime),
                            CloseTimeMs = DateTimeHelper.ToUnixMs(cachedBar.CloseTime),
                            Open = cachedBar.Open,
                            High = cachedBar.High,
                            Low = cachedBar.Low,
                            Close = cachedBar.Close,
                            Volume = cachedBar.Volume
                        };

                        barData.Levels.AddRange(cachedBar.Levels);
                        response.Bars.Add(barData);
                    }
                }

                response.BarsCollected = response.Bars.Count;
                Log($"QueryBars: returned {response.BarsCollected} bars [{startTime:HH:mm:ss} - {endTime:HH:mm:ss}]");
                IncrementBarsQueried(response.BarsCollected);
            }
            catch (Exception ex)
            {
                response.DebugInfo = $"Cache query error: {ex.Message}";
                Log($"QueryBars error: {ex}");
                RecordError(ex.Message);
            }

            return response;
        }

        #endregion

        #region Utilities (v4.1)

        /// <summary>
        /// Check if can initialize (InstrumentInfo/ChartInfo available and TimeFrame parseable)
        /// </summary>
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

            // 4. TimeFrame can be parsed
            try
            {
                var chartType = ChartInfo.ChartType;
                var timeFrameStr = ChartInfo.TimeFrame?.ToString();

                if (string.IsNullOrEmpty(timeFrameStr))
                {
                    Log("ChartInfo.TimeFrame is null or empty");
                    return false;
                }

                // 5. Verify chart type is supported
                if (!TimeframeConverter.IsSupportedChartType(chartType))
                {
                    Log($"Unsupported chart type: {chartType}");
                    return false;
                }

                // 6. Verify TimeFrame can be successfully converted
                var protoTf = TimeframeConverter.ToProto(chartType, timeFrameStr);

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
        /// Cleanup communication components - Stop and dispose
        /// </summary>
        private void CleanupComponents()
        {
            try
            {
                if (_pusher != null)
                {
                    _pusher.Stop();
                    _pusher.Dispose();
                    _pusher = null;
                }

                if (_puller != null)
                {
                    _puller.Stop();
                    _puller.Dispose();
                    _puller = null;
                }

                Log("Communication components cleaned up");
            }
            catch (Exception ex)
            {
                Log($"CleanupComponents error: {ex.Message}");
            }
        }

        private void Log(string message)
        {
            if (_enableDebugLog)
            {
                this.LogInfo($"[DataTerminal] {message}");
            }
        }

        #endregion

        #region IMonitorablePlugin Implementation

        /// <summary>
        /// Plugin unique ID
        /// Format: DataTerminal_Symbol_ChartType_Timeframe
        /// Example: "DataTerminal_ES_TIMEFRAME_M5"
        /// </summary>
        public string PluginId
        {
            get
            {
                if (InstrumentInfo == null || ChartInfo == null || !_cachedProtoTf.HasValue)
                    return "DataTerminal_Unknown";

                var symbol = InstrumentInfo.Instrument ?? "Unknown";
                var chartType = ChartInfo.ChartType ?? "Unknown";
                var resolution = _cachedProtoTf.Value.Resolution;
                var numUnits = _cachedProtoTf.Value.NumUnits;

                return $"DataTerminal_{symbol}_{chartType}_{resolution}{numUnits}";
            }
        }

        /// <summary>
        /// Display name for UI
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (InstrumentInfo == null || !_cachedProtoTf.HasValue)
                    return "Data Terminal (initializing...)";

                var symbol = InstrumentInfo.Instrument ?? "?";
                var resolution = _cachedProtoTf.Value.Resolution;
                var numUnits = _cachedProtoTf.Value.NumUnits;

                return $"Data Terminal ({symbol}/{resolution}{numUnits})";
            }
        }

        /// <summary>
        /// Plugin type
        /// </summary>
        public PluginType Type => PluginType.DataTerminal;

        /// <summary>
        /// Is connected to Service
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return _pusher?.IsConnected() == true && _puller?.IsConnected() == true;
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
                MessagesSent = Interlocked.Read(ref _barsPushed),
                MessagesReceived = Interlocked.Read(ref _barsQueried),
                SendFailures = Interlocked.Read(ref _sendFailures),
                ReceiveFailures = Interlocked.Read(ref _receiveFailures),
                BarsPushed = Interlocked.Read(ref _barsPushed),
                BarsQueried = Interlocked.Read(ref _barsQueried),
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
                        CleanupComponents();
                        _isInitialized = false;
                        _connectedSince = null;
                        StatusChanged?.Invoke(this, "Reconnect triggered");
                        return "Reconnection triggered";

                    case "ClearStats":
                        Interlocked.Exchange(ref _barsPushed, 0);
                        Interlocked.Exchange(ref _barsQueried, 0);
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
        /// Increment bars pushed counter
        /// </summary>
        private void IncrementBarsPushed()
        {
            Interlocked.Increment(ref _barsPushed);
            LastActiveTime = DateTime.Now;
        }

        /// <summary>
        /// Increment bars queried counter
        /// </summary>
        private void IncrementBarsQueried(int count)
        {
            Interlocked.Add(ref _barsQueried, count);
            LastActiveTime = DateTime.Now;
        }

        /// <summary>
        /// Record error
        /// </summary>
        private void RecordError(string error)
        {
            _lastError = error;
            _lastErrorTime = DateTime.Now;
            Interlocked.Increment(ref _sendFailures);
            StatusChanged?.Invoke(this, $"Error: {error}");
        }

        #endregion
    }
}
