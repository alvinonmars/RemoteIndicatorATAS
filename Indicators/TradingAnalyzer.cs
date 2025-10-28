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
            public int HoldingBars { get; set; }
            public string ExitReason { get; set; }

            // 计算字段
            public decimal PnLTicks => TickSize > 0 ? ActualPnL / TickSize : 0;
            public decimal PnLDollars { get; set; }
            public decimal PnLWithFee { get; set; }
            public decimal PnLNoFee { get; set; }
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

            // 高级指标
            public decimal SharpeRatioPerTrade { get; set; }
            public decimal SharpeRatioDaily { get; set; }
            public decimal AnnualizedSharpe { get; set; }
            public decimal AnnualizedReturn { get; set; }
            public decimal CalmarRatio { get; set; }

            // 权益曲线
            public List<decimal> EquityCurveNoFee { get; set; }
            public List<decimal> EquityCurveWithFee { get; set; }
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

            // 高级指标
            CalculateAdvancedMetrics(trades, metrics);

            // 权益曲线
            CalculateEquityCurve(trades, metrics);

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
            decimal maxDrawdown = 0;
            decimal maxDrawdownPct = 0;

            foreach (var trade in trades)
            {
                equity += trade.PnLWithFee;
                runningMax = Math.Max(runningMax, equity);

                decimal currentDrawdown = runningMax - equity;
                decimal currentDrawdownPct = runningMax > 0 ? currentDrawdown / runningMax : 0;

                maxDrawdown = Math.Max(maxDrawdown, currentDrawdown);
                maxDrawdownPct = Math.Max(maxDrawdownPct, currentDrawdownPct);
            }

            metrics.MaxDrawdown = maxDrawdown;
            metrics.MaxDrawdownPct = maxDrawdownPct * 100;  // 转换为百分比

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

                // 计算交易天数
                var uniqueDays = trades.Select(t => t.OpenTime.Date).Distinct().Count();
                metrics.TradingDays = uniqueDays;

                if (uniqueDays > 0)
                    metrics.AvgTradesPerDay = (decimal)trades.Count / uniqueDays;
            }
        }

        private void CalculateAdvancedMetrics(List<TradeRecord> trades, AnalysisMetrics metrics)
        {
            if (trades.Count == 0) return;

            // Sharpe比率（按笔）
            var pnls = trades.Select(t => (double)t.PnLWithFee).ToArray();
            double meanPnL = pnls.Average();
            double stdDevPnL = CalculateStdDev(pnls);

            if (stdDevPnL > 0.001)
                metrics.SharpeRatioPerTrade = (decimal)(meanPnL / stdDevPnL);

            // Sharpe比率（按天）
            var dailyPnLs = trades
                .GroupBy(t => t.OpenTime.Date)
                .Select(g => (double)g.Sum(t => t.PnLWithFee))
                .ToArray();

            if (dailyPnLs.Length > 1)
            {
                double meanDaily = dailyPnLs.Average();
                double stdDevDaily = CalculateStdDev(dailyPnLs);

                if (stdDevDaily > 0.001)
                {
                    metrics.SharpeRatioDaily = (decimal)(meanDaily / stdDevDaily);
                    metrics.AnnualizedSharpe = metrics.SharpeRatioDaily * (decimal)Math.Sqrt(252);
                }
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
            sb.AppendLine($"{"Sharpe Ratio (Per-Trade)",-40}{allMetrics.SharpeRatioPerTrade:F2}{"",-13}{allMetrics.SharpeRatioPerTrade:F2}{"",-13}-");
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
                sb.AppendLine($"{"Sharpe (Per-Trade)",-25}{(longM?.SharpeRatioPerTrade ?? 0):F2}{"",-17}{(shortM?.SharpeRatioPerTrade ?? 0):F2}");
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
                sb.AppendLine($"{"Sharpe (Per-Trade)",-25}{(asianM?.SharpeRatioPerTrade ?? 0):F2}{"",-13}{(euroM?.SharpeRatioPerTrade ?? 0):F2}{"",-13}{(usM?.SharpeRatioPerTrade ?? 0):F2}");
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
            sb.AppendLine($"Sharpe Ratio (Per-Trade),{metrics.SharpeRatioPerTrade:F2},{metrics.SharpeRatioPerTrade:F2}");
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
