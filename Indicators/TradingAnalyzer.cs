using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RemoteIndicatorATAS_standalone.Indicators
{
    /// <summary>
    /// 交易分析器 - 对交易记录进行全面量化分析
    /// </summary>
    public class TradingAnalyzer
    {
        #region 常量配置（日内交易阈值） - 已移至AnalysisConfig，保留向后兼容

        // 默认值（如果AnalysisConfig未设置）
        private const decimal DEFAULT_DAILY_LOSS_LIMIT_PCT = 0.02m;  // 2%
        private const int DEFAULT_OVERTRADING_THRESHOLD = 15;
        private const double DEFAULT_REVENGE_TRADE_WINDOW_MINUTES = 5.0;
        private const decimal DEFAULT_RISK_FREE_RATE_ANNUAL = 0.03m;  // 3%年化无风险利率

        #endregion

        #region 数据结构

        /// <summary>
        /// 交易记录（从CSV或内部数据加载）
        /// </summary>
        public class TradeRecord
        {
            public DateTime OpenTime { get; set; }
            public DateTime CloseTime { get; set; }
            public string Direction { get; set; }  // "Long" or "Short"
            public decimal ActualPnL { get; set; }  // 价格差盈亏
            public decimal TickSize { get; set; }

            // 时间相关（日内交易）
            public int HoldingBars { get; set; }  // 兼容性保留
            public TimeSpan HoldingTime => CloseTime - OpenTime;
            public double HoldingMinutes => HoldingTime.TotalMinutes;
            public double HoldingHours => HoldingTime.TotalHours;

            public string ExitReason { get; set; }

            // 价格信息
            public decimal OpenPrice { get; set; }
            public decimal ClosePrice { get; set; }
            public decimal TakeProfitPrice { get; set; }
            public decimal StopLossPrice { get; set; }

            // 日内交易特定字段
            public decimal Slippage { get; set; }  // 滑点（实际成交价 - 预期价格）
            public string TradingSession { get; set; }  // "Opening", "Midday", "Closing", "PreMarket", "PostMarket"
            public bool IsFirstTradeOfDay { get; set; }
            public bool IsLastTradeOfDay { get; set; }
            public int TradeNumberToday { get; set; }  // 当天第几笔交易

            // MAE/MFE（最大不利/有利偏移）
            public decimal MAE { get; set; }  // Maximum Adverse Excursion（最大浮亏）
            public decimal MFE { get; set; }  // Maximum Favorable Excursion（最大浮盈）
            public decimal MAETicks => TickSize > 0 ? Math.Abs(MAE) / TickSize : 0;
            public decimal MFETicks => TickSize > 0 ? Math.Abs(MFE) / TickSize : 0;

            // 计算字段
            public decimal PnLTicks => TickSize > 0 ? ActualPnL / TickSize : 0;
            public decimal PnLDollars { get; set; }
            public decimal PnLWithFee { get; set; }
            public decimal PnLNoFee { get; set; }

            // 衍生指标
            public decimal MFERealizationRate => MFE > 0 ? ActualPnL / MFE : 0;  // 盈利实现率
            public bool MAEViolation => Math.Abs(MAE) > Math.Abs(OpenPrice - StopLossPrice) * 1.1m;  // MAE超过止损距离10%
        }

        /// <summary>
        /// 分析配置
        /// </summary>
        public class AnalysisConfig
        {
            public decimal TickValue { get; set; } = 10.0m;
            public decimal CommissionPerSide { get; set; } = 2.2m;
            public decimal InitialCapital { get; set; } = 5000.0m;
            public string Symbol { get; set; } = "GC";

            // 新增：可配置的风控参数
            public decimal DailyLossLimitPct { get; set; } = 0.02m;  // 2%
            public int OvertradingThreshold { get; set; } = 15;
            public double RevengeTradeWindowMinutes { get; set; } = 5.0;
            public decimal RiskFreeRateAnnual { get; set; } = 0.03m;  // 3%年化
        }

        /// <summary>
        /// 热力图数据（入场时间 × 持仓时长）
        /// 用于分析不同入场时间和持仓时长组合的表现
        /// </summary>
        public class EntryHoldingHeatmap
        {
            public List<string> TimeSlots { get; set; }  // X轴：入场时间段（如"09:30-10:00"）
            public List<string> HoldingBuckets { get; set; }  // Y轴：持仓时长分组（如"<5min", "5-15min"）
            public decimal[,] AvgPnL { get; set; }  // Z值：平均盈亏（主指标）
            public int[,] TradeCounts { get; set; }  // 交易笔数（用于判断统计有效性）
            public decimal[,] WinRates { get; set; }  // 胜率（辅助指标）
        }

        /// <summary>
        /// 热力图数据（时段 × 星期）
        /// 用于分析每周不同时段的表现模式
        /// </summary>
        public class SessionDayHeatmap
        {
            public List<string> DaysOfWeek { get; set; }  // X轴：星期（Monday-Friday）
            public List<string> Sessions { get; set; }  // Y轴：时段（Opening, Midday, Closing, Extended）
            public decimal[,] AvgPnL { get; set; }  // Z值：平均盈亏
            public int[,] TradeCounts { get; set; }  // 交易笔数
            public decimal[,] WinRates { get; set; }  // 胜率
        }

        /// <summary>
        /// 回撤点
        /// </summary>
        public class DrawdownPoint
        {
            public DateTime Time { get; set; }
            public decimal Equity { get; set; }
            public decimal RunningMax { get; set; }
            public decimal Drawdown { get; set; }       // 绝对回撤
            public decimal DrawdownPct { get; set; }    // 百分比回撤
        }

        /// <summary>
        /// 滚动指标点
        /// </summary>
        public class RollingMetric
        {
            public DateTime EndTime { get; set; }
            public decimal WinRate { get; set; }
            public decimal AvgPnL { get; set; }
            public decimal SharpeRatio { get; set; }
            public decimal ProfitFactor { get; set; }
        }

        /// <summary>
        /// 连胜/连亏序列
        /// </summary>
        public class Streak
        {
            public string Type { get; set; }  // "Win" or "Loss"
            public int Length { get; set; }
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public decimal TotalPnL { get; set; }
        }

        /// <summary>
        /// 日度收益（日内交易）
        /// </summary>
        public class DailyReturn
        {
            public DateTime Date { get; set; }
            public decimal TotalPnL { get; set; }
            public decimal ReturnPct { get; set; }
            public int TradeCount { get; set; }
            public decimal WinRate { get; set; }
            public decimal MaxIntradayDrawdown { get; set; }  // 日内最大回撤
            public bool HitDailyLossLimit { get; set; }
        }

        /// <summary>
        /// 月度收益
        /// </summary>
        public class MonthlyReturn
        {
            public int Year { get; set; }
            public int Month { get; set; }
            public decimal TotalPnL { get; set; }
            public decimal ReturnPct { get; set; }
            public int TradeCount { get; set; }
            public decimal WinRate { get; set; }
        }

        /// <summary>
        /// 分析结果
        /// </summary>
        public class AnalysisMetrics
        {
            // 基础统计
            public int TotalTrades { get; set; }
            public int WinningTrades { get; set; }
            public int LosingTrades { get; set; }
            public int BreakevenTrades { get; set; }
            public decimal WinRate { get; set; }

            // 收益指标（0手续费）
            public decimal NetProfitNoFee { get; set; }
            public decimal GrossProfitNoFee { get; set; }
            public decimal GrossLossNoFee { get; set; }

            // 收益指标（含手续费）
            public decimal NetProfitWithFee { get; set; }
            public decimal GrossProfitWithFee { get; set; }
            public decimal GrossLossWithFee { get; set; }

            // 盈亏比
            public decimal AverageWin { get; set; }
            public decimal AverageLoss { get; set; }
            public decimal ProfitFactor { get; set; }  // 盈亏比
            public decimal RiskRewardRatio { get; set; }  // 风报比

            // 回撤指标
            public decimal MaxDrawdown { get; set; }
            public decimal MaxDrawdownPct { get; set; }
            public decimal LargestWin { get; set; }
            public decimal LargestLoss { get; set; }

            // 时间指标
            public decimal AvgHoldingBars { get; set; }
            public int TradingDays { get; set; }
            public decimal AvgTradesPerDay { get; set; }

            // 高级指标（风险调整收益）
            public decimal QualityRatio { get; set; }  // 原SharpeRatioPerTrade，重命名避免混淆
            public decimal SharpeRatioDaily { get; set; }
            public decimal AnnualizedSharpe { get; set; }
            public decimal AnnualizedReturn { get; set; }
            public decimal CalmarRatio { get; set; }

            // 新增：实际年化Sharpe（考虑实际交易天数）
            public decimal RealizedAnnualizedSharpe { get; set; }

            // 权益曲线
            public List<decimal> EquityCurveNoFee { get; set; }
            public List<decimal> EquityCurveWithFee { get; set; }

            // 统计指标（扩展）
            public decimal Skewness { get; set; }
            public decimal Kurtosis { get; set; }
            public decimal Percentile5 { get; set; }
            public decimal Percentile25 { get; set; }
            public decimal Median { get; set; }
            public decimal Percentile75 { get; set; }
            public decimal Percentile95 { get; set; }

            // 回撤详细信息
            public List<DrawdownPoint> DrawdownCurve { get; set; }
            public DateTime MaxDDStartTime { get; set; }
            public DateTime MaxDDEndTime { get; set; }
            public DateTime MaxDDRecoveryTime { get; set; }
            public int MaxDDDurationDays { get; set; }  // 兼容性保留
            public int MaxDDRecoveryDays { get; set; }  // 兼容性保留
            // 日内交易：以小时/分钟为单位
            public double MaxDDDurationHours { get; set; }
            public double MaxDDDurationMinutes { get; set; }
            public double MaxDDRecoveryHours { get; set; }
            public double MaxDDRecoveryMinutes { get; set; }

            // 滚动指标
            public List<RollingMetric> RollingMetrics { get; set; }

            // 连胜连亏
            public List<Streak> Streaks { get; set; }
            public int MaxWinStreak { get; set; }
            public int MaxLossStreak { get; set; }
            public decimal AvgWinStreakLength { get; set; }
            public decimal AvgLossStreakLength { get; set; }

            // 日度收益（日内交易）
            public List<DailyReturn> DailyReturns { get; set; }
            public decimal BestDayReturn { get; set; }
            public decimal WorstDayReturn { get; set; }
            public int WinningDays { get; set; }
            public int LosingDays { get; set; }
            public decimal AvgDailyReturn { get; set; }
            //public decimal AvgTradesPerDay { get; set; }

            // 月度收益
            public List<MonthlyReturn> MonthlyReturns { get; set; }
            public decimal BestMonthReturn { get; set; }
            public decimal WorstMonthReturn { get; set; }
            public int WinningMonths { get; set; }
            public int LosingMonths { get; set; }

            // Sortino Ratio (只考虑下行波动)
            public decimal SortinoRatio { get; set; }

            // 手续费分析（日内交易关键）
            public decimal CommissionPctOfGrossProfit { get; set; }  // 手续费占总盈利的百分比
            public decimal BreakevenWinRate { get; set; }  // 盈亏平衡所需胜率
            public decimal TotalCommissionPaid { get; set; }
            public decimal AvgCommissionPerTrade { get; set; }

            // 行为指标（心理/纪律）
            public int RevengeTrades { get; set; }  // 亏损后立即开仓的次数
            public decimal PostLossWinRate { get; set; }  // 亏损后下一笔的胜率
            public int DaysWithOvertrading { get; set; }  // 过度交易的天数
            public int MaxTradesInDay { get; set; }  // 单日最多交易笔数
            public decimal MorningWinRate { get; set; }  // 早盘胜率
            public decimal AfternoonWinRate { get; set; }  // 午后胜率

            // 新增：MAE/MFE分析
            public decimal AvgMAE { get; set; }  // 平均最大不利偏移
            public decimal AvgMFE { get; set; }  // 平均最大有利偏移
            public decimal AvgMAETicks { get; set; }
            public decimal AvgMFETicks { get; set; }
            public decimal AvgMFERealizationRate { get; set; }  // 平均盈利实现率（实际盈利/MFE）
            public decimal MAEViolationRate { get; set; }  // MAE超止损的比例
            public int MAEViolationCount { get; set; }

            // 新增：持仓时间分布（日内交易关键）
            public int TradesUnder5Min { get; set; }
            public int Trades5to15Min { get; set; }
            public int Trades15to30Min { get; set; }
            public int Trades30to60Min { get; set; }
            public int TradesOver60Min { get; set; }
            public decimal WinRateUnder5Min { get; set; }
            public decimal WinRate5to15Min { get; set; }
            public decimal WinRate15to30Min { get; set; }
            public decimal WinRate30to60Min { get; set; }
            public decimal WinRateOver60Min { get; set; }

            // 新增：资金管理指标
            public decimal KellyPercentage { get; set; }  // Kelly最优仓位百分比
            public decimal RiskOfRuin { get; set; }  // 破产风险（基于连续亏损）

            // 新增：滑点分析
            public decimal AvgSlippage { get; set; }
            public decimal AvgSlippageTicks { get; set; }
            public decimal SlippagePctOfProfit { get; set; }  // 滑点占利润的百分比

            // 新增：时段细分（开盘/收盘）
            public int TradesInOpeningPeriod { get; set; }  // 开盘后30分钟
            public int TradesInClosingPeriod { get; set; }  // 收盘前30分钟
            public decimal OpeningPeriodWinRate { get; set; }
            public decimal ClosingPeriodWinRate { get; set; }
            public decimal OpeningPeriodAvgPnL { get; set; }
            public decimal ClosingPeriodAvgPnL { get; set; }

            // 新增：序列相关性
            public decimal Autocorrelation { get; set; }  // Lag-1自相关系数
            public string AutocorrelationInterpretation { get; set; }  // "趋势追踪"/"均值回归"/"随机"
        }

        #endregion

        private readonly AnalysisConfig _config;

        public TradingAnalyzer(AnalysisConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// 执行完整分析
        /// </summary>
        public AnalysisMetrics Analyze(List<TradeRecord> trades)
        {
            if (trades == null || trades.Count == 0)
                return new AnalysisMetrics();

            // 数据验证
            ValidateTradeRecords(trades);

            // 计算每笔交易的美元盈亏
            foreach (var trade in trades)
            {
                trade.PnLDollars = trade.PnLTicks * _config.TickValue;
                trade.PnLNoFee = trade.PnLDollars;
                trade.PnLWithFee = trade.PnLDollars - (_config.CommissionPerSide * 2);
            }

            var metrics = new AnalysisMetrics();

            // 基础统计
            CalculateBasicStats(trades, metrics);

            // 收益指标
            CalculateProfitMetrics(trades, metrics);

            // 回撤指标
            CalculateDrawdownMetrics(trades, metrics);

            // 时间指标
            CalculateTimeMetrics(trades, metrics);

            // 高级指标（包含Sharpe, Sortino, 统计量等）
            CalculateAdvancedMetrics(trades, metrics);

            // 权益曲线
            CalculateEquityCurve(trades, metrics);

            // 滚动指标（30笔交易窗口）
            CalculateRollingMetrics(trades, metrics, windowSize: 30);

            // 连胜连亏分析
            CalculateStreaks(trades, metrics);

            // 月度收益统计
            CalculateMonthlyReturns(trades, metrics);

            // 日内交易：日度收益分析
            CalculateDailyReturns(trades, metrics);

            // 日内交易：手续费影响分析
            CalculateCommissionAnalysis(trades, metrics);

            // 日内交易：行为指标（Tilt、过度交易）
            CalculateBehavioralMetrics(trades, metrics);

            // 新增：MAE/MFE分析
            CalculateMAEMFE(trades, metrics);

            // 新增：持仓时间分布分析
            CalculateHoldingTimeDistribution(trades, metrics);

            // 新增：Kelly比例和破产风险
            CalculateRiskManagementMetrics(trades, metrics);

            // 新增：滑点分析
            CalculateSlippageAnalysis(trades, metrics);

            // 新增：时段细分分析
            CalculateTimingAnalysis(trades, metrics);

            // 新增：自相关分析
            CalculateAutocorrelation(trades, metrics);

            return metrics;
        }

        #region 计算方法

        private void CalculateBasicStats(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            metrics.TotalTrades = trades.Count;
            metrics.WinningTrades = trades.Count(t => t.PnLWithFee > 0);
            metrics.LosingTrades = trades.Count(t => t.PnLWithFee < 0);
            metrics.BreakevenTrades = trades.Count(t => t.PnLWithFee == 0);
            metrics.WinRate = metrics.TotalTrades > 0 ? (decimal)metrics.WinningTrades / metrics.TotalTrades * 100 : 0;
        }

        private void CalculateProfitMetrics(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            // 0手续费
            var winningTradesNoFee = trades.Where(t => t.PnLNoFee > 0).ToList();
            var losingTradesNoFee = trades.Where(t => t.PnLNoFee < 0).ToList();

            metrics.GrossProfitNoFee = winningTradesNoFee.Sum(t => t.PnLNoFee);
            metrics.GrossLossNoFee = losingTradesNoFee.Sum(t => t.PnLNoFee);  // 负数
            metrics.NetProfitNoFee = metrics.GrossProfitNoFee + metrics.GrossLossNoFee;

            // 含手续费
            var winningTradesWithFee = trades.Where(t => t.PnLWithFee > 0).ToList();
            var losingTradesWithFee = trades.Where(t => t.PnLWithFee < 0).ToList();

            metrics.GrossProfitWithFee = winningTradesWithFee.Sum(t => t.PnLWithFee);
            metrics.GrossLossWithFee = losingTradesWithFee.Sum(t => t.PnLWithFee);  // 负数
            metrics.NetProfitWithFee = metrics.GrossProfitWithFee + metrics.GrossLossWithFee;

            // 盈亏比分析
            if (winningTradesWithFee.Count > 0)
                metrics.AverageWin = metrics.GrossProfitWithFee / winningTradesWithFee.Count;

            if (losingTradesWithFee.Count > 0)
                metrics.AverageLoss = Math.Abs(metrics.GrossLossWithFee) / losingTradesWithFee.Count;

            if (Math.Abs(metrics.GrossLossWithFee) > 0.001m)
                metrics.ProfitFactor = metrics.GrossProfitWithFee / Math.Abs(metrics.GrossLossWithFee);

            if (metrics.AverageLoss > 0.001m)
                metrics.RiskRewardRatio = metrics.AverageWin / metrics.AverageLoss;
        }

        private void CalculateDrawdownMetrics(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            decimal equity = _config.InitialCapital;
            decimal runningMax = _config.InitialCapital;
            DateTime runningMaxTime = trades.Count > 0 ? trades[0].CloseTime : DateTime.MinValue;
            decimal maxDrawdown = 0;
            decimal maxDrawdownPct = 0;

            // 记录完整的回撤曲线
            metrics.DrawdownCurve = new List<DrawdownPoint>();

            DateTime maxDDStart = DateTime.MinValue;
            DateTime maxDDEnd = DateTime.MinValue;
            decimal maxDDEquity = equity;

            foreach (var trade in trades)
            {
                equity += trade.PnLWithFee;

                // 更新运行最大值和峰值时间
                if (equity > runningMax)
                {
                    runningMax = equity;
                    runningMaxTime = trade.CloseTime;
                }

                decimal currentDrawdown = runningMax - equity;
                decimal currentDrawdownPct = runningMax > 0 ? currentDrawdown / runningMax : 0;

                // 记录回撤点
                metrics.DrawdownCurve.Add(new DrawdownPoint
                {
                    Time = trade.CloseTime,
                    Equity = equity,
                    RunningMax = runningMax,
                    Drawdown = currentDrawdown,
                    DrawdownPct = currentDrawdownPct * 100
                });

                // 追踪最大回撤
                if (currentDrawdown > maxDrawdown)
                {
                    maxDrawdown = currentDrawdown;
                    maxDrawdownPct = currentDrawdownPct;
                    maxDDStart = runningMaxTime;  // 直接使用记录的峰值时间
                    maxDDEnd = trade.CloseTime;
                    maxDDEquity = equity;
                }
            }

            metrics.MaxDrawdown = maxDrawdown;
            metrics.MaxDrawdownPct = maxDrawdownPct * 100;  // 转换为百分比
            metrics.MaxDDStartTime = maxDDStart;
            metrics.MaxDDEndTime = maxDDEnd;

            // 计算回撤持续时间（支持日内交易的精细时间单位）
            if (maxDDStart != DateTime.MinValue && maxDDEnd != DateTime.MinValue)
            {
                TimeSpan ddDuration = maxDDEnd - maxDDStart;
                metrics.MaxDDDurationDays = (int)ddDuration.TotalDays;
                metrics.MaxDDDurationHours = ddDuration.TotalHours;
                metrics.MaxDDDurationMinutes = ddDuration.TotalMinutes;

                // 查找恢复时间（回撤后重新达到峰值的时间）
                decimal peakEquity = equity + maxDrawdown;  // 峰值权益
                DateTime? recoveryTime = null;

                for (int i = 0; i < metrics.DrawdownCurve.Count; i++)
                {
                    if (metrics.DrawdownCurve[i].Time > maxDDEnd &&
                        metrics.DrawdownCurve[i].Equity >= peakEquity)
                    {
                        recoveryTime = metrics.DrawdownCurve[i].Time;
                        break;
                    }
                }

                if (recoveryTime.HasValue)
                {
                    TimeSpan recoveryDuration = recoveryTime.Value - maxDDEnd;
                    metrics.MaxDDRecoveryTime = recoveryTime.Value;
                    metrics.MaxDDRecoveryDays = (int)recoveryDuration.TotalDays;
                    metrics.MaxDDRecoveryHours = recoveryDuration.TotalHours;
                    metrics.MaxDDRecoveryMinutes = recoveryDuration.TotalMinutes;
                }
                else
                {
                    // 尚未恢复
                    metrics.MaxDDRecoveryTime = DateTime.MinValue;
                    metrics.MaxDDRecoveryDays = -1;
                    metrics.MaxDDRecoveryHours = -1;
                    metrics.MaxDDRecoveryMinutes = -1;
                }
            }

            // 最大单笔盈利/亏损
            if (trades.Count > 0)
            {
                metrics.LargestWin = trades.Max(t => t.PnLWithFee);
                metrics.LargestLoss = trades.Min(t => t.PnLWithFee);
            }
        }

        private void CalculateTimeMetrics(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count > 0)
            {
                metrics.AvgHoldingBars = (decimal)trades.Average(t => t.HoldingBars); // Explicit cast to decimal

                // 计算交易天数（使用CloseTime，因为盈亏在平仓时实现）
                var uniqueDays = trades.Select(t => t.CloseTime.Date).Distinct().Count();
                metrics.TradingDays = uniqueDays;

                if (uniqueDays > 0)
                    metrics.AvgTradesPerDay = (decimal)trades.Count / uniqueDays;
            }
        }

        private void CalculateAdvancedMetrics(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count == 0) return;

            // Quality Ratio（原按笔Sharpe，重命名避免混淆）
            // 衡量"每次出手"的质量，不可年化，仅用于策略质量对比
            var pnls = trades.Select(t => (double)t.PnLWithFee).ToArray();
            double meanPnL = pnls.Average();
            double stdDevPnL = CalculateStdDev(pnls);

            if (stdDevPnL > 0.001)
                metrics.QualityRatio = (decimal)(meanPnL / stdDevPnL);

            // Sharpe比率（按天）- 标准的Sharpe计算方式
            var dailyPnLs = trades
                .GroupBy(t => t.CloseTime.Date)
                .Select(g => (double)g.Sum(t => t.PnLWithFee))
                .ToArray();

            if (dailyPnLs.Length > 1)
            {
                double meanDaily = dailyPnLs.Average();
                double stdDevDaily = CalculateStdDev(dailyPnLs);

                if (stdDevDaily > 0.001)
                {
                    // 日度Sharpe（简化：无风险利率≈0）
                    metrics.SharpeRatioDaily = (decimal)(meanDaily / stdDevDaily);

                    // 标准年化：假设252个交易日
                    metrics.AnnualizedSharpe = metrics.SharpeRatioDaily * (decimal)Math.Sqrt(252);

                    // 实际年化：考虑实际交易天数
                    // 公式：RealizedAnnualized = Daily × √(实际交易天数 × 252 / 回测总天数)
                    if (trades.Count > 0)
                    {
                        int actualTradingDays = metrics.TradingDays;
                        var firstTradeDate = trades.Min(t => t.CloseTime.Date);
                        var lastTradeDate = trades.Max(t => t.CloseTime.Date);
                        int totalDays = Math.Max(1, (lastTradeDate - firstTradeDate).Days + 1);

                        double tradingDaysPerYear = 252.0 * actualTradingDays / totalDays;
                        metrics.RealizedAnnualizedSharpe = metrics.SharpeRatioDaily * (decimal)Math.Sqrt(tradingDaysPerYear);
                    }
                }
            }

            // Sortino比率（只考虑下行波动）- 修正版
            // 下行偏差公式：√(Σ(min(Return - Target, 0)²) / N_negative)
            // 目标收益率设为0（日内交易常用）
            double targetReturn = 0.0;
            var downsideSquares = pnls.Where(p => p < targetReturn)
                                      .Select(p => Math.Pow(p - targetReturn, 2))
                                      .ToList();

            if (downsideSquares.Count > 0)
            {
                double downsideVariance = downsideSquares.Sum() / downsideSquares.Count;
                double downsideDeviation = Math.Sqrt(downsideVariance);

                if (downsideDeviation > 0.001)
                    metrics.SortinoRatio = (decimal)((meanPnL - targetReturn) / downsideDeviation);
            }
            else
            {
                // 没有负收益，Sortino无法计算或设为极大值
                metrics.SortinoRatio = meanPnL > 0 ? 999.99m : 0m;
            }

            // 年化收益率
            if (metrics.TradingDays > 0)
            {
                decimal returnRate = metrics.NetProfitWithFee / _config.InitialCapital;
                metrics.AnnualizedReturn = returnRate * (365m / metrics.TradingDays) * 100;  // 百分比
            }

            // Calmar比率
            if (metrics.MaxDrawdownPct > 0.001m)
                metrics.CalmarRatio = metrics.AnnualizedReturn / metrics.MaxDrawdownPct;

            // 统计指标
            var pnlList = trades.Select(t => t.PnLWithFee).ToList();
            metrics.Skewness = (decimal)CalculateSkewness(pnls);
            metrics.Kurtosis = (decimal)CalculateKurtosis(pnls);
            metrics.Percentile5 = Percentile(pnlList, 5);
            metrics.Percentile25 = Percentile(pnlList, 25);
            metrics.Median = Percentile(pnlList, 50);
            metrics.Percentile75 = Percentile(pnlList, 75);
            metrics.Percentile95 = Percentile(pnlList, 95);
        }

        private void CalculateRollingMetrics(List<TradeRecord> trades, AnalysisMetrics metrics, int windowSize = 30)
        {
            metrics.RollingMetrics = new List<RollingMetric>();

            if (trades.Count < windowSize) return;

            for (int i = windowSize; i <= trades.Count; i++)
            {
                var windowTrades = trades.Skip(i - windowSize).Take(windowSize).ToList();
                var windowPnLs = windowTrades.Select(t => t.PnLWithFee).ToList();

                // 计算窗口内的指标
                decimal winRate = (decimal)windowTrades.Count(t => t.PnLWithFee > 0) / windowSize * 100;
                decimal avgPnL = windowPnLs.Average();

                // Sharpe Ratio
                var pnlsArray = windowPnLs.Select(p => (double)p).ToArray();
                double sharpe = 0;
                if (pnlsArray.Length > 1)
                {
                    double mean = pnlsArray.Average();
                    double stdDev = CalculateStdDev(pnlsArray);
                    if (stdDev > 0.001)
                        sharpe = mean / stdDev;
                }

                // Profit Factor
                decimal grossProfit = windowTrades.Where(t => t.PnLWithFee > 0).Sum(t => t.PnLWithFee);
                decimal grossLoss = Math.Abs(windowTrades.Where(t => t.PnLWithFee < 0).Sum(t => t.PnLWithFee));
                decimal profitFactor = grossLoss > 0.001m ? grossProfit / grossLoss : 0;

                metrics.RollingMetrics.Add(new RollingMetric
                {
                    EndTime = windowTrades.Last().CloseTime,
                    WinRate = winRate,
                    AvgPnL = avgPnL,
                    SharpeRatio = (decimal)sharpe,
                    ProfitFactor = profitFactor
                });
            }
        }

        private void CalculateStreaks(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            metrics.Streaks = new List<Streak>();

            if (trades.Count == 0) return;

            int currentStreakLength = 1;
            bool isWinStreak = trades[0].PnLWithFee > 0;
            int streakStartIndex = 0;
            decimal streakTotalPnL = trades[0].PnLWithFee;

            for (int i = 1; i < trades.Count; i++)
            {
                bool isWin = trades[i].PnLWithFee > 0;

                if ((isWin && isWinStreak) || (!isWin && !isWinStreak))
                {
                    // 序列继续
                    currentStreakLength++;
                    streakTotalPnL += trades[i].PnLWithFee;
                }
                else
                {
                    // 序列结束，记录
                    metrics.Streaks.Add(new Streak
                    {
                        Type = isWinStreak ? "Win" : "Loss",
                        Length = currentStreakLength,
                        StartIndex = streakStartIndex,
                        EndIndex = i - 1,
                        TotalPnL = streakTotalPnL
                    });

                    // 开始新序列
                    currentStreakLength = 1;
                    isWinStreak = isWin;
                    streakStartIndex = i;
                    streakTotalPnL = trades[i].PnLWithFee;
                }
            }

            // 记录最后一个序列
            metrics.Streaks.Add(new Streak
            {
                Type = isWinStreak ? "Win" : "Loss",
                Length = currentStreakLength,
                StartIndex = streakStartIndex,
                EndIndex = trades.Count - 1,
                TotalPnL = streakTotalPnL
            });

            // 统计连胜连亏
            var winStreaks = metrics.Streaks.Where(s => s.Type == "Win").ToList();
            var lossStreaks = metrics.Streaks.Where(s => s.Type == "Loss").ToList();

            if (winStreaks.Any())
            {
                metrics.MaxWinStreak = winStreaks.Max(s => s.Length);
                metrics.AvgWinStreakLength = (decimal)winStreaks.Average(s => s.Length);
            }

            if (lossStreaks.Any())
            {
                metrics.MaxLossStreak = lossStreaks.Max(s => s.Length);
                metrics.AvgLossStreakLength = (decimal)lossStreaks.Average(s => s.Length);
            }
        }

        private void CalculateMonthlyReturns(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            metrics.MonthlyReturns = new List<MonthlyReturn>();

            if (trades.Count == 0) return;

            // 按月分组
            var monthlyGroups = trades
                .GroupBy(t => new { t.CloseTime.Year, t.CloseTime.Month })
                .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
                .ToList();

            decimal equity = _config.InitialCapital;

            foreach (var group in monthlyGroups)
            {
                decimal monthStartEquity = equity;
                decimal monthPnL = group.Sum(t => t.PnLWithFee);
                equity += monthPnL;

                decimal returnPct = monthStartEquity > 0 ? (monthPnL / monthStartEquity) * 100 : 0;
                decimal winRate = group.Count() > 0 ? (decimal)group.Count(t => t.PnLWithFee > 0) / group.Count() * 100 : 0;

                metrics.MonthlyReturns.Add(new MonthlyReturn
                {
                    Year = group.Key.Year,
                    Month = group.Key.Month,
                    TotalPnL = monthPnL,
                    ReturnPct = returnPct,
                    TradeCount = group.Count(),
                    WinRate = winRate
                });
            }

            if (metrics.MonthlyReturns.Any())
            {
                metrics.BestMonthReturn = metrics.MonthlyReturns.Max(m => m.ReturnPct);
                metrics.WorstMonthReturn = metrics.MonthlyReturns.Min(m => m.ReturnPct);
                metrics.WinningMonths = metrics.MonthlyReturns.Count(m => m.TotalPnL > 0);
                metrics.LosingMonths = metrics.MonthlyReturns.Count(m => m.TotalPnL < 0);
            }
        }

        private void CalculateDailyReturns(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            metrics.DailyReturns = new List<DailyReturn>();

            if (trades.Count == 0) return;

            // 按日分组（使用CloseTime）
            var dailyGroups = trades
                .GroupBy(t => t.CloseTime.Date)
                .OrderBy(g => g.Key)
                .ToList();

            decimal equity = _config.InitialCapital;

            foreach (var group in dailyGroups)
            {
                decimal dayStartEquity = equity;
                var dayTrades = group.OrderBy(t => t.CloseTime).ToList();

                // 计算日内最大回撤
                decimal dayPeak = dayStartEquity;
                decimal maxIntradayDD = 0;

                foreach (var trade in dayTrades)
                {
                    equity += trade.PnLWithFee;
                    dayPeak = Math.Max(dayPeak, equity);
                    decimal currentDD = dayPeak - equity;
                    maxIntradayDD = Math.Max(maxIntradayDD, currentDD);
                }

                decimal dayPnL = group.Sum(t => t.PnLWithFee);
                decimal returnPct = dayStartEquity > 0 ? (dayPnL / dayStartEquity) * 100 : 0;
                decimal winRate = group.Count() > 0 ? (decimal)group.Count(t => t.PnLWithFee > 0) / group.Count() * 100 : 0;

                metrics.DailyReturns.Add(new DailyReturn
                {
                    Date = group.Key,
                    TotalPnL = dayPnL,
                    ReturnPct = returnPct,
                    TradeCount = group.Count(),
                    WinRate = winRate,
                    MaxIntradayDrawdown = maxIntradayDD,
                    HitDailyLossLimit = dayPnL < -(_config.InitialCapital * 0.02m)  // 2% 日亏损限制
                });
            }

            if (metrics.DailyReturns.Any())
            {
                metrics.BestDayReturn = metrics.DailyReturns.Max(d => d.ReturnPct);
                metrics.WorstDayReturn = metrics.DailyReturns.Min(d => d.ReturnPct);
                metrics.WinningDays = metrics.DailyReturns.Count(d => d.TotalPnL > 0);
                metrics.LosingDays = metrics.DailyReturns.Count(d => d.TotalPnL < 0);
                metrics.AvgDailyReturn = metrics.DailyReturns.Average(d => d.ReturnPct);
                metrics.MaxTradesInDay = metrics.DailyReturns.Max(d => d.TradeCount);
                metrics.DaysWithOvertrading = metrics.DailyReturns.Count(d => d.TradeCount > 15);  // 超过15笔视为过度交易
            }
        }

        private void CalculateCommissionAnalysis(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count == 0) return;

            // 总手续费
            decimal totalCommission = trades.Count * (_config.CommissionPerSide * 2);
            metrics.TotalCommissionPaid = totalCommission;
            metrics.AvgCommissionPerTrade = totalCommission / trades.Count;

            // 手续费占原始总盈利的百分比（扣费前）
            if (metrics.GrossProfitNoFee > 0)
            {
                metrics.CommissionPctOfGrossProfit = (totalCommission / metrics.GrossProfitNoFee) * 100;
            }

            // Break-even胜率计算
            // 公式：WR = (AvgLoss + Commission) / (AvgWin + AvgLoss + 2*Commission)
            decimal avgWin = metrics.AverageWin > 0 ? metrics.AverageWin : 0;
            decimal avgLoss = metrics.AverageLoss > 0 ? metrics.AverageLoss : 0;
            decimal commission = _config.CommissionPerSide * 2;

            if (avgWin + avgLoss + 2 * commission > 0)
            {
                metrics.BreakevenWinRate = ((avgLoss + commission) / (avgWin + avgLoss + 2 * commission)) * 100;
            }
        }

        private void CalculateBehavioralMetrics(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count < 2) return;

            // 复仇交易检测：亏损后5分钟内开仓
            int revengeTrades = 0;
            int postLossTrades = 0;
            int postLossWins = 0;

            for (int i = 1; i < trades.Count; i++)
            {
                if (trades[i - 1].PnLWithFee < 0)
                {
                    postLossTrades++;
                    if (trades[i].PnLWithFee > 0)
                        postLossWins++;

                    // 如果在5分钟内开仓，视为复仇交易
                    TimeSpan timeSinceLoss = trades[i].OpenTime - trades[i - 1].CloseTime;
                    if (timeSinceLoss.TotalMinutes < 5)
                    {
                        revengeTrades++;
                    }
                }
            }

            metrics.RevengeTrades = revengeTrades;
            metrics.PostLossWinRate = postLossTrades > 0 ? ((decimal)postLossWins / postLossTrades) * 100 : 0;

            // 早盘 vs 午后表现（以12:00为分界）
            var morningTrades = trades.Where(t => t.OpenTime.Hour < 12).ToList();
            var afternoonTrades = trades.Where(t => t.OpenTime.Hour >= 12).ToList();

            metrics.MorningWinRate = morningTrades.Count > 0
                ? ((decimal)morningTrades.Count(t => t.PnLWithFee > 0) / morningTrades.Count) * 100
                : 0;

            metrics.AfternoonWinRate = afternoonTrades.Count > 0
                ? ((decimal)afternoonTrades.Count(t => t.PnLWithFee > 0) / afternoonTrades.Count) * 100
                : 0;
        }

        /// <summary>
        /// MAE/MFE分析（最大不利/有利偏移）
        /// </summary>
        private void CalculateMAEMFE(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count == 0) return;

            // 统计MAE/MFE
            var maes = trades.Select(t => t.MAE).ToList();
            var mfes = trades.Select(t => t.MFE).ToList();
            var maeTicks = trades.Select(t => t.MAETicks).ToList();
            var mfeTicks = trades.Select(t => t.MFETicks).ToList();

            metrics.AvgMAE = maes.Average();
            metrics.AvgMFE = mfes.Average();
            metrics.AvgMAETicks = maeTicks.Average();
            metrics.AvgMFETicks = mfeTicks.Average();

            // MFE实现率（实际盈利/MFE）
            var realizationRates = trades.Where(t => t.MFE > 0)
                                         .Select(t => t.MFERealizationRate)
                                         .ToList();
            metrics.AvgMFERealizationRate = realizationRates.Count > 0 ? realizationRates.Average() : 0;

            // MAE违规率（超过止损距离的比例）
            metrics.MAEViolationCount = trades.Count(t => t.MAEViolation);
            metrics.MAEViolationRate = trades.Count > 0 ? ((decimal)metrics.MAEViolationCount / trades.Count) * 100 : 0;
        }

        /// <summary>
        /// 持仓时间分布分析（日内交易关键）
        /// </summary>
        private void CalculateHoldingTimeDistribution(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count == 0) return;

            // 按持仓时长分组
            var under5 = trades.Where(t => t.HoldingMinutes < 5).ToList();
            var between5and15 = trades.Where(t => t.HoldingMinutes >= 5 && t.HoldingMinutes < 15).ToList();
            var between15and30 = trades.Where(t => t.HoldingMinutes >= 15 && t.HoldingMinutes < 30).ToList();
            var between30and60 = trades.Where(t => t.HoldingMinutes >= 30 && t.HoldingMinutes < 60).ToList();
            var over60 = trades.Where(t => t.HoldingMinutes >= 60).ToList();

            // 统计数量
            metrics.TradesUnder5Min = under5.Count;
            metrics.Trades5to15Min = between5and15.Count;
            metrics.Trades15to30Min = between15and30.Count;
            metrics.Trades30to60Min = between30and60.Count;
            metrics.TradesOver60Min = over60.Count;

            // 统计胜率
            metrics.WinRateUnder5Min = under5.Count > 0 ? ((decimal)under5.Count(t => t.PnLWithFee > 0) / under5.Count) * 100 : 0;
            metrics.WinRate5to15Min = between5and15.Count > 0 ? ((decimal)between5and15.Count(t => t.PnLWithFee > 0) / between5and15.Count) * 100 : 0;
            metrics.WinRate15to30Min = between15and30.Count > 0 ? ((decimal)between15and30.Count(t => t.PnLWithFee > 0) / between15and30.Count) * 100 : 0;
            metrics.WinRate30to60Min = between30and60.Count > 0 ? ((decimal)between30and60.Count(t => t.PnLWithFee > 0) / between30and60.Count) * 100 : 0;
            metrics.WinRateOver60Min = over60.Count > 0 ? ((decimal)over60.Count(t => t.PnLWithFee > 0) / over60.Count) * 100 : 0;
        }

        /// <summary>
        /// 资金管理指标（Kelly比例和破产风险）
        /// </summary>
        private void CalculateRiskManagementMetrics(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count == 0) return;

            // Kelly比例计算
            // 公式：Kelly% = (WinRate × AvgWin - LossRate × AvgLoss) / AvgWin
            decimal winRate = metrics.WinRate / 100;  // 转换为小数
            decimal lossRate = 1 - winRate;

            if (metrics.AverageWin > 0)
            {
                metrics.KellyPercentage = ((winRate * metrics.AverageWin) - (lossRate * metrics.AverageLoss)) / metrics.AverageWin * 100;

                // Kelly可能为负（说明策略期望为负），限制范围
                if (metrics.KellyPercentage < 0) metrics.KellyPercentage = 0;
                if (metrics.KellyPercentage > 100) metrics.KellyPercentage = 100;
            }

            // 破产风险（简化计算：基于最大连续亏损）
            // 假设风险资本 = 初始资金，计算连续亏损达到破产的概率
            if (metrics.MaxLossStreak > 0 && metrics.AverageLoss > 0)
            {
                // 破产需要的连续亏损笔数
                decimal lossesToRuin = _config.InitialCapital / metrics.AverageLoss;

                // 破产概率 ≈ (LossRate)^LossesToRuin（简化模型）
                if (lossesToRuin > 0 && lossesToRuin < 100)  // 避免计算溢出
                {
                    metrics.RiskOfRuin = (decimal)Math.Pow((double)lossRate, (double)lossesToRuin) * 100;
                }
            }
        }

        /// <summary>
        /// 滑点分析
        /// </summary>
        private void CalculateSlippageAnalysis(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count == 0) return;

            // 统计滑点
            var slippages = trades.Select(t => t.Slippage).ToList();
            metrics.AvgSlippage = slippages.Average();

            // 滑点跳数
            var slippageTicks = trades.Where(t => t.TickSize > 0)
                                      .Select(t => Math.Abs(t.Slippage / t.TickSize))
                                      .ToList();
            metrics.AvgSlippageTicks = slippageTicks.Count > 0 ? slippageTicks.Average() : 0;

            // 滑点占利润的百分比
            decimal totalSlippage = Math.Abs(slippages.Sum());
            if (metrics.GrossProfitNoFee > 0)
            {
                metrics.SlippagePctOfProfit = (totalSlippage / metrics.GrossProfitNoFee) * 100;
            }
        }

        /// <summary>
        /// 时段细分分析（开盘/收盘）
        /// </summary>
        private void CalculateTimingAnalysis(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count == 0) return;

            // 按日分组，判断每笔交易是否在开盘/收盘时段
            var tradesWithTiming = trades.Select(t =>
            {
                var openTime = t.OpenTime.TimeOfDay;
                var closeTime = t.CloseTime.TimeOfDay;

                // 根据市场定义开盘/收盘时间（这里以美股为例：9:30-10:00, 15:30-16:00）
                // 可根据具体市场调整
                bool isOpening = openTime.TotalMinutes >= 570 && openTime.TotalMinutes < 600;  // 9:30-10:00
                bool isClosing = openTime.TotalMinutes >= 930 && openTime.TotalMinutes < 960;  // 15:30-16:00

                return new { Trade = t, IsOpening = isOpening, IsClosing = isClosing };
            }).ToList();

            var openingTrades = tradesWithTiming.Where(x => x.IsOpening).Select(x => x.Trade).ToList();
            var closingTrades = tradesWithTiming.Where(x => x.IsClosing).Select(x => x.Trade).ToList();

            metrics.TradesInOpeningPeriod = openingTrades.Count;
            metrics.TradesInClosingPeriod = closingTrades.Count;

            metrics.OpeningPeriodWinRate = openingTrades.Count > 0
                ? ((decimal)openingTrades.Count(t => t.PnLWithFee > 0) / openingTrades.Count) * 100
                : 0;

            metrics.ClosingPeriodWinRate = closingTrades.Count > 0
                ? ((decimal)closingTrades.Count(t => t.PnLWithFee > 0) / closingTrades.Count) * 100
                : 0;

            metrics.OpeningPeriodAvgPnL = openingTrades.Count > 0 ? openingTrades.Average(t => t.PnLWithFee) : 0;
            metrics.ClosingPeriodAvgPnL = closingTrades.Count > 0 ? closingTrades.Average(t => t.PnLWithFee) : 0;
        }

        /// <summary>
        /// 自相关分析（检测交易序列相关性）
        /// </summary>
        private void CalculateAutocorrelation(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count < 3) return;  // 至少需要3笔交易

            // 计算Lag-1自相关系数
            var returns = trades.Select(t => (double)t.PnLWithFee).ToList();
            double mean = returns.Average();

            double numerator = 0;
            double denominator = returns.Sum(r => Math.Pow(r - mean, 2));

            for (int i = 1; i < returns.Count; i++)
            {
                numerator += (returns[i] - mean) * (returns[i - 1] - mean);
            }

            if (denominator > 0.001)
            {
                metrics.Autocorrelation = (decimal)(numerator / denominator);

                // 解释自相关系数
                if (metrics.Autocorrelation > 0.2m)
                    metrics.AutocorrelationInterpretation = "趋势追踪 (Momentum)";
                else if (metrics.Autocorrelation < -0.2m)
                    metrics.AutocorrelationInterpretation = "均值回归 (Mean Reversion)";
                else
                    metrics.AutocorrelationInterpretation = "随机 (Random)";
            }
        }

        /// <summary>
        /// 计算入场时间×持仓时长热力图（日内交易核心维度）
        /// X轴：入场时间段（30分钟为一个slot）
        /// Y轴：持仓时长分组（<5min, 5-15min, 15-30min, 30-60min, >60min）
        /// Z值：平均盈亏、交易笔数、胜率
        /// </summary>
        public EntryHoldingHeatmap CalculateEntryHoldingHeatmap(List<TradeRecord> trades)
        {
            var heatmap = new EntryHoldingHeatmap();

            // 定义入场时间段（24小时UTC，每2小时一个时间段，共12个时段）
            // 适用于黄金期货等24小时交易品种
            heatmap.TimeSlots = new List<string>
            {
                "00:00-02:00", "02:00-04:00", "04:00-06:00", "06:00-08:00",
                "08:00-10:00", "10:00-12:00", "12:00-14:00", "14:00-16:00",
                "16:00-18:00", "18:00-20:00", "20:00-22:00", "22:00-24:00"
            };

            // 定义持仓时长分组（优化为更细致的6个区间，适合日内交易分析）
            heatmap.HoldingBuckets = new List<string>
            {
                "<15min", "15-30min", "30min-1h", "1h-2h", "2h-4h", ">4h"
            };

            int xSize = heatmap.TimeSlots.Count;
            int ySize = heatmap.HoldingBuckets.Count;

            // 初始化二维数组
            heatmap.AvgPnL = new decimal[ySize, xSize];
            heatmap.TradeCounts = new int[ySize, xSize];
            heatmap.WinRates = new decimal[ySize, xSize];

            // 分组统计
            var groups = new Dictionary<(int timeSlotIdx, int holdingBucketIdx), List<TradeRecord>>();

            foreach (var trade in trades)
            {
                // 确定入场时间slot（24小时UTC，每个slot 2小时 = 120分钟）
                double entryMinutes = trade.OpenTime.TimeOfDay.TotalMinutes;
                int timeSlotIdx = (int)(entryMinutes / 120);  // 0-11

                // 处理边界情况（24:00会被计算为12，应该归入23:00-24:00）
                if (timeSlotIdx >= 12) timeSlotIdx = 11;

                // 确定持仓时长bucket（6个区间）
                int holdingBucketIdx;
                if (trade.HoldingMinutes < 15)
                    holdingBucketIdx = 0;  // <15min
                else if (trade.HoldingMinutes < 30)
                    holdingBucketIdx = 1;  // 15-30min
                else if (trade.HoldingMinutes < 60)
                    holdingBucketIdx = 2;  // 30min-1h
                else if (trade.HoldingMinutes < 120)
                    holdingBucketIdx = 3;  // 1h-2h
                else if (trade.HoldingMinutes < 240)
                    holdingBucketIdx = 4;  // 2h-4h
                else
                    holdingBucketIdx = 5;  // >4h

                // 添加到分组
                var key = (timeSlotIdx, holdingBucketIdx);
                if (!groups.ContainsKey(key))
                    groups[key] = new List<TradeRecord>();
                groups[key].Add(trade);
            }

            // 计算每个格子的指标
            foreach (var kvp in groups)
            {
                int x = kvp.Key.timeSlotIdx;
                int y = kvp.Key.holdingBucketIdx;
                var groupTrades = kvp.Value;

                heatmap.TradeCounts[y, x] = groupTrades.Count;
                heatmap.AvgPnL[y, x] = groupTrades.Average(t => t.PnLWithFee);
                heatmap.WinRates[y, x] = groupTrades.Count > 0
                    ? (decimal)groupTrades.Count(t => t.PnLWithFee > 0) / groupTrades.Count * 100
                    : 0;
            }

            return heatmap;
        }

        /// <summary>
        /// 计算时段×星期热力图（周期性模式分析）
        /// X轴：星期一至星期五
        /// Y轴：交易时段（Opening, Midday, Closing, Extended）
        /// Z值：平均盈亏、交易笔数、胜率
        /// </summary>
        public SessionDayHeatmap CalculateSessionDayHeatmap(List<TradeRecord> trades)
        {
            var heatmap = new SessionDayHeatmap();

            // 定义星期（包含周末，因为黄金期货24小时交易）
            heatmap.DaysOfWeek = new List<string>
            {
                "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"
            };

            // 定义全球交易时段（UTC时间，适用于24小时交易品种）
            heatmap.Sessions = new List<string>
            {
                "Asian (00:00-08:00)",      // 亚洲时段：东京、上海、新加坡
                "European (08:00-16:00)",   // 欧洲时段：伦敦金市
                "US (16:00-24:00)"          // 美国时段：COMEX最活跃
            };

            int xSize = heatmap.DaysOfWeek.Count;
            int ySize = heatmap.Sessions.Count;

            // 初始化二维数组
            heatmap.AvgPnL = new decimal[ySize, xSize];
            heatmap.TradeCounts = new int[ySize, xSize];
            heatmap.WinRates = new decimal[ySize, xSize];

            // 分组统计
            var groups = new Dictionary<(int dayIdx, int sessionIdx), List<TradeRecord>>();

            foreach (var trade in trades)
            {
                // 确定星期（0=Sunday, 6=Saturday，需要转换为0=Monday）
                var dayOfWeek = trade.OpenTime.DayOfWeek;
                int dayIdx;

                switch (dayOfWeek)
                {
                    case DayOfWeek.Monday: dayIdx = 0; break;
                    case DayOfWeek.Tuesday: dayIdx = 1; break;
                    case DayOfWeek.Wednesday: dayIdx = 2; break;
                    case DayOfWeek.Thursday: dayIdx = 3; break;
                    case DayOfWeek.Friday: dayIdx = 4; break;
                    case DayOfWeek.Saturday: dayIdx = 5; break;
                    case DayOfWeek.Sunday: dayIdx = 6; break;
                    default: dayIdx = 0; break;
                }

                // 确定交易时段（UTC时间）
                double totalMinutes = trade.OpenTime.TimeOfDay.TotalMinutes;
                int sessionIdx;

                if (totalMinutes >= 0 && totalMinutes < 480)  // 00:00-08:00 (480分钟)
                    sessionIdx = 0;  // Asian
                else if (totalMinutes >= 480 && totalMinutes < 960)  // 08:00-16:00 (960分钟)
                    sessionIdx = 1;  // European
                else  // 16:00-24:00
                    sessionIdx = 2;  // US

                // 添加到分组
                var key = (dayIdx, sessionIdx);
                if (!groups.ContainsKey(key))
                    groups[key] = new List<TradeRecord>();
                groups[key].Add(trade);
            }

            // 计算每个格子的指标
            foreach (var kvp in groups)
            {
                int x = kvp.Key.dayIdx;
                int y = kvp.Key.sessionIdx;
                var groupTrades = kvp.Value;

                heatmap.TradeCounts[y, x] = groupTrades.Count;
                heatmap.AvgPnL[y, x] = groupTrades.Average(t => t.PnLWithFee);
                heatmap.WinRates[y, x] = groupTrades.Count > 0
                    ? (decimal)groupTrades.Count(t => t.PnLWithFee > 0) / groupTrades.Count * 100
                    : 0;
            }

            return heatmap;
        }

        /// <summary>
        /// 验证交易记录数据质量（日内交易关键）
        /// </summary>
        private void ValidateTradeRecords(List<TradeRecord> trades)
        {
            var warnings = new List<string>();

            for (int i = 0; i < trades.Count; i++)
            {
                var trade = trades[i];

                // 1. 时间逻辑错误
                if (trade.CloseTime < trade.OpenTime)
                {
                    warnings.Add($"Trade #{i}: CloseTime before OpenTime ({trade.CloseTime} < {trade.OpenTime})");
                }

                // 2. 持仓时间异常
                var holdingTime = trade.HoldingTime;
                if (holdingTime.TotalSeconds < 1)
                {
                    warnings.Add($"Trade #{i}: Holding time < 1 second ({holdingTime.TotalSeconds:F2}s)");
                }
                else if (holdingTime.TotalHours > 24)
                {
                    warnings.Add($"Trade #{i}: Holding time > 24 hours (not typical for intraday, {holdingTime.TotalHours:F1}h)");
                }

                // 3. 价格异常（对于多头/空头逻辑检查）
                if (trade.Direction == "Long")
                {
                    decimal priceChange = trade.ClosePrice - trade.OpenPrice;
                    decimal priceChangePct = trade.OpenPrice > 0 ? Math.Abs(priceChange / trade.OpenPrice) * 100 : 0;

                    if (priceChangePct > 10)  // 单笔交易价格变化>10%
                    {
                        warnings.Add($"Trade #{i} (Long): Price change {priceChangePct:F2}% seems excessive");
                    }
                }
                else if (trade.Direction == "Short")
                {
                    decimal priceChange = trade.OpenPrice - trade.ClosePrice;
                    decimal priceChangePct = trade.OpenPrice > 0 ? Math.Abs(priceChange / trade.OpenPrice) * 100 : 0;

                    if (priceChangePct > 10)
                    {
                        warnings.Add($"Trade #{i} (Short): Price change {priceChangePct:F2}% seems excessive");
                    }
                }

                // 4. TP/SL逻辑检查（多头）
                if (trade.Direction == "Long")
                {
                    if (trade.TakeProfitPrice > 0 && trade.TakeProfitPrice <= trade.OpenPrice)
                    {
                        warnings.Add($"Trade #{i} (Long): TP ({trade.TakeProfitPrice}) <= OpenPrice ({trade.OpenPrice})");
                    }
                    if (trade.StopLossPrice > 0 && trade.StopLossPrice >= trade.OpenPrice)
                    {
                        warnings.Add($"Trade #{i} (Long): SL ({trade.StopLossPrice}) >= OpenPrice ({trade.OpenPrice})");
                    }
                }

                // 5. TP/SL逻辑检查（空头）
                if (trade.Direction == "Short")
                {
                    if (trade.TakeProfitPrice > 0 && trade.TakeProfitPrice >= trade.OpenPrice)
                    {
                        warnings.Add($"Trade #{i} (Short): TP ({trade.TakeProfitPrice}) >= OpenPrice ({trade.OpenPrice})");
                    }
                    if (trade.StopLossPrice > 0 && trade.StopLossPrice <= trade.OpenPrice)
                    {
                        warnings.Add($"Trade #{i} (Short): SL ({trade.StopLossPrice}) <= OpenPrice ({trade.OpenPrice})");
                    }
                }
            }

            // 6. 检测重复记录（相同开仓和平仓时间）
            var duplicates = trades
                .GroupBy(t => new { t.OpenTime, t.CloseTime })
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicates.Any())
            {
                warnings.Add($"Found {duplicates.Count} potential duplicate trade groups");
            }

            // 如果有警告，输出到调试（生产环境可改为日志）
            if (warnings.Any())
            {
                System.Diagnostics.Debug.WriteLine("=== Trade Data Validation Warnings ===");
                foreach (var warning in warnings)
                {
                    System.Diagnostics.Debug.WriteLine($"  ⚠️  {warning}");
                }
                System.Diagnostics.Debug.WriteLine($"Total: {warnings.Count} warnings");
            }
        }

        private void CalculateEquityCurve(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            metrics.EquityCurveNoFee = new List<decimal>();
            metrics.EquityCurveWithFee = new List<decimal>();

            decimal equityNoFee = _config.InitialCapital;
            decimal equityWithFee = _config.InitialCapital;

            metrics.EquityCurveNoFee.Add(equityNoFee);
            metrics.EquityCurveWithFee.Add(equityWithFee);

            foreach (var trade in trades)
            {
                equityNoFee += trade.PnLNoFee;
                equityWithFee += trade.PnLWithFee;

                metrics.EquityCurveNoFee.Add(equityNoFee);
                metrics.EquityCurveWithFee.Add(equityWithFee);
            }
        }

        private double CalculateStdDev(double[] values)
        {
            if (values.Length < 2) return 0;

            double mean = values.Average();
            double sumSquaredDiff = values.Sum(v => (v - mean) * (v - mean));
            return Math.Sqrt(sumSquaredDiff / (values.Length - 1));
        }

        /// <summary>
        /// 计算偏度 (Skewness)
        /// </summary>
        private double CalculateSkewness(double[] values)
        {
            if (values.Length < 3) return 0;

            double mean = values.Average();
            double stdDev = CalculateStdDev(values);

            if (stdDev == 0) return 0;

            double sumCubedDiff = values.Sum(v => Math.Pow((v - mean) / stdDev, 3));
            int n = values.Length;

            return (n / ((n - 1.0) * (n - 2.0))) * sumCubedDiff;
        }

        /// <summary>
        /// 计算峰度 (Kurtosis) - 超额峰度
        /// </summary>
        private double CalculateKurtosis(double[] values)
        {
            if (values.Length < 4) return 0;

            double mean = values.Average();
            double stdDev = CalculateStdDev(values);

            if (stdDev == 0) return 0;

            double sumFourthPower = values.Sum(v => Math.Pow((v - mean) / stdDev, 4));
            int n = values.Length;

            // 超额峰度 (excess kurtosis)
            double kurtosis = (n * (n + 1.0) / ((n - 1.0) * (n - 2.0) * (n - 3.0))) * sumFourthPower;
            double correction = (3.0 * Math.Pow(n - 1.0, 2)) / ((n - 2.0) * (n - 3.0));

            return kurtosis - correction;
        }

        /// <summary>
        /// 计算百分位数 (Percentile)
        /// </summary>
        private decimal Percentile(List<decimal> values, int percentile)
        {
            if (values == null || values.Count == 0)
                return 0;

            var sorted = values.OrderBy(v => v).ToList();

            if (percentile <= 0) return sorted.First();
            if (percentile >= 100) return sorted.Last();

            double index = (percentile / 100.0) * (sorted.Count - 1);
            int lowerIndex = (int)Math.Floor(index);
            int upperIndex = (int)Math.Ceiling(index);

            if (lowerIndex == upperIndex)
                return sorted[lowerIndex];

            double fraction = index - lowerIndex;
            return sorted[lowerIndex] + (decimal)fraction * (sorted[upperIndex] - sorted[lowerIndex]);
        }

        /// <summary>
        /// 独立样本T检验 (Welch's t-test)
        /// 返回 p-value
        /// </summary>
        private double WelchTTest(double[] sample1, double[] sample2)
        {
            if (sample1.Length < 2 || sample2.Length < 2)
                return 1.0;  // 样本太小，无法检验

            double mean1 = sample1.Average();
            double mean2 = sample2.Average();
            double var1 = sample1.Sum(v => Math.Pow(v - mean1, 2)) / (sample1.Length - 1);
            double var2 = sample2.Sum(v => Math.Pow(v - mean2, 2)) / (sample2.Length - 1);

            // Welch's t统计量
            double tStatistic = (mean1 - mean2) / Math.Sqrt(var1 / sample1.Length + var2 / sample2.Length);

            // 自由度 (Welch-Satterthwaite equation)
            double df = Math.Pow(var1 / sample1.Length + var2 / sample2.Length, 2) /
                       (Math.Pow(var1 / sample1.Length, 2) / (sample1.Length - 1) +
                        Math.Pow(var2 / sample2.Length, 2) / (sample2.Length - 1));

            // 计算p-value (双尾检验)
            // 简化版：使用正态近似 (df较大时准确)
            double pValue = 2 * (1 - NormalCDF(Math.Abs(tStatistic)));

            return pValue;
        }

        /// <summary>
        /// 标准正态分布的累积分布函数 (CDF)
        /// </summary>
        private double NormalCDF(double z)
        {
            // 使用误差函数近似
            return 0.5 * (1 + Erf(z / Math.Sqrt(2)));
        }

        /// <summary>
        /// 误差函数 (Error function)
        /// 使用Abramowitz and Stegun近似
        /// </summary>
        private double Erf(double x)
        {
            double sign = x >= 0 ? 1 : -1;
            x = Math.Abs(x);

            double a1 = 0.254829592;
            double a2 = -0.284496736;
            double a3 = 1.421413741;
            double a4 = -1.453152027;
            double a5 = 1.061405429;
            double p = 0.3275911;

            double t = 1.0 / (1.0 + p * x);
            double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

            return sign * y;
        }

        #endregion

        #region 分维度分析

        /// <summary>
        /// 按方向分析（Long vs Short）
        /// </summary>
        public Dictionary<string, AnalysisMetrics> AnalyzeByDirection(List<TradeRecord> trades)
        {
            var results = new Dictionary<string, AnalysisMetrics>();

            var longTrades = trades.Where(t => t.Direction == "Long").ToList();
            var shortTrades = trades.Where(t => t.Direction == "Short").ToList();

            if (longTrades.Count > 0)
                results["Long"] = Analyze(longTrades);

            if (shortTrades.Count > 0)
                results["Short"] = Analyze(shortTrades);

            return results;
        }

        /// <summary>
        /// 按时段分析（亚盘/欧盘/美盘）
        /// </summary>
        public Dictionary<string, AnalysisMetrics> AnalyzeBySession(List<TradeRecord> trades)
        {
            var results = new Dictionary<string, AnalysisMetrics>();

            var asianTrades = trades.Where(t => IsAsianSession(t.OpenTime)).ToList();
            var europeanTrades = trades.Where(t => IsEuropeanSession(t.OpenTime)).ToList();
            var usTrades = trades.Where(t => IsUSSession(t.OpenTime)).ToList();

            if (asianTrades.Count > 0)
                results["Asian"] = Analyze(asianTrades);

            if (europeanTrades.Count > 0)
                results["European"] = Analyze(europeanTrades);

            if (usTrades.Count > 0)
                results["US"] = Analyze(usTrades);

            return results;
        }

        private bool IsAsianSession(DateTime time)
        {
            // UTC 00:00 - 08:00
            var utcTime = time.ToUniversalTime();
            int hour = utcTime.Hour;
            return hour >= 0 && hour < 8;
        }

        private bool IsEuropeanSession(DateTime time)
        {
            // UTC 08:00 - 16:00
            var utcTime = time.ToUniversalTime();
            int hour = utcTime.Hour;
            return hour >= 8 && hour < 16;
        }

        private bool IsUSSession(DateTime time)
        {
            // UTC 16:00 - 24:00
            var utcTime = time.ToUniversalTime();
            int hour = utcTime.Hour;
            return hour >= 16;
        }

        #endregion

        #region 报告生成

        /// <summary>
        /// 生成控制台文本报告
        /// </summary>
        public string GenerateConsoleReport(
            AnalysisMetrics allMetrics,
            Dictionary<string, AnalysisMetrics> directionMetrics,
            Dictionary<string, AnalysisMetrics> sessionMetrics)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;

            sb.AppendLine("================================================================================");
            sb.AppendLine($"              Paper Trading Analysis Report - {_config.Symbol}");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Tick Value: ${_config.TickValue:F2} | Commission: ${_config.CommissionPerSide * 2:F2}/RT | Initial Capital: ${_config.InitialCapital:N2}");
            sb.AppendLine("================================================================================");
            sb.AppendLine();
            sb.AppendLine("                        PERFORMANCE SUMMARY");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"{"",40}0 Fee          With Fee        Impact");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"{"Total Trades",-40}{allMetrics.TotalTrades,-15}{allMetrics.TotalTrades,-15}-");
            sb.AppendLine($"{"Winning Trades",-40}{allMetrics.WinningTrades,-15}{allMetrics.WinningTrades,-15}-");
            sb.AppendLine($"{"Win Rate",-40}{allMetrics.WinRate:F2}%{"",-10}{allMetrics.WinRate:F2}%{"",-10}-");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"{"Net Profit",-40}${allMetrics.NetProfitNoFee:N2}{"",-6}${allMetrics.NetProfitWithFee:N2}{"",-6}{CalculateImpact(allMetrics.NetProfitNoFee, allMetrics.NetProfitWithFee)}");
            sb.AppendLine($"{"Gross Profit",-40}${allMetrics.GrossProfitNoFee:N2}{"",-6}${allMetrics.GrossProfitWithFee:N2}{"",-6}{CalculateImpact(allMetrics.GrossProfitNoFee, allMetrics.GrossProfitWithFee)}");
            sb.AppendLine($"{"Gross Loss",-40}${allMetrics.GrossLossNoFee:N2}{"",-6}${allMetrics.GrossLossWithFee:N2}{"",-6}{CalculateImpact(allMetrics.GrossLossNoFee, allMetrics.GrossLossWithFee)}");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"{"Profit Factor",-40}{allMetrics.ProfitFactor:F2}{"",-13}{allMetrics.ProfitFactor:F2}{"",-13}-");
            sb.AppendLine($"{"Average Win",-40}${allMetrics.AverageWin:N2}{"",-6}${allMetrics.AverageWin:N2}{"",-6}-");
            sb.AppendLine($"{"Average Loss",-40}${Math.Abs(allMetrics.AverageLoss):N2}{"",-6}${Math.Abs(allMetrics.AverageLoss):N2}{"",-6}-");
            sb.AppendLine($"{"Risk/Reward Ratio",-40}{allMetrics.RiskRewardRatio:F2}{"",-13}{allMetrics.RiskRewardRatio:F2}{"",-13}-");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"{"Max Drawdown",-40}${allMetrics.MaxDrawdown:N2}{"",-6}${allMetrics.MaxDrawdown:N2}{"",-6}-");
            sb.AppendLine($"{"Max Drawdown %",-40}{allMetrics.MaxDrawdownPct:F2}%{"",-10}{allMetrics.MaxDrawdownPct:F2}%{"",-10}-");
            sb.AppendLine($"{"Largest Win",-40}${allMetrics.LargestWin:N2}{"",-6}${allMetrics.LargestWin:N2}{"",-6}-");
            sb.AppendLine($"{"Largest Loss",-40}${allMetrics.LargestLoss:N2}{"",-6}${allMetrics.LargestLoss:N2}{"",-6}-");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"{"Quality Ratio (Per-Trade)",-40}{allMetrics.QualityRatio:F2}{"",-13}{allMetrics.QualityRatio:F2}{"",-13}-");
            sb.AppendLine($"{"Sharpe Ratio (Daily)",-40}{allMetrics.SharpeRatioDaily:F2}{"",-13}{allMetrics.SharpeRatioDaily:F2}{"",-13}-");
            sb.AppendLine($"{"Annualized Sharpe",-40}{allMetrics.AnnualizedSharpe:F2}{"",-13}{allMetrics.AnnualizedSharpe:F2}{"",-13}-");
            sb.AppendLine($"{"Calmar Ratio",-40}{allMetrics.CalmarRatio:F2}{"",-13}{allMetrics.CalmarRatio:F2}{"",-13}-");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"{"Avg Holding Time (bars)",-40}{allMetrics.AvgHoldingBars:F0}{"",-13}{allMetrics.AvgHoldingBars:F0}{"",-13}-");
            sb.AppendLine($"{"Trading Days",-40}{allMetrics.TradingDays,-15}{allMetrics.TradingDays,-15}-");
            sb.AppendLine($"{"Avg Trades per Day",-40}{allMetrics.AvgTradesPerDay:F2}{"",-13}{allMetrics.AvgTradesPerDay:F2}{"",-13}-");
            sb.AppendLine("================================================================================");

            // 方向分析
            if (directionMetrics != null && directionMetrics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("                        DIRECTION BREAKDOWN");
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine($"{"",25}Long Trades{"",-15}Short Trades");
                sb.AppendLine("--------------------------------------------------------------------------------");

                var longM = directionMetrics.ContainsKey("Long") ? directionMetrics["Long"] : null;
                var shortM = directionMetrics.ContainsKey("Short") ? directionMetrics["Short"] : null;

                sb.AppendLine($"{"Win Rate",-25}{(longM?.WinRate ?? 0):F2}%{"",-15}{(shortM?.WinRate ?? 0):F2}%");
                sb.AppendLine($"{"Net Profit",-25}${(longM?.NetProfitWithFee ?? 0):N2}{"",-6}${(shortM?.NetProfitWithFee ?? 0):N2}");
                sb.AppendLine($"{"Profit Factor",-25}{(longM?.ProfitFactor ?? 0):F2}{"",-17}{(shortM?.ProfitFactor ?? 0):F2}");
                sb.AppendLine($"{"Quality Ratio",-25}{(longM?.QualityRatio ?? 0):F2}{"",-17}{(shortM?.QualityRatio ?? 0):F2}");
                sb.AppendLine($"{"Max Drawdown %",-25}{(longM?.MaxDrawdownPct ?? 0):F2}%{"",-15}{(shortM?.MaxDrawdownPct ?? 0):F2}%");
                sb.AppendLine("================================================================================");
            }

            // 时段分析
            if (sessionMetrics != null && sessionMetrics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("                        SESSION BREAKDOWN");
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine($"{"",25}Asian{"",-11}European{"",-10}US");
                sb.AppendLine("--------------------------------------------------------------------------------");

                var asianM = sessionMetrics.ContainsKey("Asian") ? sessionMetrics["Asian"] : null;
                var euroM = sessionMetrics.ContainsKey("European") ? sessionMetrics["European"] : null;
                var usM = sessionMetrics.ContainsKey("US") ? sessionMetrics["US"] : null;

                sb.AppendLine($"{"Total Trades",-25}{(asianM?.TotalTrades ?? 0),-15}{(euroM?.TotalTrades ?? 0),-15}{(usM?.TotalTrades ?? 0)}");
                sb.AppendLine($"{"Win Rate",-25}{(asianM?.WinRate ?? 0):F2}%{"",-10}{(euroM?.WinRate ?? 0):F2}%{"",-10}{(usM?.WinRate ?? 0):F2}%");
                sb.AppendLine($"{"Net Profit",-25}${(asianM?.NetProfitWithFee ?? 0):N2}{"",-3}${(euroM?.NetProfitWithFee ?? 0):N2}{"",-3}${(usM?.NetProfitWithFee ?? 0):N2}");
                sb.AppendLine($"{"Profit Factor",-25}{(asianM?.ProfitFactor ?? 0):F2}{"",-13}{(euroM?.ProfitFactor ?? 0):F2}{"",-13}{(usM?.ProfitFactor ?? 0):F2}");
                sb.AppendLine($"{"Quality Ratio",-25}{(asianM?.QualityRatio ?? 0):F2}{"",-13}{(euroM?.QualityRatio ?? 0):F2}{"",-13}{(usM?.QualityRatio ?? 0):F2}");
                sb.AppendLine($"{"Max Drawdown %",-25}{(asianM?.MaxDrawdownPct ?? 0):F2}%{"",-10}{(euroM?.MaxDrawdownPct ?? 0):F2}%{"",-10}{(usM?.MaxDrawdownPct ?? 0):F2}%");
                sb.AppendLine("================================================================================");
            }

            sb.AppendLine();
            sb.AppendLine($"Report Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("================================================================================");

            return sb.ToString();
        }

        private string CalculateImpact(decimal noFee, decimal withFee)
        {
            if (Math.Abs(noFee) < 0.001m) return "N/A";
            decimal impact = (withFee - noFee) / noFee * 100;
            return $"{impact:+0.0;-0.0;0.0}%";
        }

        /// <summary>
        /// 导出分析CSV
        /// </summary>
        public string ExportAnalysisCSV(AnalysisMetrics metrics, string filePath)
        {
            var sb = new StringBuilder();

            // 基础指标
            sb.AppendLine("Metric,0 Fee,With Fee");
            sb.AppendLine($"Total Trades,{metrics.TotalTrades},{metrics.TotalTrades}");
            sb.AppendLine($"Winning Trades,{metrics.WinningTrades},{metrics.WinningTrades}");
            sb.AppendLine($"Win Rate,{metrics.WinRate:F2}%,{metrics.WinRate:F2}%");
            sb.AppendLine($"Net Profit,${metrics.NetProfitNoFee:F2},${metrics.NetProfitWithFee:F2}");
            sb.AppendLine($"Gross Profit,${metrics.GrossProfitNoFee:F2},${metrics.GrossProfitWithFee:F2}");
            sb.AppendLine($"Gross Loss,${metrics.GrossLossNoFee:F2},${metrics.GrossLossWithFee:F2}");
            sb.AppendLine($"Profit Factor,{metrics.ProfitFactor:F2},{metrics.ProfitFactor:F2}");
            sb.AppendLine($"Average Win,${metrics.AverageWin:F2},${metrics.AverageWin:F2}");
            sb.AppendLine($"Average Loss,${Math.Abs(metrics.AverageLoss):F2},${Math.Abs(metrics.AverageLoss):F2}");
            sb.AppendLine($"Risk/Reward Ratio,{metrics.RiskRewardRatio:F2},{metrics.RiskRewardRatio:F2}");
            sb.AppendLine($"Max Drawdown,${metrics.MaxDrawdown:F2},${metrics.MaxDrawdown:F2}");
            sb.AppendLine($"Max Drawdown %,{metrics.MaxDrawdownPct:F2}%,{metrics.MaxDrawdownPct:F2}%");
            sb.AppendLine($"Largest Win,${metrics.LargestWin:F2},${metrics.LargestWin:F2}");
            sb.AppendLine($"Largest Loss,${metrics.LargestLoss:F2},${metrics.LargestLoss:F2}");
            sb.AppendLine($"Quality Ratio (Per-Trade),{metrics.QualityRatio:F2},{metrics.QualityRatio:F2}");
            sb.AppendLine($"Sharpe Ratio (Daily),{metrics.SharpeRatioDaily:F2},{metrics.SharpeRatioDaily:F2}");
            sb.AppendLine($"Annualized Sharpe,{metrics.AnnualizedSharpe:F2},{metrics.AnnualizedSharpe:F2}");
            sb.AppendLine($"Calmar Ratio,{metrics.CalmarRatio:F2},{metrics.CalmarRatio:F2}");
            sb.AppendLine($"Avg Holding Time (bars),{metrics.AvgHoldingBars:F0},{metrics.AvgHoldingBars:F0}");
            sb.AppendLine($"Trading Days,{metrics.TradingDays},{metrics.TradingDays}");
            sb.AppendLine($"Avg Trades per Day,{metrics.AvgTradesPerDay:F2},{metrics.AvgTradesPerDay:F2}");

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            return filePath;
        }

        #endregion
    }
}
