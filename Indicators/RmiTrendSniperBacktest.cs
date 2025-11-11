using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;
using ATAS.Indicators;
using ATAS.Indicators.Technical;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

[DisplayName("RMI Trend Sniper Backtest")]
[Description("Backtesting indicator for RMI Trend Sniper with optimized logic")]
public class RmiTrendSniperBacktest : Indicator
{
    #region Private fields
    private readonly ValueDataSeries _longEntries;
    private readonly ValueDataSeries _shortEntries;
    private readonly ValueDataSeries _longExits;
    private readonly ValueDataSeries _shortExits;

    // RMI calculation fields
    private readonly ValueDataSeries _up;
    private readonly ValueDataSeries _down;
    private readonly ValueDataSeries _rsi;
    private readonly ValueDataSeries _posMf;
    private readonly ValueDataSeries _negMf;
    private readonly ValueDataSeries _mf;
    private readonly ValueDataSeries _rsiMfi;
    private readonly ValueDataSeries _ema5;
    private readonly ValueDataSeries _barRange;
    private readonly ValueDataSeries _atr;
    private readonly ValueDataSeries _minVal;
    private readonly ValueDataSeries _band;
    private readonly ValueDataSeries _rwma;
    private readonly ValueDataSeries _positiveSeries;
    private readonly ValueDataSeries _rwmaSeries;
    private readonly ValueDataSeries _min;
    private readonly ValueDataSeries _max;

    // Trade tracking
    private readonly List<Trade> _trades = new List<Trade>();
    private Trade _currentTrade = null;

    // Signal tracking
    private int _lastBullishSignalBar = -1;
    private int _lastBearishSignalBar = -1;
    private int _bullishConfirmationCount = 0;
    private int _bearishConfirmationCount = 0;

    // Trend segment tracking
    private int _currentTrendSegment = 0;
    private bool _tradeOpenedInCurrentSegment = false;

    // Fast entry tracking
    private bool _fastEntryPending = false;
    private bool _fastEntryIsLong = false;
    private int _fastEntryBar = -1;

    // V-Reversal tracking
    private int _vReversalSignalBar = -1;
    private bool _vReversalIsLong = false;
    private bool _vReversalConfirmed = false;
    private decimal _vReversalExtreme = 0;  // RMI extreme value (not price)
    private int _vReversalExtremeBar = -1;
    private int _lastVReversalTrendState = 0;
    private readonly Dictionary<int, VReversalInfo> _vReversalSignals = new Dictionary<int, VReversalInfo>();

    // Statistics
    private int _totalTrades = 0;
    private int _longTrades = 0;
    private int _shortTrades = 0;
    private int _winningTrades = 0;
    private int _losingTrades = 0;
    private decimal _totalProfitTicks = 0;
    private decimal _totalProfitAmount = 0;
    private decimal _maxProfit = 0;
    private decimal _maxLoss = 0;
    private decimal _winRate = 0;
    private decimal _avgProfit = 0;
    private decimal _profitFactor = 0;
    private decimal _maxDrawdown = 0;
    private decimal _maxDrawdownAmount = 0;
    private decimal _sharpeRatio = 0;

    // Winning streak statistics
    private int _maxConsecutiveWins = 0;
    private decimal _maxConsecutiveWinsProfit = 0;
    private TimeSpan _maxConsecutiveWinsDuration = TimeSpan.Zero;

    // Losing streak statistics
    private int _maxConsecutiveLosses = 0;
    private decimal _maxConsecutiveLossesAmount = 0;
    private TimeSpan _maxConsecutiveLossesDuration = TimeSpan.Zero;

    // V-Reversal specific statistics
    private int _vReversalTrades = 0;
    private int _vReversalWinners = 0;
    private int _vReversalLosers = 0;
    private decimal _vReversalTotalProfit = 0;
    private decimal _vReversalWinRate = 0;

    private DateTime _startDateAll = DateTime.MaxValue;
    private DateTime _endDateAll = DateTime.MinValue;

    // Account settings
    private decimal _initialCapital = 5000m;
    private decimal _commission = 4.44m;
    private decimal _tickValue = 10m;
    private decimal _currentEquity = 0;
    private decimal _maxRiskPerTrade = 4000m;

    // Store TP/SL data for all trades
    private readonly List<TradeLinesData> _allTradeLines = new List<TradeLinesData>();

    private bool _isFirstCalculation = true;

    // Export status
    private string _exportStatusMessage = "";
    private DateTime _exportStatusTime = DateTime.MinValue;
    #endregion

    #region Public properties - Risk Management
    [DisplayName("Max Risk Per Trade")]
    [Description("Maximum risk amount per trade in USD (< $4000)")]
    [Category("Risk Management")]
    public decimal MaxRiskPerTrade
    {
        get => _maxRiskPerTrade;
        set
        {
            _maxRiskPerTrade = Math.Min(4000m, Math.Max(100m, value));
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }
    #endregion

    #region Enums
    public enum TakeProfitType
    {
        Signal,        // 信号止盈（波段策略）
        RiskReward,    // 盈亏比止盈（反向策略）
        VReversal      // V反交易模式
    }
    #endregion

    #region Trade classes
    private class Trade
    {
        public int EntryBar { get; set; }
        public int ExitBar { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public bool IsLong { get; set; }
        public decimal TicksDifference => IsLong ? (ExitPrice - EntryPrice) / 0.1m : (EntryPrice - ExitPrice) / 0.1m;
        public decimal Profit { get; set; }
        public bool IsWinner => Profit > 0;
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public bool IsActive => ExitBar == 0;
        public string ExitReason { get; set; }
        public TakeProfitType TpMode { get; set; }
        public decimal RiskAmount { get; set; }
        public int TrendSegment { get; set; }
        public bool IsVReversal { get; set; }
    }

    private class TradeLinesData
    {
        public int EntryBar { get; set; }
        public int ExitBar { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public bool IsLong { get; set; }
        public decimal EntryPrice { get; set; }
        public TakeProfitType TpMode { get; set; }
        public bool IsVReversal { get; set; }
    }

    private class VReversalInfo
    {
        public int SignalBar { get; set; }
        public bool IsLongSignal { get; set; }  // RMI变色方向（青色=true, 红色=false）
        public bool TradeIsLong { get; set; }   // 实际交易方向（反向）
        public decimal SignalRMI { get; set; }  // 信号时的RMI值
        public decimal ExtremeRMI { get; set; } // RMI极值
        public int ExtremeBar { get; set; }     // RMI极值bar位置
        public decimal CurrentRMI { get; set; } // 当前RMI值
        public bool Confirmed { get; set; }
        public bool Executed { get; set; }
        public int ConfirmBar { get; set; }
        public decimal RmiMovement { get; set; } // RMI波动幅度
        public decimal RetracementPct { get; set; } // 回撤比例
    }
    #endregion

    #region Public properties - RMI Settings
    private int _rmiLength = 14;
    [DisplayName("RMI Length")]
    [Description("RMI calculation length")]
    [Category("RMI Settings")]
    public int RmiLength
    {
        get => _rmiLength;
        set
        {
            _rmiLength = Math.Max(1, value);
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    private int _positiveAbove = 66;
    [DisplayName("Positive Above")]
    [Description("Positive momentum threshold")]
    [Category("RMI Settings")]
    public int PositiveAbove
    {
        get => _positiveAbove;
        set
        {
            _positiveAbove = value;
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    private int _negativeBelow = 30;
    [DisplayName("Negative Below")]
    [Description("Negative momentum threshold")]
    [Category("RMI Settings")]
    public int NegativeBelow
    {
        get => _negativeBelow;
        set
        {
            _negativeBelow = value;
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }
    #endregion

    #region Public properties - Entry Settings
    private int _waitBars = 2;
    [DisplayName("Wait Bars")]
    [Description("Number of bars to wait after signal for confirmation")]
    [Category("Entry Settings")]
    public int WaitBars
    {
        get => _waitBars;
        set
        {
            _waitBars = Math.Max(0, value);
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }
    #endregion

    #region Public properties - Exit Settings
    private TakeProfitType _tpType = TakeProfitType.Signal;
    [DisplayName("Take Profit Mode")]
    [Description("Signal=Wave, RiskReward=Counter, VReversal=V-Reversal Counter Trade")]
    [Category("Exit Settings")]
    public TakeProfitType TakeProfitMode
    {
        get => _tpType;
        set
        {
            _tpType = value;
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    private decimal _riskRewardRatio = 1.0m;
    [DisplayName("Risk:Reward Ratio")]
    [Description("Risk to reward ratio for RiskReward/VReversal mode (e.g., 1.0 = 1:1)")]
    [Category("Exit Settings")]
    public decimal RiskRewardRatio
    {
        get => _riskRewardRatio;
        set
        {
            _riskRewardRatio = Math.Max(0.5m, value);
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    private decimal _atrMultiplier = 1.0m;
    [DisplayName("ATR Multiplier for Stop Loss")]
    [Description("ATR multiplier for stop loss (default 1x)")]
    [Category("Exit Settings")]
    public decimal AtrMultiplier
    {
        get => _atrMultiplier;
        set
        {
            _atrMultiplier = value;
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    private int _vReversalLookback = 3;
    [DisplayName("V-Reversal Lookback Bars")]
    [Description("Number of bars to lookback for V-reversal validation (default 3)")]
    [Category("Exit Settings")]
    public int VReversalLookback
    {
        get => _vReversalLookback;
        set
        {
            _vReversalLookback = Math.Max(0, Math.Min(20, value));
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    private decimal _vReversalThreshold = 0.2m;
    [DisplayName("V-Reversal Retracement %")]
    [Description("Minimum retracement percentage to confirm V-reversal: 0.3=30%, 0.5=50%, 0.7=70% (default 0.2)")]
    [Category("Exit Settings")]
    public decimal VReversalThreshold
    {
        get => _vReversalThreshold;
        set
        {
            _vReversalThreshold = Math.Max(0.2m, Math.Min(0.8m, value));
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    // 新增V反模式参数
    private decimal _vReversalRmiMovement = 2.0m;
    [DisplayName("V-Reversal RMI Movement")]
    [Description("Minimum RMI movement points for V-reversal confirmation (default 2.0)")]
    [Category("Exit Settings")]
    public decimal VReversalRmiMovement
    {
        get => _vReversalRmiMovement;
        set
        {
            _vReversalRmiMovement = Math.Max(1.0m, Math.Min(20.0m, value));
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    private decimal _vReversalConfirmation = 0.1m;
    [DisplayName("V-Reversal Confirmation %")]
    [Description("Minimum confirmation percentage for V-reversal: 0.2=20%, 0.3=30%, 0.5=50% (default 0.1)")]
    [Category("Exit Settings")]
    public decimal VReversalConfirmation
    {
        get => _vReversalConfirmation;
        set
        {
            _vReversalConfirmation = Math.Max(0.1m, Math.Min(0.8m, value));
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    private int _maxHoldBars = 100000;
    [DisplayName("Max Hold Bars")]
    [Description("Maximum bars to hold position")]
    [Category("Exit Settings")]
    public int MaxHoldBars
    {
        get => _maxHoldBars;
        set
        {
            _maxHoldBars = value;
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }
    #endregion

    #region Public properties - Account Settings
    [DisplayName("Initial Capital")]
    [Description("Initial trading capital in USD")]
    [Category("Account Settings")]
    public decimal InitialCapital
    {
        get => _initialCapital;
        set
        {
            _initialCapital = value;
            _currentEquity = value;
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    [DisplayName("Commission")]
    [Description("Total commission per trade in USD (open + close)")]
    [Category("Account Settings")]
    public decimal Commission
    {
        get => _commission;
        set
        {
            _commission = value;
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    [DisplayName("Tick Value")]
    [Description("Value of one tick in USD")]
    [Category("Account Settings")]
    public decimal TickValue
    {
        get => _tickValue;
        set
        {
            _tickValue = value;
            _isFirstCalculation = true;
            RecalculateValues();
        }
    }

    private bool _exportButton = false;
    [DisplayName("📊 Export Results to CSV")]
    [Description("Click to export backtest results to CSV file")]
    [Category("Account Settings")]
    public bool ExportButton
    {
        get => _exportButton;
        set
        {
            if (value)
            {
                ExportBacktestResults();
                _exportButton = false;
            }
        }
    }
    #endregion

    #region Public properties - Visual Settings
    private System.Windows.Media.Color _longColor = System.Windows.Media.Colors.Green;
    [DisplayName("Long Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color LongColor
    {
        get => _longColor;
        set
        {
            _longColor = value;
            _longEntries.Color = value;
        }
    }

    private System.Windows.Media.Color _shortColor = System.Windows.Media.Colors.Red;
    [DisplayName("Short Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color ShortColor
    {
        get => _shortColor;
        set
        {
            _shortColor = value;
            _shortEntries.Color = value;
        }
    }

    private System.Windows.Media.Color _stopLossColor = System.Windows.Media.Colors.DarkRed;
    [DisplayName("Stop Loss Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color StopLossColor
    {
        get => _stopLossColor;
        set => _stopLossColor = value;
    }

    private System.Windows.Media.Color _takeProfitColor = System.Windows.Media.Colors.DarkGreen;
    [DisplayName("Take Profit Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color TakeProfitColor
    {
        get => _takeProfitColor;
        set => _takeProfitColor = value;
    }

    private bool _enableStopLossTakeProfitLines = true;
    [DisplayName("Enable SL/TP Lines")]
    [Category("Visual Settings")]
    public bool EnableStopLossTakeProfitLines
    {
        get => _enableStopLossTakeProfitLines;
        set => _enableStopLossTakeProfitLines = value;
    }

    private int _lineThickness = 2;
    [DisplayName("Line Thickness")]
    [Category("Visual Settings")]
    public int LineThickness
    {
        get => _lineThickness;
        set => _lineThickness = value;
    }

    private bool _showRangeMa = true;
    [DisplayName("Show Range MA")]
    [Category("Visual Settings")]
    public bool ShowRangeMa
    {
        get => _showRangeMa;
        set => _showRangeMa = value;
    }

    private System.Windows.Media.Color _bullRange = System.Windows.Media.Color.FromRgb(0, 150, 200);
    [DisplayName("Bull Range Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color BullRangeColor
    {
        get => _bullRange;
        set => _bullRange = value;
    }

    private System.Windows.Media.Color _bearRange = System.Windows.Media.Color.FromRgb(220, 60, 60);
    [DisplayName("Bear Range Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color BearRangeColor
    {
        get => _bearRange;
        set => _bearRange = value;
    }

    private System.Windows.Media.Color _bullCenterColor = System.Windows.Media.Color.FromRgb(0, 188, 212);
    [DisplayName("Bull Center Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color BullCenterColor
    {
        get => _bullCenterColor;
        set => _bullCenterColor = value;
    }

    private System.Windows.Media.Color _bearCenterColor = System.Windows.Media.Color.FromRgb(255, 82, 82);
    [DisplayName("Bear Center Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color BearCenterColor
    {
        get => _bearCenterColor;
        set => _bearCenterColor = value;
    }

    // V反模式专用颜色
    private System.Windows.Media.Color _vReversalLongColor = System.Windows.Media.Color.FromRgb(0, 200, 255);
    [DisplayName("V-Reversal Long Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color VReversalLongColor
    {
        get => _vReversalLongColor;
        set => _vReversalLongColor = value;
    }

    private System.Windows.Media.Color _vReversalShortColor = System.Windows.Media.Color.FromRgb(255, 100, 0);
    [DisplayName("V-Reversal Short Color")]
    [Category("Visual Settings")]
    public System.Windows.Media.Color VReversalShortColor
    {
        get => _vReversalShortColor;
        set => _vReversalShortColor = value;
    }
    #endregion

    #region Constructor
    public RmiTrendSniperBacktest()
    {
        DenyToChangePanel = true;
        EnableCustomDrawing = true;

        // Initialize RMI series
        _up = new ValueDataSeries("Up") { VisualType = VisualMode.Hide };
        _down = new ValueDataSeries("Down") { VisualType = VisualMode.Hide };
        _rsi = new ValueDataSeries("RSI") { VisualType = VisualMode.Hide };
        _posMf = new ValueDataSeries("PosMf") { VisualType = VisualMode.Hide };
        _negMf = new ValueDataSeries("NegMf") { VisualType = VisualMode.Hide };
        _mf = new ValueDataSeries("MF") { VisualType = VisualMode.Hide };
        _rsiMfi = new ValueDataSeries("RsiMfi") { VisualType = VisualMode.Hide };
        _ema5 = new ValueDataSeries("Ema5") { VisualType = VisualMode.Hide };
        _barRange = new ValueDataSeries("BarRange") { VisualType = VisualMode.Hide };
        _atr = new ValueDataSeries("Atr") { VisualType = VisualMode.Hide };
        _minVal = new ValueDataSeries("MinVal") { VisualType = VisualMode.Hide };
        _band = new ValueDataSeries("Band") { VisualType = VisualMode.Hide };
        _rwma = new ValueDataSeries("Rwma") { VisualType = VisualMode.Hide };
        _positiveSeries = new ValueDataSeries("Positive") { VisualType = VisualMode.Hide };
        _rwmaSeries = new ValueDataSeries("RwmaSeries") { VisualType = VisualMode.Hide };
        _min = new ValueDataSeries("Min") { VisualType = VisualMode.Hide };
        _max = new ValueDataSeries("Max") { VisualType = VisualMode.Hide };

        // Trading series
        _longEntries = new ValueDataSeries("Long Entries")
        {
            Color = System.Windows.Media.Colors.Green,
            VisualType = VisualMode.UpArrow,
            Width = 3,
            ShowZeroValue = false
        };

        _shortEntries = new ValueDataSeries("Short Entries")
        {
            Color = System.Windows.Media.Colors.OrangeRed,
            VisualType = VisualMode.DownArrow,
            Width = 3,
            ShowZeroValue = false
        };

        _longExits = new ValueDataSeries("Long Exits")
        {
            Color = System.Windows.Media.Colors.LightGreen,
            VisualType = VisualMode.UpArrow,
            Width = 2,
            ShowZeroValue = false
        };

        _shortExits = new ValueDataSeries("Short Exits")
        {
            Color = System.Windows.Media.Colors.Pink,
            VisualType = VisualMode.DownArrow,
            Width = 2,
            ShowZeroValue = false
        };

        DataSeries[0] = _longEntries;
        DataSeries.Add(_shortEntries);
        DataSeries.Add(_longExits);
        DataSeries.Add(_shortExits);

        _currentEquity = _initialCapital;
    }
    #endregion

    #region Main calculation
    private int _lastBar = -1;
    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < 1)
            return;

        if (_lastBar == -1)
            _lastBar = bar;

        if (_lastBar == bar || bar < 1.5 * _rmiLength)
            return;
            
        int closedBar = bar - 1;
        try
        {
            if (_isFirstCalculation)
            {
                ResetTradingData();
                _isFirstCalculation = false;
            }

            var currentCandle = GetCandle(closedBar);
            if (closedBar == 1)
                _startDateAll = currentCandle.Time;
            _endDateAll = currentCandle.Time;

            _longEntries[closedBar] = 0;
            _shortEntries[closedBar] = 0;
            _longExits[closedBar] = 0;
            _shortExits[closedBar] = 0;

            // Calculate RMI
            CalculateRmiTrendSniper(closedBar);

            // Process trading logic
            ProcessTradingLogic(closedBar);

            // Update statistics
            UpdateStatistics();
        }
        catch (Exception)
        {
            // Ignore errors
        }
    }

    private void ResetTradingData()
    {
        _trades.Clear();
        _allTradeLines.Clear();
        _currentTrade = null;
        _lastBullishSignalBar = -1;
        _lastBearishSignalBar = -1;
        _bullishConfirmationCount = 0;
        _bearishConfirmationCount = 0;
        _currentTrendSegment = 0;
        _tradeOpenedInCurrentSegment = false;
        _fastEntryPending = false;
        _fastEntryIsLong = false;
        _fastEntryBar = -1;

        // Reset V-Reversal tracking
        _vReversalSignalBar = -1;
        _vReversalIsLong = false;
        _vReversalConfirmed = false;
        _vReversalExtreme = 0;
        _vReversalExtremeBar = -1;
        _lastVReversalTrendState = 0;
        _vReversalSignals.Clear();

        _totalTrades = 0;
        _longTrades = 0;
        _shortTrades = 0;
        _winningTrades = 0;
        _losingTrades = 0;
        _totalProfitTicks = 0;
        _totalProfitAmount = 0;
        _maxProfit = 0;
        _maxLoss = 0;
        _currentEquity = _initialCapital;

        // Reset streak statistics
        _maxConsecutiveWins = 0;
        _maxConsecutiveWinsProfit = 0;
        _maxConsecutiveWinsDuration = TimeSpan.Zero;
        _maxConsecutiveLosses = 0;
        _maxConsecutiveLossesAmount = 0;
        _maxConsecutiveLossesDuration = TimeSpan.Zero;
        _maxDrawdownAmount = 0;

        // Reset V-Reversal statistics
        _vReversalTrades = 0;
        _vReversalWinners = 0;
        _vReversalLosers = 0;
        _vReversalTotalProfit = 0;
        _vReversalWinRate = 0;

        _exportStatusMessage = "";
        _exportStatusTime = DateTime.MinValue;
    }

    private void CalculateRmiTrendSniper(int bar)
    {
        var candle = GetCandle(bar);
        _barRange[bar] = candle.High - candle.Low;

        decimal change = bar > 0 ? candle.Close - GetCandle(bar - 1).Close : 0;
        decimal upVal = Math.Max(change, 0);
        decimal downVal = Math.Max(-change, 0);

        _up[bar] = bar > 0 ? (upVal + (_rmiLength - 1) * _up[bar - 1]) / _rmiLength : upVal;
        _down[bar] = bar > 0 ? (downVal + (_rmiLength - 1) * _down[bar - 1]) / _rmiLength : downVal;

        if (_down[bar] == 0)
            _rsi[bar] = 100;
        else if (_up[bar] == 0)
            _rsi[bar] = 0;
        else
            _rsi[bar] = 100 - 100 / (1 + _up[bar] / _down[bar]);

        decimal tp = (candle.High + candle.Low + candle.Close) / 3;
        decimal rmf = tp * (decimal)(candle.Volume > 0 ? candle.Volume : 1000);

        decimal posMfVal = 0, negMfVal = 0;
        if (bar > 0)
        {
            var prevCandle = GetCandle(bar - 1);
            decimal tp1 = (prevCandle.High + prevCandle.Low + prevCandle.Close) / 3;
            if (tp > tp1)
                posMfVal = rmf;
            else if (tp < tp1)
                negMfVal = rmf;
        }

        _posMf[bar] = bar > 0 ? (posMfVal + (_rmiLength - 1) * _posMf[bar - 1]) / _rmiLength : posMfVal;
        _negMf[bar] = bar > 0 ? (negMfVal + (_rmiLength - 1) * _negMf[bar - 1]) / _rmiLength : negMfVal;

        if (_negMf[bar] == 0)
            _mf[bar] = 100;
        else if (_posMf[bar] == 0)
            _mf[bar] = 0;
        else
            _mf[bar] = 100 - 100 / (1 + _posMf[bar] / _negMf[bar]);

        _rsiMfi[bar] = (_rsi[bar] + _mf[bar]) / 2;

        decimal alpha = 2m / 6m;
        _ema5[bar] = bar > 0 ? alpha * candle.Close + (1 - alpha) * _ema5[bar - 1] : candle.Close;

        decimal emaChange = bar > 0 ? _ema5[bar] - _ema5[bar - 1] : 0;

        bool isRmiBullish = _rsiMfi[bar] > _positiveAbove && emaChange > 0;
        bool isRmiBearish = _rsiMfi[bar] < _negativeBelow && emaChange < 0;

        bool pMom = bar > 0 && _rsiMfi[bar - 1] < _positiveAbove && isRmiBullish;
        bool nMom = bar > 0 && _rsiMfi[bar - 1] > _negativeBelow && isRmiBearish;

        // Update positive series
        if (pMom)
            _positiveSeries[bar] = 1;
        else if (nMom)
            _positiveSeries[bar] = -1;
        else
            _positiveSeries[bar] = bar > 0 ? _positiveSeries[bar - 1] : 0;

        // Calculate TR and ATR
        decimal tr = Math.Max(candle.High - candle.Low,
                      Math.Max(Math.Abs(candle.High - (bar > 0 ? GetCandle(bar - 1).Close : candle.Close)),
                               Math.Abs(candle.Low - (bar > 0 ? GetCandle(bar - 1).Close : candle.Close))));

        _atr[bar] = bar > 0 ? (tr + 29 * _atr[bar - 1]) / 30 : tr;

        decimal minV = Math.Min(_atr[bar] * 0.3m, candle.Close * 0.003m);
        _minVal[bar] = minV;
        _band[bar] = bar >= 20 ? _minVal[bar - 20] * 4 : 0;

        // Calculate RWMA
        if (bar >= 19)
        {
            decimal sumRange = 0;
            for (int i = 0; i < 20; i++)
                sumRange += _barRange[bar - i];

            decimal sumWeighted = 0;
            if (sumRange > 0)
            {
                for (int i = 0; i < 20; i++)
                {
                    decimal weight = _barRange[bar - i] / sumRange;
                    sumWeighted += GetCandle(bar - i).Close * weight;
                }
            }
            _rwma[bar] = sumWeighted;
        }
        else
        {
            _rwma[bar] = candle.Close;
        }

        decimal centerValue = _rwma[bar];
        _rwmaSeries[bar] = centerValue;
        _min[bar] = centerValue - _band[bar];
        _max[bar] = centerValue + _band[bar];
    }

    private void ProcessTradingLogic(int bar)
    {
        if (bar < 2)
            return;

        // Detect trend changes
        DetectTrendChanges(bar);

        // Check stop loss and TP for active trade
        if (_currentTrade != null && _currentTrade.IsActive)
        {
            CheckStopLossAndTakeProfit(bar);
        }

        // Process signals and trades
        ProcessSignalsAndTrades(bar);
    }

    private void DetectTrendChanges(int bar)
    {
        int currentTrendState = (int)_positiveSeries[bar];
        int previousTrendState = bar > 0 ? (int)_positiveSeries[bar - 1] : 0;

        // Detect trend change to bullish (cyan)
        if (currentTrendState == 1 && previousTrendState != 1)
        {
            _lastBullishSignalBar = bar;
            _bullishConfirmationCount = 0;
            _currentTrendSegment++;
            _tradeOpenedInCurrentSegment = false;
        }
        // Detect trend change to bearish (red)
        else if (currentTrendState == -1 && previousTrendState != -1)
        {
            _lastBearishSignalBar = bar;
            _bearishConfirmationCount = 0;
            _currentTrendSegment++;
            _tradeOpenedInCurrentSegment = false;
        }

        // Count confirmation bars
        if (_lastBullishSignalBar > 0 && bar > _lastBullishSignalBar)
        {
            if (currentTrendState == 1)
                _bullishConfirmationCount++;
            else
                _lastBullishSignalBar = -1;
        }

        if (_lastBearishSignalBar > 0 && bar > _lastBearishSignalBar)
        {
            if (currentTrendState == -1)
                _bearishConfirmationCount++;
            else
                _lastBearishSignalBar = -1;
        }
    }

    private void ProcessSignalsAndTrades(int bar)
    {
        int currentTrendState = (int)_positiveSeries[bar];

        // Process V-Reversal mode separately
        if (_tpType == TakeProfitType.VReversal)
        {
            ProcessVReversalLogic(bar, currentTrendState);
            return;
        }

        // If no active trade, check for entry
        if (_currentTrade == null || !_currentTrade.IsActive)
        {
            // Check for fast entry first
            if (_fastEntryPending && bar == _fastEntryBar)
            {
                if (_fastEntryIsLong)
                {
                    if (currentTrendState == 1 && CheckRiskManagement(bar, true))
                    {
                        EnterLongTrade(bar, false);
                        _tradeOpenedInCurrentSegment = true;
                    }
                }
                else
                {
                    if (currentTrendState == -1 && CheckRiskManagement(bar, false))
                    {
                        EnterShortTrade(bar, false);
                        _tradeOpenedInCurrentSegment = true;
                    }
                }

                _fastEntryPending = false;
            }
            // Check for bullish entry
            else if (_lastBullishSignalBar > 0 && _bullishConfirmationCount >= _waitBars && !_tradeOpenedInCurrentSegment)
            {
                if (currentTrendState == 1 && CheckRiskManagement(bar, true))
                {
                    EnterLongTrade(bar, false);
                    _tradeOpenedInCurrentSegment = true;
                    _lastBullishSignalBar = -1;
                }
            }
            // Check for bearish entry
            else if (_lastBearishSignalBar > 0 && _bearishConfirmationCount >= _waitBars && !_tradeOpenedInCurrentSegment)
            {
                if (currentTrendState == -1 && CheckRiskManagement(bar, false))
                {
                    EnterShortTrade(bar, false);
                    _tradeOpenedInCurrentSegment = true;
                    _lastBearishSignalBar = -1;
                }
            }
        }
        // If active trade, check for signal-based exit
        else if (_tpType == TakeProfitType.Signal)
        {
            // Long position: exit on bearish signal
            if (_currentTrade.IsLong)
            {
                if (_lastBearishSignalBar > _currentTrade.EntryBar &&
                    _bearishConfirmationCount >= _waitBars &&
                    currentTrendState == -1)
                {
                    ExitLongTrade(bar, GetCandle(bar).Close, "Signal TP");

                    // Setup fast entry for opposite direction
                    _fastEntryPending = true;
                    _fastEntryIsLong = false;
                    _fastEntryBar = bar;
                }
            }
            // Short position: exit on bullish signal
            else
            {
                if (_lastBullishSignalBar > _currentTrade.EntryBar &&
                    _bullishConfirmationCount >= _waitBars &&
                    currentTrendState == 1)
                {
                    ExitShortTrade(bar, GetCandle(bar).Close, "Signal TP");

                    // Setup fast entry for opposite direction
                    _fastEntryPending = true;
                    _fastEntryIsLong = true;
                    _fastEntryBar = bar;
                }
            }
        }

        // Check max hold bars
        if (_currentTrade != null && _currentTrade.IsActive)
        {
            if (bar - _currentTrade.EntryBar >= _maxHoldBars)
            {
                if (_currentTrade.IsLong)
                    ExitLongTrade(bar, GetCandle(bar).Close, "MaxBars");
                else
                    ExitShortTrade(bar, GetCandle(bar).Close, "MaxBars");

                _fastEntryPending = false;
            }
        }
    }

    private void ProcessVReversalLogic(int bar, int currentTrendState)
    {
        var candle = GetCandle(bar);
        decimal currentRMI = _rsiMfi[bar];

        // Detect new trend change for V-Reversal
        int previousTrendState = bar > 0 ? (int)_positiveSeries[bar - 1] : 0;

        // New trend segment started - RMI color changed
        if (currentTrendState != previousTrendState && currentTrendState != 0)
        {
            // Reset for new segment only if trend actually changed
            if (_lastVReversalTrendState != currentTrendState)
            {
                _vReversalSignalBar = bar;
                _vReversalExtreme = currentRMI;
                _vReversalExtremeBar = bar;
                _vReversalConfirmed = false;
                _lastVReversalTrendState = currentTrendState;
                _tradeOpenedInCurrentSegment = false;

                // Record V-Reversal signal
                var vInfo = new VReversalInfo
                {
                    SignalBar = bar,
                    IsLongSignal = currentTrendState == 1,  // RMI cyan (bullish)
                    TradeIsLong = currentTrendState == -1,  // Trade LONG when RMI is red
                    SignalRMI = currentRMI,
                    ExtremeRMI = currentRMI,
                    ExtremeBar = bar,
                    CurrentRMI = currentRMI,
                    Confirmed = false,
                    Executed = false,
                    ConfirmBar = -1,
                    RmiMovement = 0,
                    RetracementPct = 0
                };
                _vReversalSignals[bar] = vInfo;
            }
        }

        // Track RMI extremes in current trend
        if (_vReversalSignalBar > 0 && !_vReversalConfirmed && bar > _vReversalSignalBar)
        {
            // For bullish RMI (cyan), track highest RMI value
            if (currentTrendState == 1)
            {
                if (currentRMI > _vReversalExtreme)
                {
                    _vReversalExtreme = currentRMI;
                    _vReversalExtremeBar = bar;
                }
            }
            // For bearish RMI (red), track lowest RMI value
            else if (currentTrendState == -1)
            {
                if (currentRMI < _vReversalExtreme)
                {
                    _vReversalExtreme = currentRMI;
                    _vReversalExtremeBar = bar;
                }
            }

            // Update signal info with current RMI extremes
            if (_vReversalSignals.ContainsKey(_vReversalSignalBar))
            {
                _vReversalSignals[_vReversalSignalBar].ExtremeRMI = _vReversalExtreme;
                _vReversalSignals[_vReversalSignalBar].ExtremeBar = _vReversalExtremeBar;
                _vReversalSignals[_vReversalSignalBar].CurrentRMI = currentRMI;
            }

            // Check for RMI V-Reversal confirmation
            // Must wait for lookback period AFTER extreme was formed
            if (_vReversalExtremeBar > _vReversalSignalBar &&
                bar >= _vReversalExtremeBar + _vReversalLookback)
            {
                bool rmiVReversalDetected = DetectRMIVReversal(bar, currentTrendState, currentRMI);

                if (rmiVReversalDetected && !_tradeOpenedInCurrentSegment)
                {
                    _vReversalConfirmed = true;

                    // Update signal info
                    if (_vReversalSignals.ContainsKey(_vReversalSignalBar))
                    {
                        _vReversalSignals[_vReversalSignalBar].Confirmed = true;
                        _vReversalSignals[_vReversalSignalBar].ConfirmBar = bar;
                    }

                    // Enter trade in REVERSE direction
                    // RMI cyan (1) -> RMI went up then reversed down -> Trade SHORT
                    // RMI red (-1) -> RMI went down then reversed up -> Trade LONG
                    bool shouldGoLong = currentTrendState == -1;

                    if (shouldGoLong && CheckRiskManagement(bar, true))
                    {
                        EnterLongTrade(bar, true);
                        _tradeOpenedInCurrentSegment = true;
                        if (_vReversalSignals.ContainsKey(_vReversalSignalBar))
                            _vReversalSignals[_vReversalSignalBar].Executed = true;
                    }
                    else if (!shouldGoLong && CheckRiskManagement(bar, false))
                    {
                        EnterShortTrade(bar, true);
                        _tradeOpenedInCurrentSegment = true;
                        if (_vReversalSignals.ContainsKey(_vReversalSignalBar))
                            _vReversalSignals[_vReversalSignalBar].Executed = true;
                    }
                }
            }
        }

        // Check stop loss and take profit for active trade
        if (_currentTrade != null && _currentTrade.IsActive)
        {
            CheckStopLossAndTakeProfit(bar);

            // Check max hold bars
            if (bar - _currentTrade.EntryBar >= _maxHoldBars)
            {
                if (_currentTrade.IsLong)
                    ExitLongTrade(bar, candle.Close, "MaxBars");
                else
                    ExitShortTrade(bar, candle.Close, "MaxBars");
            }
        }
    }

    private bool DetectRMIVReversal(int bar, int currentTrendState, decimal currentRMI)
    {
        if (_vReversalSignalBar < 0 || _vReversalExtremeBar <= _vReversalSignalBar)
            return false;

        if (bar < _vReversalExtremeBar + _vReversalLookback)
            return false;

        decimal signalRMI = _rsiMfi[_vReversalSignalBar];

        // For bullish RMI (cyan), check if RMI went up then reversed down
        // RMI should: rise from signal -> reach extreme -> fall back
        if (currentTrendState == 1)
        {
            // RMI movement: signalRMI -> extremeRMI (higher) -> currentRMI (lower than extreme)
            decimal rmiRise = _vReversalExtreme - signalRMI;
            decimal rmiRetracement = _vReversalExtreme - currentRMI;

            if (rmiRise > 0 && rmiRetracement > 0)
            {
                decimal retracementPct = rmiRetracement / rmiRise;

                // 新增条件1: RMI波动幅度必须达到设定值
                bool rmiMovementValid = rmiRise >= _vReversalRmiMovement;

                // 新增条件2: 明显反转确认 - 回撤比例必须达到确认阈值
                bool confirmationValid = retracementPct >= _vReversalConfirmation;

                // 原有条件: 回撤比例必须达到阈值
                bool retracementValid = retracementPct >= _vReversalThreshold;

                // RMI should still be in uptrend zone (above original signal level or close to it)
                bool stillInTrend = currentRMI >= signalRMI * 0.95m;

                // 更新V反信号信息
                if (_vReversalSignals.ContainsKey(_vReversalSignalBar))
                {
                    _vReversalSignals[_vReversalSignalBar].RmiMovement = rmiRise;
                    _vReversalSignals[_vReversalSignalBar].RetracementPct = retracementPct;
                }

                // V-reversal confirmed if all conditions met and still in trend
                return rmiMovementValid && confirmationValid && retracementValid && stillInTrend;
            }
        }
        // For bearish RMI (red), check if RMI went down then reversed up
        // RMI should: fall from signal -> reach extreme -> rise back
        else if (currentTrendState == -1)
        {
            // RMI movement: signalRMI -> extremeRMI (lower) -> currentRMI (higher than extreme)
            decimal rmiFall = signalRMI - _vReversalExtreme;
            decimal rmiRetracement = currentRMI - _vReversalExtreme;

            if (rmiFall > 0 && rmiRetracement > 0)
            {
                decimal retracementPct = rmiRetracement / rmiFall;

                // 新增条件1: RMI波动幅度必须达到设定值
                bool rmiMovementValid = rmiFall >= _vReversalRmiMovement;

                // 新增条件2: 明显反转确认 - 回撤比例必须达到确认阈值
                bool confirmationValid = retracementPct >= _vReversalConfirmation;

                // 原有条件: 回撤比例必须达到阈值
                bool retracementValid = retracementPct >= _vReversalThreshold;

                // RMI should still be in downtrend zone (below original signal level or close to it)
                bool stillInTrend = currentRMI <= signalRMI * 1.05m;

                // 更新V反信号信息
                if (_vReversalSignals.ContainsKey(_vReversalSignalBar))
                {
                    _vReversalSignals[_vReversalSignalBar].RmiMovement = rmiFall;
                    _vReversalSignals[_vReversalSignalBar].RetracementPct = retracementPct;
                }

                // V-reversal confirmed if all conditions met and still in trend
                return rmiMovementValid && confirmationValid && retracementValid && stillInTrend;
            }
        }

        return false;
    }

    private bool CheckRiskManagement(int bar, bool isLong)
    {
        var candle = GetCandle(bar);
        decimal entryPrice = candle.Close;
        decimal atrValue = _atr[bar] > 0 ? _atr[bar] : (candle.High - candle.Low) * 0.1m;

        decimal stopLoss;
        if (isLong)
            stopLoss = entryPrice - atrValue * _atrMultiplier;
        else
            stopLoss = entryPrice + atrValue * _atrMultiplier;

        decimal riskTicks = Math.Abs(entryPrice - stopLoss) / 0.1m;
        decimal riskAmount = riskTicks * _tickValue;

        // Risk must be less than max risk per trade
        return riskAmount <= _maxRiskPerTrade;
    }

    private void CheckStopLossAndTakeProfit(int bar)
    {
        if (_currentTrade == null || !_currentTrade.IsActive)
            return;

        var candle = GetCandle(bar);

        if (_currentTrade.IsLong)
        {
            // Check stop loss
            if (candle.Low <= _currentTrade.StopLoss)
            {
                ExitLongTrade(bar, _currentTrade.StopLoss, "Stop Loss");
            }
            // Check take profit (only for RiskReward and VReversal mode)
            else if ((_tpType == TakeProfitType.RiskReward || _tpType == TakeProfitType.VReversal) &&
                     _currentTrade.TakeProfit > 0 && candle.High >= _currentTrade.TakeProfit)
            {
                ExitLongTrade(bar, _currentTrade.TakeProfit, "Take Profit");
            }
        }
        else
        {
            // Check stop loss
            if (candle.High >= _currentTrade.StopLoss)
            {
                ExitShortTrade(bar, _currentTrade.StopLoss, "Stop Loss");
            }
            // Check take profit (only for RiskReward and VReversal mode)
            else if ((_tpType == TakeProfitType.RiskReward || _tpType == TakeProfitType.VReversal) &&
                     _currentTrade.TakeProfit > 0 && candle.Low <= _currentTrade.TakeProfit)
            {
                ExitShortTrade(bar, _currentTrade.TakeProfit, "Take Profit");
            }
        }
    }

    private void EnterLongTrade(int bar, bool isVReversal = false)
    {
        var candle = GetCandle(bar);
        decimal entryPrice = candle.Close;

        decimal atrValue = _atr[bar] > 0 ? _atr[bar] : (candle.High - candle.Low) * 0.1m;
        decimal stopLoss = entryPrice - atrValue * _atrMultiplier;

        decimal riskTicks = (entryPrice - stopLoss) / 0.1m;
        decimal riskAmount = riskTicks * _tickValue;

        // Calculate take profit based on mode
        decimal takeProfit = 0;
        if (_tpType == TakeProfitType.RiskReward || _tpType == TakeProfitType.VReversal)
        {
            takeProfit = entryPrice + (riskTicks * _riskRewardRatio * 0.1m);
        }

        _currentTrade = new Trade
        {
            EntryBar = bar,
            EntryPrice = entryPrice,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            IsLong = true,
            EntryTime = candle.Time,
            ExitReason = "",
            TpMode = _tpType,
            RiskAmount = riskAmount,
            TrendSegment = _currentTrendSegment,
            IsVReversal = isVReversal
        };

        // 使用不同的颜色标记V反交易
        if (isVReversal)
        {
            _longEntries.Color = _vReversalLongColor;
            _vReversalTrades++;
        }
        else
        {
            _longEntries.Color = _longColor;
        }

        _longEntries[bar] = entryPrice;
        _longTrades++;

        var tradeData = new TradeLinesData
        {
            EntryBar = bar,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            IsLong = true,
            ExitBar = 0,
            EntryPrice = entryPrice,
            TpMode = _tpType,
            IsVReversal = isVReversal
        };

        _allTradeLines.Add(tradeData);

        if (_startDateAll == DateTime.MaxValue)
            _startDateAll = candle.Time;
    }

    private void EnterShortTrade(int bar, bool isVReversal = false)
    {
        var candle = GetCandle(bar);
        decimal entryPrice = candle.Close;

        decimal atrValue = _atr[bar] > 0 ? _atr[bar] : (candle.High - candle.Low) * 0.1m;
        decimal stopLoss = entryPrice + atrValue * _atrMultiplier;

        decimal riskTicks = (stopLoss - entryPrice) / 0.1m;
        decimal riskAmount = riskTicks * _tickValue;

        // Calculate take profit based on mode
        decimal takeProfit = 0;
        if (_tpType == TakeProfitType.RiskReward || _tpType == TakeProfitType.VReversal)
        {
            takeProfit = entryPrice - (riskTicks * _riskRewardRatio * 0.1m);
        }

        _currentTrade = new Trade
        {
            EntryBar = bar,
            EntryPrice = entryPrice,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            IsLong = false,
            EntryTime = candle.Time,
            ExitReason = "",
            TpMode = _tpType,
            RiskAmount = riskAmount,
            TrendSegment = _currentTrendSegment,
            IsVReversal = isVReversal
        };

        // 使用不同的颜色标记V反交易
        if (isVReversal)
        {
            _shortEntries.Color = _vReversalShortColor;
            _vReversalTrades++;
        }
        else
        {
            _shortEntries.Color = _shortColor;
        }

        _shortEntries[bar] = entryPrice;
        _shortTrades++;

        var tradeData = new TradeLinesData
        {
            EntryBar = bar,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            IsLong = false,
            ExitBar = 0,
            EntryPrice = entryPrice,
            TpMode = _tpType,
            IsVReversal = isVReversal
        };

        _allTradeLines.Add(tradeData);

        if (_startDateAll == DateTime.MaxValue)
            _startDateAll = candle.Time;
    }

    private void ExitLongTrade(int bar, decimal exitPrice, string reason)
    {
        _longExits[bar] = exitPrice;

        if (_currentTrade != null)
        {
            _currentTrade.ExitBar = bar;
            _currentTrade.ExitPrice = exitPrice;
            _currentTrade.ExitTime = GetCandle(bar).Time;
            _currentTrade.ExitReason = reason;

            // Calculate profit
            decimal ticks = (_currentTrade.ExitPrice - _currentTrade.EntryPrice) / 0.1m;
            _currentTrade.Profit = ticks * _tickValue - _commission;

            _trades.Add(_currentTrade);

            // 更新V反交易统计
            if (_currentTrade.IsVReversal)
            {
                _vReversalTotalProfit += _currentTrade.Profit;
                if (_currentTrade.IsWinner)
                    _vReversalWinners++;
                else
                    _vReversalLosers++;
            }

            var tradeLineData = _allTradeLines.LastOrDefault(t => t.EntryBar == _currentTrade.EntryBar);
            if (tradeLineData != null)
                tradeLineData.ExitBar = bar;

            _currentTrade = null;
            _endDateAll = GetCandle(bar).Time;
        }
    }

    private void ExitShortTrade(int bar, decimal exitPrice, string reason)
    {
        _shortExits[bar] = exitPrice;

        if (_currentTrade != null)
        {
            _currentTrade.ExitBar = bar;
            _currentTrade.ExitPrice = exitPrice;
            _currentTrade.ExitTime = GetCandle(bar).Time;
            _currentTrade.ExitReason = reason;

            // Calculate profit
            decimal ticks = (_currentTrade.EntryPrice - _currentTrade.ExitPrice) / 0.1m;
            _currentTrade.Profit = ticks * _tickValue - _commission;

            _trades.Add(_currentTrade);

            // 更新V反交易统计
            if (_currentTrade.IsVReversal)
            {
                _vReversalTotalProfit += _currentTrade.Profit;
                if (_currentTrade.IsWinner)
                    _vReversalWinners++;
                else
                    _vReversalLosers++;
            }

            var tradeLineData = _allTradeLines.LastOrDefault(t => t.EntryBar == _currentTrade.EntryBar);
            if (tradeLineData != null)
                tradeLineData.ExitBar = bar;

            _currentTrade = null;
            _endDateAll = GetCandle(bar).Time;
        }
    }

    private void UpdateStatistics()
    {
        if (_trades.Count == 0) return;

        _totalTrades = _trades.Count;
        _winningTrades = 0;
        _losingTrades = 0;
        _totalProfitTicks = 0;
        _totalProfitAmount = 0;
        _maxProfit = 0;
        _maxLoss = 0;
        _maxDrawdown = 0;
        _maxDrawdownAmount = 0;

        decimal totalWinningProfit = 0;
        decimal totalLosingLoss = 0;
        decimal peakEquity = _initialCapital;
        _currentEquity = _initialCapital;

        List<decimal> returns = new List<decimal>();

        // Streak tracking variables
        int currentWinStreak = 0;
        int currentLossStreak = 0;
        decimal currentWinStreakProfit = 0;
        decimal currentLossStreakLoss = 0;
        int currentWinStreakStartIndex = -1;
        int currentLossStreakStartIndex = -1;

        // Reset max streak values
        _maxConsecutiveWins = 0;
        _maxConsecutiveWinsProfit = 0;
        _maxConsecutiveWinsDuration = TimeSpan.Zero;
        _maxConsecutiveLosses = 0;
        _maxConsecutiveLossesAmount = 0;
        _maxConsecutiveLossesDuration = TimeSpan.Zero;

        for (int i = 0; i < _trades.Count; i++)
        {
            var trade = _trades[i];
            decimal tradeProfit = trade.Profit;
            decimal tradeTicks = trade.TicksDifference;

            _totalProfitAmount += tradeProfit;
            _totalProfitTicks += tradeTicks;
            _currentEquity += tradeProfit;

            decimal tradeReturn = tradeProfit / _initialCapital;
            returns.Add(tradeReturn);

            if (_currentEquity > peakEquity)
                peakEquity = _currentEquity;

            decimal drawdown = peakEquity > 0 ? (peakEquity - _currentEquity) / peakEquity * 100 : 0;
            if (drawdown > _maxDrawdown)
            {
                _maxDrawdown = drawdown;
                _maxDrawdownAmount = peakEquity - _currentEquity;  // Calculate drawdown amount
            }

            if (trade.IsWinner)
            {
                _winningTrades++;
                totalWinningProfit += tradeProfit;
                if (tradeProfit > _maxProfit)
                    _maxProfit = tradeProfit;

                // ===== WINNING STREAK LOGIC =====
                currentWinStreak++;
                currentWinStreakProfit += tradeProfit;

                if (currentWinStreak == 1)
                {
                    // Starting a new winning streak
                    currentWinStreakStartIndex = i;
                }

                // Check if we need to finalize a losing streak
                if (currentLossStreak > 0)
                {
                    // Check if this losing streak is a new record
                    if (currentLossStreak > _maxConsecutiveLosses ||
                        (currentLossStreak == _maxConsecutiveLosses && currentLossStreakLoss > _maxConsecutiveLossesAmount))
                    {
                        _maxConsecutiveLosses = currentLossStreak;
                        _maxConsecutiveLossesAmount = currentLossStreakLoss;

                        // Calculate duration: from first losing trade entry to last losing trade exit
                        if (currentLossStreakStartIndex >= 0 && i > 0)
                        {
                            DateTime streakStart = _trades[currentLossStreakStartIndex].EntryTime;
                            DateTime streakEnd = _trades[i - 1].ExitTime;
                            _maxConsecutiveLossesDuration = streakEnd - streakStart;
                        }
                    }

                    // Reset losing streak
                    currentLossStreak = 0;
                    currentLossStreakLoss = 0;
                    currentLossStreakStartIndex = -1;
                }
            }
            else
            {
                _losingTrades++;
                totalLosingLoss += Math.Abs(tradeProfit);
                if (tradeProfit < _maxLoss)
                    _maxLoss = tradeProfit;

                // ===== LOSING STREAK LOGIC =====
                currentLossStreak++;
                currentLossStreakLoss += Math.Abs(tradeProfit);

                if (currentLossStreak == 1)
                {
                    // Starting a new losing streak
                    currentLossStreakStartIndex = i;
                }

                // Check if we need to finalize a winning streak
                if (currentWinStreak > 0)
                {
                    // Check if this winning streak is a new record
                    if (currentWinStreak > _maxConsecutiveWins ||
                        (currentWinStreak == _maxConsecutiveWins && currentWinStreakProfit > _maxConsecutiveWinsProfit))
                    {
                        _maxConsecutiveWins = currentWinStreak;
                        _maxConsecutiveWinsProfit = currentWinStreakProfit;

                        // Calculate duration: from first winning trade entry to last winning trade exit
                        if (currentWinStreakStartIndex >= 0 && i > 0)
                        {
                            DateTime streakStart = _trades[currentWinStreakStartIndex].EntryTime;
                            DateTime streakEnd = _trades[i - 1].ExitTime;
                            _maxConsecutiveWinsDuration = streakEnd - streakStart;
                        }
                    }

                    // Reset winning streak
                    currentWinStreak = 0;
                    currentWinStreakProfit = 0;
                    currentWinStreakStartIndex = -1;
                }
            }
        }

        // ===== FINALIZE REMAINING STREAKS =====
        // Check if the last sequence is a record (winning streak)
        if (currentWinStreak > 0)
        {
            if (currentWinStreak > _maxConsecutiveWins ||
                (currentWinStreak == _maxConsecutiveWins && currentWinStreakProfit > _maxConsecutiveWinsProfit))
            {
                _maxConsecutiveWins = currentWinStreak;
                _maxConsecutiveWinsProfit = currentWinStreakProfit;

                if (currentWinStreakStartIndex >= 0)
                {
                    DateTime streakStart = _trades[currentWinStreakStartIndex].EntryTime;
                    DateTime streakEnd = _trades[_trades.Count - 1].ExitTime;
                    _maxConsecutiveWinsDuration = streakEnd - streakStart;
                }
            }
        }

        // Check if the last sequence is a record (losing streak)
        if (currentLossStreak > 0)
        {
            if (currentLossStreak > _maxConsecutiveLosses ||
                (currentLossStreak == _maxConsecutiveLosses && currentLossStreakLoss > _maxConsecutiveLossesAmount))
            {
                _maxConsecutiveLosses = currentLossStreak;
                _maxConsecutiveLossesAmount = currentLossStreakLoss;

                if (currentLossStreakStartIndex >= 0)
                {
                    DateTime streakStart = _trades[currentLossStreakStartIndex].EntryTime;
                    DateTime streakEnd = _trades[_trades.Count - 1].ExitTime;
                    _maxConsecutiveLossesDuration = streakEnd - streakStart;
                }
            }
        }

        _winRate = _totalTrades > 0 ? (decimal)_winningTrades / _totalTrades * 100 : 0;
        _avgProfit = _totalTrades > 0 ? _totalProfitAmount / _totalTrades : 0;
        _profitFactor = totalLosingLoss != 0 ? totalWinningProfit / totalLosingLoss : totalWinningProfit > 0 ? 999 : 0;

        // 更新V反交易胜率
        _vReversalWinRate = _vReversalTrades > 0 ? (decimal)_vReversalWinners / _vReversalTrades * 100 : 0;

        // Calculate Sharpe Ratio
        if (returns.Count > 1)
        {
            decimal avgReturn = returns.Average();
            decimal variance = returns.Sum(r => (r - avgReturn) * (r - avgReturn)) / (returns.Count - 1);
            decimal stdDev = (decimal)Math.Sqrt((double)variance);
            _sharpeRatio = stdDev != 0 ? (avgReturn * (decimal)Math.Sqrt(252)) / stdDev : 0;
        }
        else
        {
            _sharpeRatio = 0;
        }
    }

    private string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        else if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        else if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        else
            return $"{duration.Seconds}s";
    }

    private void ExportBacktestResults()
    {
        try
        {
            if (_trades.Count == 0)
            {
                _exportStatusMessage = "No trades to export";
                _exportStatusTime = DateTime.Now;
                return;
            }

            StringBuilder csv = new StringBuilder();

            // CSV header
            csv.AppendLine("Entry Time,Exit Time,Direction,Entry Price,Exit Price,Stop Loss,Take Profit,Ticks P/L,$ P/L,Exit Reason,TP Mode,Risk Amount,Trend Segment,Is V-Reversal");

            // CSV data
            foreach (var trade in _trades)
            {
                string entryTime = trade.EntryTime.ToString("yyyy-MM-dd HH:mm:ss");
                string exitTime = trade.ExitTime.ToString("yyyy-MM-dd HH:mm:ss");
                string direction = trade.IsLong ? "Long" : "Short";
                string entryPrice = trade.EntryPrice.ToString("F2");
                string exitPrice = trade.ExitPrice.ToString("F2");
                string stopLoss = trade.StopLoss.ToString("F2");
                string takeProfit = trade.TakeProfit > 0 ? trade.TakeProfit.ToString("F2") : "N/A";
                string ticksPL = trade.TicksDifference.ToString("F2");
                string dollarPL = trade.Profit.ToString("F2");
                string exitReason = trade.ExitReason;
                string tpMode = trade.TpMode == TakeProfitType.Signal ? "Signal" :
                               trade.TpMode == TakeProfitType.VReversal ? $"V-Reversal RR {_riskRewardRatio:F1}:1" :
                               $"RR {_riskRewardRatio:F1}:1";
                string riskAmount = trade.RiskAmount.ToString("F2");
                string trendSegment = trade.TrendSegment.ToString();
                string isVReversal = trade.IsVReversal ? "Yes" : "No";

                csv.AppendLine($"{entryTime},{exitTime},{direction},{entryPrice},{exitPrice},{stopLoss},{takeProfit},{ticksPL},{dollarPL},{exitReason},{tpMode},{riskAmount},{trendSegment},{isVReversal}");
            }

            // Summary statistics
            csv.AppendLine("");
            csv.AppendLine("=== BACKTEST SUMMARY ===");
            csv.AppendLine($"Total Trades,{_totalTrades}");
            csv.AppendLine($"Long Trades,{_longTrades}");
            csv.AppendLine($"Short Trades,{_shortTrades}");
            csv.AppendLine($"Winners,{_winningTrades}");
            csv.AppendLine($"Losers,{_losingTrades}");
            csv.AppendLine($"Win Rate,{_winRate:F2}%");
            csv.AppendLine($"Total Ticks,{_totalProfitTicks:F2}");
            csv.AppendLine($"Total P/L,${_totalProfitAmount:F2}");
            csv.AppendLine($"Avg P/L,${_avgProfit:F2}");
            csv.AppendLine($"Max Profit,${_maxProfit:F2}");
            csv.AppendLine($"Max Loss,${_maxLoss:F2}");
            csv.AppendLine($"Profit Factor,{_profitFactor:F2}");
            csv.AppendLine($"Max Drawdown,{_maxDrawdown:F2}%");
            csv.AppendLine($"Max Drawdown Amount,${_maxDrawdownAmount:F2}");
            csv.AppendLine($"Sharpe Ratio,{_sharpeRatio:F2}");
            csv.AppendLine("");
            csv.AppendLine("=== WINNING STREAK STATISTICS ===");
            csv.AppendLine($"Max Consecutive Wins,{_maxConsecutiveWins}");
            csv.AppendLine($"Max Consecutive Wins Profit,${_maxConsecutiveWinsProfit:F2}");
            csv.AppendLine($"Max Consecutive Wins Duration,{FormatDuration(_maxConsecutiveWinsDuration)}");
            csv.AppendLine("");
            csv.AppendLine("=== LOSING STREAK STATISTICS ===");
            csv.AppendLine($"Max Consecutive Losses,{_maxConsecutiveLosses}");
            csv.AppendLine($"Max Consecutive Losses Amount,${_maxConsecutiveLossesAmount:F2}");
            csv.AppendLine($"Max Consecutive Losses Duration,{FormatDuration(_maxConsecutiveLossesDuration)}");

            // V-Reversal specific statistics
            if (_tpType == TakeProfitType.VReversal)
            {
                csv.AppendLine("");
                csv.AppendLine("=== V-REVERSAL STATISTICS ===");
                csv.AppendLine($"V-Reversal Trades,{_vReversalTrades}");
                csv.AppendLine($"V-Reversal Winners,{_vReversalWinners}");
                csv.AppendLine($"V-Reversal Losers,{_vReversalLosers}");
                csv.AppendLine($"V-Reversal Win Rate,{_vReversalWinRate:F2}%");
                csv.AppendLine($"V-Reversal Total P/L,${_vReversalTotalProfit:F2}");
            }

            csv.AppendLine("");
            csv.AppendLine("=== SETTINGS ===");
            csv.AppendLine($"TP Mode,{(_tpType == TakeProfitType.Signal ? "Signal" :
                              _tpType == TakeProfitType.VReversal ? $"V-Reversal RR {_riskRewardRatio:F1}:1" :
                              $"RiskReward {_riskRewardRatio:F1}:1")}");
            if (_tpType == TakeProfitType.VReversal)
            {
                csv.AppendLine($"V-Reversal Retracement,{_vReversalThreshold * 100:F0}%");
                csv.AppendLine($"V-Reversal RMI Movement,{_vReversalRmiMovement:F1} points");
                csv.AppendLine($"V-Reversal Confirmation,{_vReversalConfirmation * 100:F0}%");
                csv.AppendLine($"V-Reversal Lookback,{_vReversalLookback} bars");
            }
            csv.AppendLine($"Stop Loss,ATR x {_atrMultiplier}");
            csv.AppendLine($"Max Risk Per Trade,${_maxRiskPerTrade:F2}");
            csv.AppendLine($"Wait Bars,{_waitBars}");
            csv.AppendLine($"Initial Capital,${_initialCapital:F2}");
            csv.AppendLine($"Commission,${_commission:F2}");
            csv.AppendLine($"Tick Value,${_tickValue:F2}");

            // Save to file
            string fileName = $"RMI_Backtest_{_tpType}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string filePath = Path.Combine(documentsPath, fileName);

            File.WriteAllText(filePath, csv.ToString(), Encoding.UTF8);

            _exportStatusMessage = $"✓ Exported: {fileName}";
            _exportStatusTime = DateTime.Now;

            RedrawChart();
        }
        catch (Exception ex)
        {
            _exportStatusMessage = $"✗ Export failed: {ex.Message}";
            _exportStatusTime = DateTime.Now;
        }
    }
    #endregion

    #region Custom rendering
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        try
        {
            DrawRmiCenterLineAndRangeMa(context);
            DrawRmiTrendLabels(context);

            // Draw V-Reversal signals if in V-Reversal mode
            if (_tpType == TakeProfitType.VReversal)
            {
                DrawVReversalSignals(context);
            }

            if (EnableStopLossTakeProfitLines)
                DrawAllStopLossTakeProfitLines(context);

            DrawEntryExitMarkers(context);
            DrawStatisticsPanel(context);
        }
        catch
        {
        }
    }

    private void DrawRmiCenterLineAndRangeMa(RenderContext context)
    {
        for (int i = Math.Max(FirstVisibleBarNumber, 1); i < LastVisibleBarNumber; i++)
        {
            if (_rwmaSeries[i] == 0 || _rwmaSeries[i - 1] == 0)
                continue;

            System.Windows.Media.Color lineColor;
            if (_positiveSeries[i] == 1)
                lineColor = _bullCenterColor;
            else if (_positiveSeries[i] == -1)
                lineColor = _bearCenterColor;
            else
                lineColor = System.Windows.Media.Colors.Gray;

            Color drawColor = Color.FromArgb(255, lineColor.R, lineColor.G, lineColor.B);
            var linePen = new RenderPen(drawColor, 2);

            int x1 = ChartInfo.GetXByBar(i - 1);
            int y1 = (int)ChartInfo.GetYByPrice(_rwmaSeries[i - 1]);
            int x2 = ChartInfo.GetXByBar(i);
            int y2 = (int)ChartInfo.GetYByPrice(_rwmaSeries[i]);

            context.DrawLine(linePen, x1, y1, x2, y2);
        }

        if (_showRangeMa)
        {
            for (int i = Math.Max(FirstVisibleBarNumber, 1); i < LastVisibleBarNumber; i++)
            {
                if (_positiveSeries[i] == 0 || _rwmaSeries[i] == 0 || _rwmaSeries[i - 1] == 0)
                    continue;

                System.Windows.Media.Color mediaColor = _positiveSeries[i] == 1 ? _bullRange : _bearRange;
                Color drawColor = Color.FromArgb(64, mediaColor.R, mediaColor.G, mediaColor.B);

                int x1 = ChartInfo.GetXByBar(i - 1);
                int x2 = ChartInfo.GetXByBar(i);

                int yMax1 = (int)ChartInfo.GetYByPrice(_max[i - 1]);
                int yRwma1 = (int)ChartInfo.GetYByPrice(_rwmaSeries[i - 1]);
                int yMin1 = (int)ChartInfo.GetYByPrice(_min[i - 1]);

                int yMax2 = (int)ChartInfo.GetYByPrice(_max[i]);
                int yRwma2 = (int)ChartInfo.GetYByPrice(_rwmaSeries[i]);
                int yMin2 = (int)ChartInfo.GetYByPrice(_min[i]);

                Point[] upperPolygon = new Point[]
                {
                    new Point(x1, yMax1),
                    new Point(x2, yMax2),
                    new Point(x2, yRwma2),
                    new Point(x1, yRwma1)
                };

                Point[] lowerPolygon = new Point[]
                {
                    new Point(x1, yRwma1),
                    new Point(x2, yRwma2),
                    new Point(x2, yMin2),
                    new Point(x1, yMin1)
                };

                context.FillPolygon(drawColor, upperPolygon);
                context.FillPolygon(drawColor, lowerPolygon);
            }
        }
    }

    private void DrawVReversalSignals(RenderContext context)
    {
        var font = new RenderFont("Arial", 9, FontStyle.Bold);
        var smallFont = new RenderFont("Arial", 7);

        foreach (var vSignal in _vReversalSignals.Values)
        {
            if (vSignal.SignalBar < FirstVisibleBarNumber || vSignal.SignalBar > LastVisibleBarNumber)
                continue;

            int signalX = ChartInfo.GetXByBar(vSignal.SignalBar);
            var signalCandle = GetCandle(vSignal.SignalBar);

            // Position marker based on RMI direction
            decimal markerPrice;
            int yOffset;
            if (vSignal.IsLongSignal)  // RMI cyan (bullish signal) -> will trade SHORT
            {
                markerPrice = signalCandle.High;
                yOffset = -20;
            }
            else  // RMI red (bearish signal) -> will trade LONG
            {
                markerPrice = signalCandle.Low;
                yOffset = 20;
            }

            int markerY = (int)ChartInfo.GetYByPrice(markerPrice) + yOffset;

            // Draw diamond shape for V-Reversal signal
            Color signalColor;
            string label;

            if (vSignal.Executed)
            {
                // Executed: use V反专用颜色
                signalColor = vSignal.TradeIsLong ?
                    Color.FromArgb(255, _vReversalLongColor.R, _vReversalLongColor.G, _vReversalLongColor.B) :   // V反做多颜色
                    Color.FromArgb(255, _vReversalShortColor.R, _vReversalShortColor.G, _vReversalShortColor.B); // V反做空颜色
                label = "V✓";
            }
            else if (vSignal.Confirmed)
            {
                // Confirmed but not executed: yellow
                signalColor = Color.FromArgb(255, 255, 215, 0);
                label = "V!";
            }
            else
            {
                // Not confirmed: gray
                signalColor = Color.FromArgb(180, 150, 150, 150);
                label = "V?";
            }

            // Draw diamond
            int diamondSize = 7;
            Point[] diamond = new Point[]
            {
                new Point(signalX, markerY - diamondSize),
                new Point(signalX + diamondSize, markerY),
                new Point(signalX, markerY + diamondSize),
                new Point(signalX - diamondSize, markerY)
            };
            context.FillPolygon(signalColor, diamond);

            // Draw white border for visibility
            var borderPen = new RenderPen(Color.White, 2);
            context.DrawPolygon(borderPen, diamond);

            // Draw label with background
            var labelSize = context.MeasureString(label, font);
            int labelX = signalX + diamondSize + 4;
            int labelY = markerY - labelSize.Height / 2;

            // Label background
            var labelBg = new Rectangle(labelX - 2, labelY - 1, labelSize.Width + 4, labelSize.Height + 2);
            context.FillRectangle(Color.FromArgb(220, 0, 0, 0), labelBg);

            // Label text
            Color labelTextColor = vSignal.Executed ? signalColor : Color.White;
            context.DrawString(label, font, labelTextColor, labelX, labelY);

            // Draw RMI values info with parameters
            string rmiInfo = $"RMI:{vSignal.SignalRMI:F0}→{vSignal.ExtremeRMI:F0}→{vSignal.CurrentRMI:F0}";
            string paramInfo = $"Move:{vSignal.RmiMovement:F1} Ret:{vSignal.RetracementPct * 100:F0}%";

            int infoY = labelY + labelSize.Height + 2;
            context.DrawString(rmiInfo, smallFont, Color.FromArgb(200, 200, 200, 200), labelX, infoY);
            context.DrawString(paramInfo, smallFont, Color.FromArgb(200, 200, 200, 200), labelX, infoY + 10);

            // Draw additional info text
            if (vSignal.Executed)
            {
                string tradeInfo = vSignal.TradeIsLong ? "V-LONG" : "V-SHORT";
                int tradeY = infoY + 20;
                context.DrawString(tradeInfo, smallFont, signalColor, labelX, tradeY);
            }

            // Draw RMI extreme point if exists
            if (vSignal.ExtremeBar > vSignal.SignalBar &&
                vSignal.ExtremeBar >= FirstVisibleBarNumber &&
                vSignal.ExtremeBar <= LastVisibleBarNumber)
            {
                int extremeX = ChartInfo.GetXByBar(vSignal.ExtremeBar);
                var extremeCandle = GetCandle(vSignal.ExtremeBar);
                decimal extremePrice = vSignal.IsLongSignal ? extremeCandle.High : extremeCandle.Low;
                int extremeY = (int)ChartInfo.GetYByPrice(extremePrice) + (vSignal.IsLongSignal ? -15 : 15);

                // Draw extreme marker (small circle)
                var extremeCircle = new Rectangle(extremeX - 4, extremeY - 4, 8, 8);
                context.FillEllipse(Color.FromArgb(200, 255, 255, 0), extremeCircle);
                context.DrawEllipse(new RenderPen(Color.White, 1), extremeCircle);

                // Draw line from signal to extreme
                var dashedPen = new RenderPen(Color.FromArgb(150, 200, 200, 200), 1);
                context.DrawLine(dashedPen, signalX, markerY, extremeX, extremeY);
            }

            // Draw confirmation line if confirmed
            if (vSignal.Confirmed && vSignal.ConfirmBar > vSignal.SignalBar &&
                vSignal.ConfirmBar >= FirstVisibleBarNumber && vSignal.ConfirmBar <= LastVisibleBarNumber)
            {
                int confirmX = ChartInfo.GetXByBar(vSignal.ConfirmBar);
                var confirmCandle = GetCandle(vSignal.ConfirmBar);
                int confirmY = (int)ChartInfo.GetYByPrice(confirmCandle.Close);

                // Draw line from extreme to confirmation
                if (vSignal.ExtremeBar > vSignal.SignalBar)
                {
                    int extremeX = ChartInfo.GetXByBar(vSignal.ExtremeBar);
                    var extremeCandle = GetCandle(vSignal.ExtremeBar);
                    decimal extremePrice = vSignal.IsLongSignal ? extremeCandle.High : extremeCandle.Low;
                    int extremeY = (int)ChartInfo.GetYByPrice(extremePrice) + (vSignal.IsLongSignal ? -15 : 15);

                    var confirmPen = new RenderPen(signalColor, 2);
                    context.DrawLine(confirmPen, extremeX, extremeY, confirmX, confirmY);
                }

                // Draw confirmation point
                var confirmCircle = new Rectangle(confirmX - 3, confirmY - 3, 6, 6);
                context.FillEllipse(signalColor, confirmCircle);
            }
        }
    }

    private void DrawRmiTrendLabels(RenderContext context)
    {
        for (int i = FirstVisibleBarNumber; i <= LastVisibleBarNumber; i++)
        {
            if (i == 0) continue;

            if (_positiveSeries[i] == 1 && _positiveSeries[i - 1] != 1 && _min[i] != 0)
            {
                decimal labelPrice = _min[i] - _band[i] / 2;
                int x = ChartInfo.GetXByBar(i);
                int y = (int)ChartInfo.GetYByPrice(labelPrice);

                Point[] triangle = new Point[]
                {
                    new Point(x, y - 5),
                    new Point(x - 5, y + 5),
                    new Point(x + 5, y + 5)
                };
                context.FillPolygon(Color.FromArgb(255, 0, 188, 212), triangle);
            }

            if (_positiveSeries[i] == -1 && _positiveSeries[i - 1] != -1 && _max[i] != 0)
            {
                decimal labelPrice = _max[i] + _band[i] / 2;
                int x = ChartInfo.GetXByBar(i);
                int y = (int)ChartInfo.GetYByPrice(labelPrice);

                Point[] triangle = new Point[]
                {
                    new Point(x, y + 5),
                    new Point(x - 5, y - 5),
                    new Point(x + 5, y - 5)
                };
                context.FillPolygon(Color.Red, triangle);
            }
        }
    }

    private void DrawAllStopLossTakeProfitLines(RenderContext context)
    {
        foreach (var tradeData in _allTradeLines)
        {
            if (tradeData.EntryBar > LastVisibleBarNumber)
                continue;
            if (tradeData.ExitBar > 0 && tradeData.ExitBar < FirstVisibleBarNumber)
                continue;

            DrawTradeLinesForTrade(context, tradeData);
        }

        if (_currentTrade != null && _currentTrade.IsActive)
        {
            DrawCurrentTradeLines(context);
        }
    }

    private void DrawTradeLinesForTrade(RenderContext context, TradeLinesData tradeData)
    {
        try
        {
            int startX = ChartInfo.GetXByBar(tradeData.EntryBar);
            int endX = tradeData.ExitBar > 0 ? ChartInfo.GetXByBar(tradeData.ExitBar) : ChartInfo.Region.Width;

            startX = Math.Max(0, Math.Min(startX, ChartInfo.Region.Width));
            endX = Math.Max(0, Math.Min(endX, ChartInfo.Region.Width));

            int entryY = (int)ChartInfo.GetYByPrice(tradeData.EntryPrice);
            int slY = (int)ChartInfo.GetYByPrice(tradeData.StopLoss);

            // Draw vertical line at entry
            var verticalColor = tradeData.IsVReversal ?
                Color.FromArgb(150, 255, 200, 0) : // V反交易用橙色
                Color.FromArgb(150, 150, 150, 150); // 普通交易用灰色
            var verticalPen = new RenderPen(verticalColor, 1);

            // Draw stop loss line - V反交易使用不同颜色
            Color slColor;
            if (tradeData.IsVReversal)
            {
                slColor = tradeData.IsLong ?
                    Color.FromArgb(200, 255, 100, 0) : // V反做多止损
                    Color.FromArgb(200, 255, 150, 0);  // V反做空止损
            }
            else
            {
                slColor = Color.FromArgb(200, StopLossColor.R, StopLossColor.G, StopLossColor.B);
            }
            var slPen = new RenderPen(slColor, _lineThickness);
            context.DrawLine(slPen, startX, slY, endX, slY);

            // Only draw TP line for RiskReward and VReversal mode
            if ((tradeData.TpMode == TakeProfitType.RiskReward || tradeData.TpMode == TakeProfitType.VReversal) &&
                tradeData.TakeProfit > 0)
            {
                int tpY = (int)ChartInfo.GetYByPrice(tradeData.TakeProfit);

                // V反交易使用不同颜色
                Color tpColor;
                if (tradeData.IsVReversal)
                {
                    tpColor = tradeData.IsLong ?
                        Color.FromArgb(200, 0, 200, 255) : // V反做多止盈
                        Color.FromArgb(200, 0, 150, 255);  // V反做空止盈
                }
                else
                {
                    tpColor = Color.FromArgb(200, TakeProfitColor.R, TakeProfitColor.G, TakeProfitColor.B);
                }

                var tpPen = new RenderPen(tpColor, _lineThickness);
                context.DrawLine(tpPen, startX, tpY, endX, tpY);

                int minY = Math.Min(slY, tpY);
                int maxY = Math.Max(slY, tpY);
                context.DrawLine(verticalPen, startX, minY, startX, maxY);

                DrawPriceLabels(context, tradeData, endX, slY, tpY);
            }
            else
            {
                context.DrawLine(verticalPen, startX, slY, startX, entryY);
                DrawPriceLabels(context, tradeData, endX, slY, -1);
            }
        }
        catch
        {
        }
    }

    private void DrawCurrentTradeLines(RenderContext context)
    {
        if (_currentTrade == null) return;

        try
        {
            int startX = ChartInfo.GetXByBar(_currentTrade.EntryBar);
            int endX = ChartInfo.Region.Width;

            int entryY = (int)ChartInfo.GetYByPrice(_currentTrade.EntryPrice);
            int slY = (int)ChartInfo.GetYByPrice(_currentTrade.StopLoss);

            var verticalColor = _currentTrade.IsVReversal ?
                Color.FromArgb(200, 255, 200, 0) : // V反交易用橙色
                Color.FromArgb(200, 200, 200, 0);  // 普通交易用黄色
            var verticalPen = new RenderPen(verticalColor, 2);

            // V反交易使用不同颜色
            Color slColor;
            if (_currentTrade.IsVReversal)
            {
                slColor = _currentTrade.IsLong ?
                    Color.FromArgb(255, 255, 100, 0) : // V反做多止损
                    Color.FromArgb(255, 255, 150, 0);  // V反做空止损
            }
            else
            {
                slColor = Color.FromArgb(255, StopLossColor.R, StopLossColor.G, StopLossColor.B);
            }
            var slPen = new RenderPen(slColor, _lineThickness + 1);
            context.DrawLine(slPen, startX, slY, endX, slY);

            if ((_currentTrade.TpMode == TakeProfitType.RiskReward || _currentTrade.TpMode == TakeProfitType.VReversal) &&
                _currentTrade.TakeProfit > 0)
            {
                int tpY = (int)ChartInfo.GetYByPrice(_currentTrade.TakeProfit);

                // V反交易使用不同颜色
                Color tpColor;
                if (_currentTrade.IsVReversal)
                {
                    tpColor = _currentTrade.IsLong ?
                        Color.FromArgb(255, 0, 200, 255) : // V反做多止盈
                        Color.FromArgb(255, 0, 150, 255);  // V反做空止盈
                }
                else
                {
                    tpColor = Color.FromArgb(255, TakeProfitColor.R, TakeProfitColor.G, TakeProfitColor.B);
                }

                var tpPen = new RenderPen(tpColor, _lineThickness + 1);
                context.DrawLine(tpPen, startX, tpY, endX, tpY);

                int minY = Math.Min(slY, tpY);
                int maxY = Math.Max(slY, tpY);
                context.DrawLine(verticalPen, startX, minY, startX, maxY);

                var tradeData = new TradeLinesData
                {
                    StopLoss = _currentTrade.StopLoss,
                    TakeProfit = _currentTrade.TakeProfit,
                    TpMode = _currentTrade.TpMode,
                    IsVReversal = _currentTrade.IsVReversal
                };
                DrawPriceLabels(context, tradeData, endX - 60, slY, tpY);
            }
            else
            {
                context.DrawLine(verticalPen, startX, slY, startX, entryY);

                var tradeData = new TradeLinesData
                {
                    StopLoss = _currentTrade.StopLoss,
                    TakeProfit = 0,
                    TpMode = _currentTrade.TpMode,
                    IsVReversal = _currentTrade.IsVReversal
                };
                DrawPriceLabels(context, tradeData, endX - 60, slY, -1);
            }
        }
        catch
        {
        }
    }

    private void DrawPriceLabels(RenderContext context, TradeLinesData tradeData, int endX, int slY, int tpY)
    {
        var textColor = Color.White;
        var font = new RenderFont("Arial", 8);
        var bgColor = Color.FromArgb(220, 0, 0, 0);

        string slLabel = $"SL:{tradeData.StopLoss:F1}";
        if (tradeData.IsVReversal) slLabel = "V-" + slLabel;

        var slLabelSize = context.MeasureString(slLabel, font);

        int slLabelX = Math.Min(endX + 3, ChartInfo.Region.Width - slLabelSize.Width - 5);
        int slLabelY = Math.Max(5, Math.Min(slY - slLabelSize.Height / 2, ChartInfo.Region.Height - slLabelSize.Height - 5));

        var slRect = new Rectangle(slLabelX - 2, slLabelY - 1, slLabelSize.Width + 4, slLabelSize.Height + 2);
        context.FillRectangle(bgColor, slRect);
        context.DrawString(slLabel, font, textColor, slLabelX, slLabelY);

        if (tradeData.TpMode == TakeProfitType.RiskReward || tradeData.TpMode == TakeProfitType.VReversal && tpY > 0)
        {
            string tpLabel = $"TP:{tradeData.TakeProfit:F1}";
            if (tradeData.IsVReversal) tpLabel = "V-" + tpLabel;

            var tpLabelSize = context.MeasureString(tpLabel, font);

            int tpLabelX = Math.Min(endX + 3, ChartInfo.Region.Width - tpLabelSize.Width - 5);
            int tpLabelY = Math.Max(5, Math.Min(tpY - tpLabelSize.Height / 2, ChartInfo.Region.Height - tpLabelSize.Height - 5));

            var tpRect = new Rectangle(tpLabelX - 2, tpLabelY - 1, tpLabelSize.Width + 4, tpLabelSize.Height + 2);
            context.FillRectangle(bgColor, tpRect);
            context.DrawString(tpLabel, font, textColor, tpLabelX, tpLabelY);
        }
    }

    private void DrawEntryExitMarkers(RenderContext context)
    {
        foreach (var trade in _trades)
        {
            if (trade.EntryBar >= FirstVisibleBarNumber && trade.EntryBar <= LastVisibleBarNumber)
            {
                int entryX = ChartInfo.GetXByBar(trade.EntryBar);
                int entryY = (int)ChartInfo.GetYByPrice(trade.EntryPrice);

                Color entryColor;
                if (trade.IsVReversal)
                {
                    // V反交易使用专用颜色
                    entryColor = trade.IsLong ?
                        Color.FromArgb(255, _vReversalLongColor.R, _vReversalLongColor.G, _vReversalLongColor.B) :
                        Color.FromArgb(255, _vReversalShortColor.R, _vReversalShortColor.G, _vReversalShortColor.B);
                }
                else
                {
                    entryColor = trade.IsLong ?
                        Color.FromArgb(255, LongColor.R, LongColor.G, LongColor.B) :
                        Color.FromArgb(255, ShortColor.R, ShortColor.G, ShortColor.B);
                }

                if (trade.IsLong)
                {
                    Point[] arrow = {
                        new Point(entryX, entryY - 10),
                        new Point(entryX - 5, entryY),
                        new Point(entryX + 5, entryY)
                    };
                    context.FillPolygon(entryColor, arrow);

                    // V反交易添加特殊标记
                    if (trade.IsVReversal)
                    {
                        context.DrawString("V", new RenderFont("Arial", 7, FontStyle.Bold),
                                          Color.White, entryX - 5, entryY - 20);
                    }
                }
                else
                {
                    Point[] arrow = {
                        new Point(entryX, entryY + 10),
                        new Point(entryX - 5, entryY),
                        new Point(entryX + 5, entryY)
                    };
                    context.FillPolygon(entryColor, arrow);

                    // V反交易添加特殊标记
                    if (trade.IsVReversal)
                    {
                        context.DrawString("V", new RenderFont("Arial", 7, FontStyle.Bold),
                                          Color.White, entryX - 5, entryY + 12);
                    }
                }
            }

            if (trade.ExitBar >= FirstVisibleBarNumber && trade.ExitBar <= LastVisibleBarNumber)
            {
                int exitX = ChartInfo.GetXByBar(trade.ExitBar);
                int exitY = (int)ChartInfo.GetYByPrice(trade.ExitPrice);

                var exitColor = trade.IsLong ?
                    Color.FromArgb(255, 100, 255, 100) :
                    Color.FromArgb(255, 255, 100, 100);

                var exitRect = new Rectangle(exitX - 4, exitY - 4, 8, 8);
                context.FillEllipse(exitColor, exitRect);

                // V反交易退出标记
                if (trade.IsVReversal)
                {
                    context.DrawString("V", new RenderFont("Arial", 6, FontStyle.Bold),
                                      Color.White, exitX - 3, exitY - 8);
                }
            }
        }
    }

    private void DrawStatisticsPanel(RenderContext context)
    {
        try
        {
            var textColor = Color.White;
            var bgColor = Color.FromArgb(240, 0, 0, 0);
            var font = new RenderFont("Arial", 10);
            var boldFont = new RenderFont("Arial", 12, FontStyle.Bold);
            var smallFont = new RenderFont("Arial", 9);

            string startDateStr = _startDateAll != DateTime.MaxValue ? _startDateAll.ToString("yyyy-MM-dd") : "N/A";
            string endDateStr = _endDateAll != DateTime.MinValue ? _endDateAll.ToString("yyyy-MM-dd") : "N/A";

            string tpModeStr = _tpType == TakeProfitType.Signal ? "Signal (Wave)" :
                              _tpType == TakeProfitType.VReversal ? $"V-Reversal RR {_riskRewardRatio:F1}:1" :
                              $"RR {_riskRewardRatio:F1}:1 (Counter)";
            string strategyType = _tpType == TakeProfitType.Signal ? "波段策略" :
                                 _tpType == TakeProfitType.VReversal ? "V反交易策略" :
                                 "反向策略";

            // Calculate risk statistics
            int highRiskTrades = _trades.Count(t => t.RiskAmount > _maxRiskPerTrade);
            decimal avgRisk = _trades.Count > 0 ? _trades.Average(t => t.RiskAmount) : 0;
            int totalSegments = _currentTrendSegment;
            decimal segmentCoverage = totalSegments > 0 ? (_totalTrades * 100m / totalSegments) : 0;

            // Calculate V-Reversal statistics
            int vReversalSignals = _vReversalSignals.Count;
            int vReversalConfirmed = _vReversalSignals.Count(v => v.Value.Confirmed);
            int vReversalExecuted = _vReversalSignals.Count(v => v.Value.Executed);
            decimal vReversalConfirmRate = vReversalSignals > 0 ? (vReversalConfirmed * 100m / vReversalSignals) : 0;
            decimal vReversalExecutionRate = vReversalConfirmed > 0 ? (vReversalExecuted * 100m / vReversalConfirmed) : 0;

            string[] statsLines = {
                "=== RMI TREND SNIPER BACKTEST ===",
                "",
                $"Period: {startDateStr} to {endDateStr}",
                $"Strategy: {strategyType}",
                "",
                "--- ACCOUNT ---",
                $"Initial Capital: ${_initialCapital:F2}",
                $"Current Equity: ${_currentEquity:F2}",
                $"Net P/L: ${_totalProfitAmount:F2}",
                $"Return: {(_initialCapital > 0 ? (_totalProfitAmount / _initialCapital * 100) : 0):F2}%",
                "",
                "--- TRADES ---",
                $"Total Trades: {_totalTrades}",
                $"Long: {_longTrades} | Short: {_shortTrades}",
                $"Winners: {_winningTrades} | Losers: {_losingTrades}",
                $"Win Rate: {_winRate:F1}%",
                $"Trend Segments: {totalSegments}",
                $"Segment Coverage: {segmentCoverage:F1}%",
                "",
                "--- PERFORMANCE ---",
                $"Total Ticks: {_totalProfitTicks:F1}",
                $"Tick Value: ${_tickValue:F2}",
                $"Avg P/L: ${_avgProfit:F2}",
                $"Profit Factor: {_profitFactor:F2}",
                $"Sharpe Ratio: {_sharpeRatio:F2}",
                "",
                $"Max Profit: ${_maxProfit:F2}",
                $"Max Loss: ${_maxLoss:F2}",
                $"Max Drawdown: {_maxDrawdown:F1}% (${_maxDrawdownAmount:F2})",
                "",
                "--- WINNING STREAK ---",
                $"Max Consecutive Wins: {_maxConsecutiveWins}",
                $"Max Win Streak Profit: ${_maxConsecutiveWinsProfit:F2}",
                $"Max Win Streak Duration: {FormatDuration(_maxConsecutiveWinsDuration)}",
                "",
                "--- LOSING STREAK ---",
                $"Max Consecutive Losses: {_maxConsecutiveLosses}",
                $"Max Loss Streak Amount: ${_maxConsecutiveLossesAmount:F2}",
                $"Max Loss Streak Duration: {FormatDuration(_maxConsecutiveLossesDuration)}",
                "",
                "--- RISK MANAGEMENT ---",
                $"Max Risk/Trade: ${_maxRiskPerTrade:F0}",
                $"Avg Risk/Trade: ${avgRisk:F0}",
                $"High Risk Trades: {highRiskTrades}",
                ""
            };

            // Add V-Reversal statistics if in V-Reversal mode
            List<string> allLines = new List<string>(statsLines);
            if (_tpType == TakeProfitType.VReversal)
            {
                allLines.Add("--- V-REVERSAL STATS ---");
                allLines.Add($"V-Reversal Trades: {_vReversalTrades}");
                allLines.Add($"V-Reversal Winners: {_vReversalWinners}");
                allLines.Add($"V-Reversal Losers: {_vReversalLosers}");
                allLines.Add($"V-Reversal Win Rate: {_vReversalWinRate:F1}%");
                allLines.Add($"V-Reversal Total P/L: ${_vReversalTotalProfit:F2}");
                allLines.Add($"V-Reversal Signals: {vReversalSignals}");
                allLines.Add($"Confirmed: {vReversalConfirmed} ({vReversalConfirmRate:F1}%)");
                allLines.Add($"Executed: {vReversalExecuted} ({vReversalExecutionRate:F1}%)");
                allLines.Add("");
                allLines.Add("--- V-REVERSAL PARAMETERS ---");
                allLines.Add($"Lookback: {_vReversalLookback} bars");
                allLines.Add($"Retracement: {_vReversalThreshold * 100:F0}%");
                allLines.Add($"RMI Movement: ≥{_vReversalRmiMovement:F1} points");
                allLines.Add($"Confirmation: ≥{_vReversalConfirmation * 100:F0}%");
                allLines.Add("");
            }

            allLines.Add("--- SETTINGS ---");
            allLines.Add($"Stop Loss: ATR x {_atrMultiplier}");
            allLines.Add($"TP Mode: {tpModeStr}");
            allLines.Add($"Confirmation: {_waitBars} bars");
            allLines.Add($"Commission: ${_commission}/trade");
            allLines.Add("");
            allLines.Add("--- EXPORT ---");
            allLines.Add("📊 Click Export Button to Save CSV");

            // Add export status message if exists
            if (!string.IsNullOrEmpty(_exportStatusMessage) &&
                (DateTime.Now - _exportStatusTime).TotalSeconds < 10)
            {
                allLines.Add("");
                allLines.Add(_exportStatusMessage);
            }

            int maxWidth = 0;
            int totalHeight = 0;
            foreach (var line in allLines)
            {
                var lineFont = line.Contains("===") ? boldFont : line.Contains("---") ? font : smallFont;
                var size = context.MeasureString(line, lineFont);
                if (size.Width > maxWidth)
                    maxWidth = size.Width + 30;
                totalHeight += size.Height + (line == "" ? 2 : 4);
            }

            int panelX = 10;
            int panelY = 10;
            int padding = 18;

            var panelRect = new Rectangle(panelX, panelY, maxWidth + padding * 2, totalHeight + padding * 2);
            context.FillRectangle(bgColor, panelRect);

            var borderPen = new RenderPen(Color.FromArgb(180, 100, 100, 100), 2);
            context.DrawRectangle(borderPen, panelRect);

            int currentY = panelY + padding;
            foreach (var line in allLines)
            {
                if (line == "")
                {
                    currentY += 2;
                    continue;
                }

                var lineFont = line.Contains("===") ? boldFont : line.Contains("---") ? font : smallFont;
                Color lineColor;

                if (line.Contains("==="))
                    lineColor = Color.FromArgb(255, 0, 200, 255);
                else if (line.Contains("---"))
                    lineColor = Color.FromArgb(255, 200, 200, 200);
                else if (line == _exportStatusMessage)
                    lineColor = line.Contains("✓") ? Color.FromArgb(255, 100, 255, 100) : Color.FromArgb(255, 255, 100, 100);
                else if (line.Contains("V-REVERSAL"))
                    lineColor = Color.FromArgb(255, 255, 200, 0); // V反统计用黄色
                else
                    lineColor = textColor;

                context.DrawString(line, lineFont, lineColor, panelX + padding, currentY);
                currentY += context.MeasureString(line, lineFont).Height + 4;
            }
        }
        catch
        {
        }
    }
    #endregion
}