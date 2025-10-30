using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;

namespace RemoteIndicatorATAS_standalone.Indicators
{
    /// <summary>
    /// 交易报告生成器
    /// 职责：将交易数据和分析结果可视化为HTML报告
    /// </summary>
    public class TradingReportGenerator
    {
        private readonly string _templatePath;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="templatePath">HTML模板文件路径，为null时使用默认路径</param>
        public TradingReportGenerator(string templatePath = null)
        {
            _templatePath = templatePath ?? GetDefaultTemplatePath();
        }

        /// <summary>
        /// 生成HTML报告
        /// </summary>
        public void GenerateHtmlReport(
            string outputFilePath,
            TradingAnalyzer.AnalysisConfig config,
            TradingAnalyzer.AnalysisMetrics allMetrics,
            Dictionary<string, TradingAnalyzer.AnalysisMetrics> directionMetrics,
            Dictionary<string, TradingAnalyzer.AnalysisMetrics> sessionMetrics,
            List<TradingAnalyzer.TradeRecord> tradeRecords,
            TradingAnalyzer.EntryHoldingHeatmap entryHoldingHeatmap,
            TradingAnalyzer.SessionDayHeatmap sessionDayHeatmap,
            TradingAnalyzer.DateHourHeatmap dateHourHeatmap,
            TradingAnalyzer.WeekdayHourHeatmap weekdayHourHeatmap,
            TradingAnalyzer.MonthHourHeatmap monthHourHeatmap,
            TradingAnalyzer.QualityHeatmap qualityHeatmap)
        {
            var sb = new StringBuilder();

            // HTML头部（使用HTML转义防止XSS）
            string safeSymbol = HtmlEncode(config.Symbol);

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang='en'>");
            sb.AppendLine("<head>");
            sb.AppendLine("    <meta charset='UTF-8'>");
            sb.AppendLine("    <meta name='viewport' content='width=device-width, initial-scale=1.0'>");
            sb.AppendLine($"    <title>Paper Trading Analysis - {safeSymbol}</title>");
            sb.AppendLine("    <script src='https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.js'></script>");
            sb.AppendLine("    <script src='https://cdn.plot.ly/plotly-2.27.0.min.js'></script>");

            // CSS样式
            sb.Append(GenerateCssStyles());

            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("    <div class='container'>");

            // 报告头部
            sb.Append(GenerateHeaderHtml(config));

            // 摘要卡片
            sb.Append(GenerateSummaryCardsHtml(allMetrics));

            // 权益曲线图容器
            sb.Append(GenerateEquityChartContainerHtml());

            // 新增4个热图（放在收益曲线之后）
            sb.Append(GenerateNewHeatmapsHtml(dateHourHeatmap, weekdayHourHeatmap, monthHourHeatmap, qualityHeatmap));

            // 详细指标表格
            sb.Append(GenerateMetricsTablesHtml(allMetrics, directionMetrics, sessionMetrics));

            // 原有热力图（24小时交易分析）
            sb.Append(GenerateHeatmapsHtml(entryHoldingHeatmap, sessionDayHeatmap));

            sb.AppendLine("    </div>");

            // JavaScript脚本（权益曲线图）
            sb.Append(GenerateEquityChartScript(tradeRecords, config.InitialCapital));

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText(outputFilePath, sb.ToString(), Encoding.UTF8);
        }

        #region 私有辅助方法

        /// <summary>
        /// 获取默认模板路径
        /// </summary>
        private string GetDefaultTemplatePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HtmlReportTemplate.txt");
        }

        /// <summary>
        /// HTML编码（防止XSS攻击）
        /// </summary>
        private string HtmlEncode(string text)
        {
            return HttpUtility.HtmlEncode(text);
        }

        /// <summary>
        /// 生成CSS样式
        /// </summary>
        private string GenerateCssStyles()
        {
            var sb = new StringBuilder();
            sb.AppendLine("    <style>");
            sb.AppendLine("        * { margin: 0; padding: 0; box-sizing: border-box; }");
            sb.AppendLine("        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background: #f5f5f5; padding: 20px; }");
            sb.AppendLine("        .container { max-width: 1400px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }");
            sb.AppendLine("        h1 { color: #333; margin-bottom: 10px; }");
            sb.AppendLine("        .subtitle { color: #666; margin-bottom: 30px; }");
            sb.AppendLine("        .summary-cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 20px; margin-bottom: 30px; }");
            sb.AppendLine("        .card { background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); padding: 20px; border-radius: 8px; color: white; }");
            sb.AppendLine("        .card.profit { background: linear-gradient(135deg, #11998e 0%, #38ef7d 100%); }");
            sb.AppendLine("        .card.loss { background: linear-gradient(135deg, #eb3349 0%, #f45c43 100%); }");
            sb.AppendLine("        .card.neutral { background: linear-gradient(135deg, #4b6cb7 0%, #182848 100%); }");
            sb.AppendLine("        .card h3 { font-size: 14px; opacity: 0.9; margin-bottom: 10px; }");
            sb.AppendLine("        .card .value { font-size: 28px; font-weight: bold; }");
            sb.AppendLine("        .chart-container { margin: 30px 0; padding: 20px; background: #fafafa; border-radius: 8px; }");
            sb.AppendLine("        .chart-container h2 { color: #333; margin-bottom: 20px; }");
            sb.AppendLine("        canvas { max-height: 400px; }");
            sb.AppendLine("        table { width: 100%; border-collapse: collapse; margin: 20px 0; }");
            sb.AppendLine("        th, td { padding: 12px; text-align: left; border-bottom: 1px solid #ddd; }");
            sb.AppendLine("        th { background: #667eea; color: white; }");
            sb.AppendLine("        tr:hover { background: #f5f5f5; }");
            sb.AppendLine("        .positive { color: #11998e; font-weight: bold; }");
            sb.AppendLine("        .negative { color: #eb3349; font-weight: bold; }");
            sb.AppendLine("        .section { margin: 40px 0; }");
            sb.AppendLine("    </style>");
            return sb.ToString();
        }

        /// <summary>
        /// 生成报告头部（标题和副标题）
        /// </summary>
        private string GenerateHeaderHtml(TradingAnalyzer.AnalysisConfig config)
        {
            string safeSymbol = HtmlEncode(config.Symbol);
            var sb = new StringBuilder();
            sb.AppendLine($"        <h1>Paper Trading Analysis Report - {safeSymbol}</h1>");
            sb.AppendLine($"        <div class='subtitle'>Generated on {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Tick Value: ${config.TickValue:F2} | Commission: ${config.CommissionPerSide * 2:F2}/RT | Initial Capital: ${config.InitialCapital:N2}</div>");
            return sb.ToString();
        }

        /// <summary>
        /// 生成摘要卡片HTML
        /// </summary>
        private string GenerateSummaryCardsHtml(TradingAnalyzer.AnalysisMetrics metrics)
        {
            var sb = new StringBuilder();
            sb.AppendLine("        <div class='summary-cards'>");

            string profitClass = metrics.NetProfitWithFee > 0 ? "profit" : (metrics.NetProfitWithFee < 0 ? "loss" : "neutral");
            sb.AppendLine($"            <div class='card {profitClass}'>");
            sb.AppendLine("                <h3>Net Profit</h3>");
            sb.AppendLine($"                <div class='value'>${metrics.NetProfitWithFee:N2}</div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("            <div class='card'>");
            sb.AppendLine("                <h3>Win Rate</h3>");
            sb.AppendLine($"                <div class='value'>{metrics.WinRate:F2}%</div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("            <div class='card'>");
            sb.AppendLine("                <h3>Quality Ratio</h3>");
            sb.AppendLine($"                <div class='value'>{metrics.QualityRatio:F2}</div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("            <div class='card'>");
            sb.AppendLine("                <h3>Max Drawdown</h3>");
            sb.AppendLine($"                <div class='value'>{metrics.MaxDrawdownPct:F2}%</div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("            <div class='card'>");
            sb.AppendLine("                <h3>Total Trades</h3>");
            sb.AppendLine($"                <div class='value'>{metrics.TotalTrades}</div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("            <div class='card'>");
            sb.AppendLine("                <h3>Profit Factor</h3>");
            sb.AppendLine($"                <div class='value'>{metrics.ProfitFactor:F2}</div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("        </div>");
            return sb.ToString();
        }

        /// <summary>
        /// 生成权益曲线图容器HTML
        /// </summary>
        private string GenerateEquityChartContainerHtml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("        <div class='chart-container'>");
            sb.AppendLine("            <h2>Equity Curve</h2>");
            sb.AppendLine("            <canvas id='equityChart'></canvas>");
            sb.AppendLine("        </div>");
            return sb.ToString();
        }

        /// <summary>
        /// 生成详细指标表格HTML
        /// </summary>
        private string GenerateMetricsTablesHtml(
            TradingAnalyzer.AnalysisMetrics allMetrics,
            Dictionary<string, TradingAnalyzer.AnalysisMetrics> directionMetrics,
            Dictionary<string, TradingAnalyzer.AnalysisMetrics> sessionMetrics)
        {
            var sb = new StringBuilder();

            // 主要指标表格
            sb.AppendLine("        <div class='section'>");
            sb.AppendLine("            <h2>Performance Metrics</h2>");
            sb.AppendLine("            <table>");
            sb.AppendLine("                <tr><th>Metric</th><th>0 Fee</th><th>With Fee</th><th>Impact</th></tr>");

            // 基础统计
            sb.AppendLine($"                <tr><td><strong>Total Trades</strong></td><td>{allMetrics.TotalTrades}</td><td>{allMetrics.TotalTrades}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Winning Trades</strong></td><td>{allMetrics.WinningTrades}</td><td>{allMetrics.WinningTrades}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Win Rate</strong></td><td>{allMetrics.WinRate:F2}%</td><td>{allMetrics.WinRate:F2}%</td><td>-</td></tr>");
            sb.AppendLine("                <tr><td colspan='4'></td></tr>");

            // 盈利指标
            decimal netProfitImpact = allMetrics.NetProfitNoFee != 0 ? (allMetrics.NetProfitWithFee - allMetrics.NetProfitNoFee) / allMetrics.NetProfitNoFee * 100 : 0;
            decimal grossProfitImpact = allMetrics.GrossProfitNoFee != 0 ? (allMetrics.GrossProfitWithFee - allMetrics.GrossProfitNoFee) / allMetrics.GrossProfitNoFee * 100 : 0;
            decimal grossLossImpact = allMetrics.GrossLossNoFee != 0 ? (allMetrics.GrossLossWithFee - allMetrics.GrossLossNoFee) / allMetrics.GrossLossNoFee * 100 : 0;

            sb.AppendLine($"                <tr><td><strong>Net Profit</strong></td><td class='{(allMetrics.NetProfitNoFee > 0 ? "positive" : "negative")}'>${allMetrics.NetProfitNoFee:N2}</td><td class='{(allMetrics.NetProfitWithFee > 0 ? "positive" : "negative")}'>${allMetrics.NetProfitWithFee:N2}</td><td class='negative'>{netProfitImpact:F1}%</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Gross Profit</strong></td><td class='positive'>${allMetrics.GrossProfitNoFee:N2}</td><td class='positive'>${allMetrics.GrossProfitWithFee:N2}</td><td class='negative'>{grossProfitImpact:F1}%</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Gross Loss</strong></td><td class='negative'>${allMetrics.GrossLossNoFee:N2}</td><td class='negative'>${allMetrics.GrossLossWithFee:N2}</td><td class='negative'>{grossLossImpact:F1}%</td></tr>");
            sb.AppendLine("                <tr><td colspan='4'></td></tr>");

            // 盈亏比指标
            sb.AppendLine($"                <tr><td><strong>Profit Factor</strong></td><td>{allMetrics.ProfitFactor:F2}</td><td>{allMetrics.ProfitFactor:F2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Average Win</strong></td><td class='positive'>${allMetrics.AverageWin:N2}</td><td class='positive'>${allMetrics.AverageWin:N2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Average Loss</strong></td><td class='negative'>${Math.Abs(allMetrics.AverageLoss):N2}</td><td class='negative'>${Math.Abs(allMetrics.AverageLoss):N2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Risk/Reward Ratio</strong></td><td>{allMetrics.RiskRewardRatio:F2}</td><td>{allMetrics.RiskRewardRatio:F2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Largest Win</strong></td><td class='positive'>${allMetrics.LargestWin:N2}</td><td class='positive'>${allMetrics.LargestWin:N2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Largest Loss</strong></td><td class='negative'>${Math.Abs(allMetrics.LargestLoss):N2}</td><td class='negative'>${Math.Abs(allMetrics.LargestLoss):N2}</td><td>-</td></tr>");
            sb.AppendLine("                <tr><td colspan='4'></td></tr>");

            // 回撤指标
            sb.AppendLine($"                <tr><td><strong>Max Drawdown</strong></td><td class='negative'>${allMetrics.MaxDrawdown:N2}</td><td class='negative'>${allMetrics.MaxDrawdown:N2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Max Drawdown %</strong></td><td class='negative'>{allMetrics.MaxDrawdownPct:F2}%</td><td class='negative'>{allMetrics.MaxDrawdownPct:F2}%</td><td>-</td></tr>");
            sb.AppendLine("                <tr><td colspan='4'></td></tr>");

            // 风险调整收益指标
            sb.AppendLine($"                <tr><td><strong>Quality Ratio (Per-Trade)</strong></td><td>{allMetrics.QualityRatio:F2}</td><td>{allMetrics.QualityRatio:F2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Sharpe Ratio (Daily)</strong></td><td>{allMetrics.SharpeRatioDaily:F2}</td><td>{allMetrics.SharpeRatioDaily:F2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Annualized Sharpe</strong></td><td>{allMetrics.AnnualizedSharpe:F2}</td><td>{allMetrics.AnnualizedSharpe:F2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Realized Annualized Sharpe</strong></td><td>{allMetrics.RealizedAnnualizedSharpe:F2}</td><td>{allMetrics.RealizedAnnualizedSharpe:F2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Sortino Ratio</strong></td><td>{allMetrics.SortinoRatio:F2}</td><td>{allMetrics.SortinoRatio:F2}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Calmar Ratio</strong></td><td>{allMetrics.CalmarRatio:F2}</td><td>{allMetrics.CalmarRatio:F2}</td><td>-</td></tr>");
            sb.AppendLine("                <tr><td colspan='4'></td></tr>");

            // 时间指标
            sb.AppendLine($"                <tr><td><strong>Avg Holding Time (bars)</strong></td><td>{allMetrics.AvgHoldingBars:F1}</td><td>{allMetrics.AvgHoldingBars:F1}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Trading Days</strong></td><td>{allMetrics.TradingDays}</td><td>{allMetrics.TradingDays}</td><td>-</td></tr>");
            sb.AppendLine($"                <tr><td><strong>Avg Trades per Day</strong></td><td>{allMetrics.AvgTradesPerDay:F2}</td><td>{allMetrics.AvgTradesPerDay:F2}</td><td>-</td></tr>");

            sb.AppendLine("            </table>");
            sb.AppendLine("        </div>");

            // 分方向对比（如果有数据）
            if (directionMetrics != null && directionMetrics.Count > 0)
            {
                sb.Append(GenerateDirectionComparisonHtml(directionMetrics));
            }

            // 时段细分（如果有数据）
            if (allMetrics.TradesInOpeningPeriod > 0 || allMetrics.TradesInClosingPeriod > 0)
            {
                sb.Append(GenerateSessionTimingHtml(allMetrics));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 生成方向对比表格
        /// </summary>
        private string GenerateDirectionComparisonHtml(Dictionary<string, TradingAnalyzer.AnalysisMetrics> directionMetrics)
        {
            var sb = new StringBuilder();
            sb.AppendLine("        <div class='section'>");
            sb.AppendLine("            <h2>Long vs Short Performance</h2>");
            sb.AppendLine("            <table>");
            sb.AppendLine("                <tr><th>Metric</th><th>Long</th><th>Short</th><th>Difference</th></tr>");

            if (directionMetrics.TryGetValue("Long", out var longMetrics) && directionMetrics.TryGetValue("Short", out var shortMetrics))
            {
                sb.AppendLine($"                <tr><td><strong>Trades</strong></td><td>{longMetrics.TotalTrades}</td><td>{shortMetrics.TotalTrades}</td><td>{longMetrics.TotalTrades - shortMetrics.TotalTrades}</td></tr>");
                sb.AppendLine($"                <tr><td><strong>Win Rate</strong></td><td>{longMetrics.WinRate:F2}%</td><td>{shortMetrics.WinRate:F2}%</td><td>{longMetrics.WinRate - shortMetrics.WinRate:F2}%</td></tr>");
                sb.AppendLine($"                <tr><td><strong>Net Profit</strong></td><td class='{(longMetrics.NetProfitWithFee > 0 ? "positive" : "negative")}'>${longMetrics.NetProfitWithFee:N2}</td><td class='{(shortMetrics.NetProfitWithFee > 0 ? "positive" : "negative")}'>${shortMetrics.NetProfitWithFee:N2}</td><td class='{(longMetrics.NetProfitWithFee - shortMetrics.NetProfitWithFee > 0 ? "positive" : "negative")}'>${longMetrics.NetProfitWithFee - shortMetrics.NetProfitWithFee:N2}</td></tr>");
                sb.AppendLine($"                <tr><td><strong>Profit Factor</strong></td><td>{longMetrics.ProfitFactor:F2}</td><td>{shortMetrics.ProfitFactor:F2}</td><td>{longMetrics.ProfitFactor - shortMetrics.ProfitFactor:F2}</td></tr>");
                sb.AppendLine($"                <tr><td><strong>Avg P&L</strong></td><td class='{(longMetrics.NetProfitWithFee / longMetrics.TotalTrades > 0 ? "positive" : "negative")}'>${longMetrics.NetProfitWithFee / longMetrics.TotalTrades:N2}</td><td class='{(shortMetrics.NetProfitWithFee / shortMetrics.TotalTrades > 0 ? "positive" : "negative")}'>${shortMetrics.NetProfitWithFee / shortMetrics.TotalTrades:N2}</td><td>-</td></tr>");
            }

            sb.AppendLine("            </table>");
            sb.AppendLine("        </div>");
            return sb.ToString();
        }

        /// <summary>
        /// 生成时段细分表格
        /// </summary>
        private string GenerateSessionTimingHtml(TradingAnalyzer.AnalysisMetrics allMetrics)
        {
            var sb = new StringBuilder();
            sb.AppendLine("        <div class='section'>");
            sb.AppendLine("            <h2>Session Timing Analysis</h2>");
            sb.AppendLine("            <table>");
            sb.AppendLine("                <tr><th>Period</th><th>Trades</th><th>Win Rate</th><th>Avg P&L</th><th>Strategy</th></tr>");
            sb.AppendLine($"                <tr><td>Opening (First 30min)</td><td>{allMetrics.TradesInOpeningPeriod}</td><td>{allMetrics.OpeningPeriodWinRate:F1}%</td><td>${allMetrics.OpeningPeriodAvgPnL:N2}</td><td>{(allMetrics.OpeningPeriodWinRate < 45 ? "⚠️ Avoid or reduce size" : "✅")}</td></tr>");
            sb.AppendLine($"                <tr><td>Closing (Last 30min)</td><td>{allMetrics.TradesInClosingPeriod}</td><td>{allMetrics.ClosingPeriodWinRate:F1}%</td><td>${allMetrics.ClosingPeriodAvgPnL:N2}</td><td>{(allMetrics.ClosingPeriodWinRate > 55 ? "✅ Focus here" : "")}</td></tr>");
            sb.AppendLine("            </table>");
            sb.AppendLine("        </div>");
            return sb.ToString();
        }

        /// <summary>
        /// 生成新增的4个热图HTML和JavaScript代码
        /// </summary>
        private string GenerateNewHeatmapsHtml(
            TradingAnalyzer.DateHourHeatmap dateHourHeatmap,
            TradingAnalyzer.WeekdayHourHeatmap weekdayHourHeatmap,
            TradingAnalyzer.MonthHourHeatmap monthHourHeatmap,
            TradingAnalyzer.QualityHeatmap qualityHeatmap)
        {
            var sb = new StringBuilder();

            // 新热图区域标题
            sb.AppendLine("    <div class='section'>");
            sb.AppendLine("        <h2>Trading Heatmaps - Time-Based Performance Analysis</h2>");

            // 热图1: 日历式日内时段热图（Date × Hour）
            sb.AppendLine("        <div style='margin: 30px 0;'>");
            sb.AppendLine("            <h3 style='margin-bottom: 15px; color: #666;'>Daily Hour Heatmap - Date × Hour</h3>");
            sb.AppendLine("            <p style='color: #888; margin-bottom: 15px;'>识别每天各小时时段的收益分布，发现最佳交易窗口</p>");
            sb.AppendLine("            <div id='dateHourHeatmap' style='width: 100%; height: 600px;'></div>");
            sb.AppendLine("        </div>");

            // 热图2: 星期-小时聚合热图（Weekday × Hour）
            sb.AppendLine("        <div style='margin: 30px 0;'>");
            sb.AppendLine("            <h3 style='margin-bottom: 15px; color: #666;'>Weekly Hour Heatmap - Weekday × Hour</h3>");
            sb.AppendLine("            <p style='color: #888; margin-bottom: 15px;'>识别每周各小时时段的聚合表现，发现周期性规律</p>");
            sb.AppendLine("            <div id='weekdayHourHeatmap' style='width: 100%; height: 600px;'></div>");
            sb.AppendLine("        </div>");

            // 热图3: 月份-小时热图（Month × Hour）
            sb.AppendLine("        <div style='margin: 30px 0;'>");
            sb.AppendLine("            <h3 style='margin-bottom: 15px; color: #666;'>Monthly Hour Heatmap - Month × Hour</h3>");
            sb.AppendLine("            <p style='color: #888; margin-bottom: 15px;'>分析不同月份各小时时段的表现，识别季节性规律</p>");
            sb.AppendLine("            <div id='monthHourHeatmap' style='width: 100%; height: 600px;'></div>");
            sb.AppendLine("        </div>");

            // 热图4: 交易质量热图（Weekday × Hour - Win Rate）
            sb.AppendLine("        <div style='margin: 30px 0;'>");
            sb.AppendLine("            <h3 style='margin-bottom: 15px; color: #666;'>Quality Heatmap - Weekday × Hour (Win Rate)</h3>");
            sb.AppendLine("            <p style='color: #888; margin-bottom: 15px;'>关注交易质量分布，识别高胜率时段</p>");
            sb.AppendLine("            <div id='qualityHeatmap' style='width: 100%; height: 600px;'></div>");
            sb.AppendLine("        </div>");

            sb.AppendLine("    </div>");

            // JavaScript代码
            sb.AppendLine("    <script>");

            // === 热图1: Date × Hour ===
            sb.AppendLine("        // Date × Hour Heatmap");
            sb.AppendLine("        {");
            sb.AppendLine("            var xLabels = " + System.Text.Json.JsonSerializer.Serialize(dateHourHeatmap.Dates) + ";");
            sb.AppendLine("            var yLabels = " + System.Text.Json.JsonSerializer.Serialize(dateHourHeatmap.Hours) + ";");

            sb.AppendLine("            var zValues = [");
            for (int y = 0; y < 24; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < dateHourHeatmap.Dates.Count; x++)
                {
                    if (x > 0) sb.Append(", ");
                    sb.Append(dateHourHeatmap.AvgPnLTicks[y, x].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.AppendLine("]" + (y < 23 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var textValues = [");
            for (int y = 0; y < 24; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < dateHourHeatmap.Dates.Count; x++)
                {
                    if (x > 0) sb.Append(", ");
                    int count = dateHourHeatmap.TradeCounts[y, x];
                    decimal pnl = dateHourHeatmap.AvgPnLTicks[y, x];
                    decimal winRate = dateHourHeatmap.WinRates[y, x];
                    sb.Append($"'{count} trades<br>{pnl:F1} ticks<br>{winRate:F0}% WR'");
                }
                sb.AppendLine("]" + (y < 23 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var data = [{");
            sb.AppendLine("                type: 'heatmap',");
            sb.AppendLine("                x: xLabels,");
            sb.AppendLine("                y: yLabels,");
            sb.AppendLine("                z: zValues,");
            sb.AppendLine("                text: textValues,");
            sb.AppendLine("                hovertemplate: 'Date: %{x}<br>Hour: %{y}<br>Cumulative Ticks: %{z:.1f}<br>%{text}<extra></extra>',");
            sb.AppendLine("                colorscale: 'RdYlGn',");
            sb.AppendLine("                showscale: true");
            sb.AppendLine("            }];");

            sb.AppendLine("            var layout = {");
            sb.AppendLine("                xaxis: { title: 'Date', side: 'bottom' },");
            sb.AppendLine("                yaxis: { title: 'Hour', autorange: 'reversed' },");
            sb.AppendLine("                margin: { l: 80, r: 50, t: 30, b: 80 }");
            sb.AppendLine("            };");

            sb.AppendLine("            Plotly.newPlot('dateHourHeatmap', data, layout, { responsive: true });");
            sb.AppendLine("        }");
            sb.AppendLine("");

            // === 热图2: Weekday × Hour ===
            sb.AppendLine("        // Weekday × Hour Heatmap");
            sb.AppendLine("        {");
            sb.AppendLine("            var xLabels = " + System.Text.Json.JsonSerializer.Serialize(weekdayHourHeatmap.Weekdays) + ";");
            sb.AppendLine("            var yLabels = " + System.Text.Json.JsonSerializer.Serialize(weekdayHourHeatmap.Hours) + ";");

            sb.AppendLine("            var zValues = [");
            for (int y = 0; y < 24; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < 7; x++)
                {
                    if (x > 0) sb.Append(", ");
                    sb.Append(weekdayHourHeatmap.AvgPnLTicks[y, x].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.AppendLine("]" + (y < 23 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var textValues = [");
            for (int y = 0; y < 24; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < 7; x++)
                {
                    if (x > 0) sb.Append(", ");
                    int count = weekdayHourHeatmap.TradeCounts[y, x];
                    decimal pnl = weekdayHourHeatmap.AvgPnLTicks[y, x];
                    decimal winRate = weekdayHourHeatmap.WinRates[y, x];
                    sb.Append($"'{count} trades<br>{pnl:F1} ticks<br>{winRate:F0}% WR'");
                }
                sb.AppendLine("]" + (y < 23 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var data = [{");
            sb.AppendLine("                type: 'heatmap',");
            sb.AppendLine("                x: xLabels,");
            sb.AppendLine("                y: yLabels,");
            sb.AppendLine("                z: zValues,");
            sb.AppendLine("                text: textValues,");
            sb.AppendLine("                hovertemplate: 'Weekday: %{x}<br>Hour: %{y}<br>Cumulative Ticks: %{z:.1f}<br>%{text}<extra></extra>',");
            sb.AppendLine("                colorscale: 'RdYlGn',");
            sb.AppendLine("                showscale: true");
            sb.AppendLine("            }];");

            sb.AppendLine("            var layout = {");
            sb.AppendLine("                xaxis: { title: 'Day of Week', side: 'bottom' },");
            sb.AppendLine("                yaxis: { title: 'Hour', autorange: 'reversed' },");
            sb.AppendLine("                margin: { l: 80, r: 50, t: 30, b: 80 }");
            sb.AppendLine("            };");

            sb.AppendLine("            Plotly.newPlot('weekdayHourHeatmap', data, layout, { responsive: true });");
            sb.AppendLine("        }");
            sb.AppendLine("");

            // === 热图3: Month × Hour ===
            sb.AppendLine("        // Month × Hour Heatmap");
            sb.AppendLine("        {");
            sb.AppendLine("            var xLabels = " + System.Text.Json.JsonSerializer.Serialize(monthHourHeatmap.Months) + ";");
            sb.AppendLine("            var yLabels = " + System.Text.Json.JsonSerializer.Serialize(monthHourHeatmap.Hours) + ";");

            sb.AppendLine("            var zValues = [");
            for (int y = 0; y < 24; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < 12; x++)
                {
                    if (x > 0) sb.Append(", ");
                    sb.Append(monthHourHeatmap.AvgPnLTicks[y, x].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.AppendLine("]" + (y < 23 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var textValues = [");
            for (int y = 0; y < 24; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < 12; x++)
                {
                    if (x > 0) sb.Append(", ");
                    int count = monthHourHeatmap.TradeCounts[y, x];
                    decimal pnl = monthHourHeatmap.AvgPnLTicks[y, x];
                    decimal winRate = monthHourHeatmap.WinRates[y, x];
                    sb.Append($"'{count} trades<br>{pnl:F1} ticks<br>{winRate:F0}% WR'");
                }
                sb.AppendLine("]" + (y < 23 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var data = [{");
            sb.AppendLine("                type: 'heatmap',");
            sb.AppendLine("                x: xLabels,");
            sb.AppendLine("                y: yLabels,");
            sb.AppendLine("                z: zValues,");
            sb.AppendLine("                text: textValues,");
            sb.AppendLine("                hovertemplate: 'Month: %{x}<br>Hour: %{y}<br>Cumulative Ticks: %{z:.1f}<br>%{text}<extra></extra>',");
            sb.AppendLine("                colorscale: 'RdYlGn',");
            sb.AppendLine("                showscale: true");
            sb.AppendLine("            }];");

            sb.AppendLine("            var layout = {");
            sb.AppendLine("                xaxis: { title: 'Month', side: 'bottom' },");
            sb.AppendLine("                yaxis: { title: 'Hour', autorange: 'reversed' },");
            sb.AppendLine("                margin: { l: 80, r: 50, t: 30, b: 80 }");
            sb.AppendLine("            };");

            sb.AppendLine("            Plotly.newPlot('monthHourHeatmap', data, layout, { responsive: true });");
            sb.AppendLine("        }");
            sb.AppendLine("");

            // === 热图4: Quality (Weekday × Hour - Win Rate) ===
            sb.AppendLine("        // Quality Heatmap - Win Rate");
            sb.AppendLine("        {");
            sb.AppendLine("            var xLabels = " + System.Text.Json.JsonSerializer.Serialize(qualityHeatmap.Weekdays) + ";");
            sb.AppendLine("            var yLabels = " + System.Text.Json.JsonSerializer.Serialize(qualityHeatmap.Hours) + ";");

            sb.AppendLine("            var zValues = [");
            for (int y = 0; y < 24; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < 7; x++)
                {
                    if (x > 0) sb.Append(", ");
                    sb.Append(qualityHeatmap.WinRates[y, x].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.AppendLine("]" + (y < 23 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var textValues = [");
            for (int y = 0; y < 24; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < 7; x++)
                {
                    if (x > 0) sb.Append(", ");
                    int count = qualityHeatmap.TradeCounts[y, x];
                    decimal winRate = qualityHeatmap.WinRates[y, x];
                    decimal profitFactor = qualityHeatmap.ProfitFactors[y, x];
                    decimal avgTicks = qualityHeatmap.AvgPnLTicks[y, x];
                    sb.Append($"'{count} trades<br>WR: {winRate:F1}%<br>PF: {profitFactor:F2}<br>Avg: {avgTicks:F1} ticks'");
                }
                sb.AppendLine("]" + (y < 23 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var data = [{");
            sb.AppendLine("                type: 'heatmap',");
            sb.AppendLine("                x: xLabels,");
            sb.AppendLine("                y: yLabels,");
            sb.AppendLine("                z: zValues,");
            sb.AppendLine("                text: textValues,");
            sb.AppendLine("                hovertemplate: 'Weekday: %{x}<br>Hour: %{y}<br>Win Rate: %{z:.1f}%<br>%{text}<extra></extra>',");
            sb.AppendLine("                colorscale: [[0, '#eb3349'], [0.5, '#f5f5f5'], [1, '#38ef7d']],");
            sb.AppendLine("                showscale: true");
            sb.AppendLine("            }];");

            sb.AppendLine("            var layout = {");
            sb.AppendLine("                xaxis: { title: 'Day of Week', side: 'bottom' },");
            sb.AppendLine("                yaxis: { title: 'Hour', autorange: 'reversed' },");
            sb.AppendLine("                margin: { l: 80, r: 50, t: 30, b: 80 }");
            sb.AppendLine("            };");

            sb.AppendLine("            Plotly.newPlot('qualityHeatmap', data, layout, { responsive: true });");
            sb.AppendLine("        }");

            sb.AppendLine("    </script>");

            // 生成统计摘要和优化建议
            sb.Append(GenerateHeatmapInsights(weekdayHourHeatmap, qualityHeatmap));

            return sb.ToString();
        }

        /// <summary>
        /// 生成热图洞察摘要和优化建议
        /// </summary>
        private string GenerateHeatmapInsights(
            TradingAnalyzer.WeekdayHourHeatmap weekdayHourHeatmap,
            TradingAnalyzer.QualityHeatmap qualityHeatmap)
        {
            var sb = new StringBuilder();

            sb.AppendLine("        <div style='margin-top: 40px; padding: 20px; background: #f9f9f9; border-radius: 8px;'>");
            sb.AppendLine("            <h3 style='color: #333; margin-bottom: 20px;'>Trading Insights & Optimization Recommendations</h3>");

            // 找出最佳和最差时段
            var bestSlots = new List<(string weekday, string hour, decimal pnl, decimal winRate, int trades)>();
            var worstSlots = new List<(string weekday, string hour, decimal pnl, decimal winRate, int trades)>();

            for (int y = 0; y < 24; y++)
            {
                for (int x = 0; x < 7; x++)
                {
                    int tradeCount = weekdayHourHeatmap.TradeCounts[y, x];
                    if (tradeCount >= 3) // 只考虑至少有3笔交易的时段
                    {
                        string weekday = weekdayHourHeatmap.Weekdays[x];
                        string hour = weekdayHourHeatmap.Hours[y];
                        decimal pnl = weekdayHourHeatmap.AvgPnLTicks[y, x];
                        decimal winRate = qualityHeatmap.WinRates[y, x];

                        bestSlots.Add((weekday, hour, pnl, winRate, tradeCount));
                        worstSlots.Add((weekday, hour, pnl, winRate, tradeCount));
                    }
                }
            }

            var topBest = bestSlots.OrderByDescending(s => s.pnl).Take(5).ToList();
            var topWorst = worstSlots.OrderBy(s => s.pnl).Take(5).ToList();

            // 最佳时段 TOP 5
            sb.AppendLine("            <div style='margin-bottom: 30px;'>");
            sb.AppendLine("                <h4 style='color: #38ef7d; margin-bottom: 15px;'>✅ Best Time Slots (TOP 5)</h4>");
            sb.AppendLine("                <table style='width: 100%; background: white;'>");
            sb.AppendLine("                    <tr><th>Rank</th><th>Time Slot</th><th>Cumulative Ticks</th><th>Win Rate</th><th>Trades</th><th>Recommendation</th></tr>");

            for (int i = 0; i < topBest.Count; i++)
            {
                var slot = topBest[i];
                string recommendation = slot.winRate >= 55 ? "🔥 Focus here - High win rate & profitability" : "✅ Strong performance";
                sb.AppendLine($"                    <tr>");
                sb.AppendLine($"                        <td>{i + 1}</td>");
                sb.AppendLine($"                        <td><strong>{slot.weekday} {slot.hour}</strong></td>");
                sb.AppendLine($"                        <td class='positive'>{slot.pnl:F1} ticks</td>");
                sb.AppendLine($"                        <td>{slot.winRate:F1}%</td>");
                sb.AppendLine($"                        <td>{slot.trades}</td>");
                sb.AppendLine($"                        <td>{recommendation}</td>");
                sb.AppendLine($"                    </tr>");
            }

            sb.AppendLine("                </table>");
            sb.AppendLine("            </div>");

            // 最差时段 TOP 5
            sb.AppendLine("            <div style='margin-bottom: 30px;'>");
            sb.AppendLine("                <h4 style='color: #eb3349; margin-bottom: 15px;'>⚠️ Worst Time Slots (TOP 5)</h4>");
            sb.AppendLine("                <table style='width: 100%; background: white;'>");
            sb.AppendLine("                    <tr><th>Rank</th><th>Time Slot</th><th>Cumulative Ticks</th><th>Win Rate</th><th>Trades</th><th>Recommendation</th></tr>");

            for (int i = 0; i < topWorst.Count; i++)
            {
                var slot = topWorst[i];
                string recommendation = slot.winRate < 40 ? "🚫 Avoid trading - Low win rate" :
                    slot.pnl < -10 ? "⚠️ Reduce position size or skip" : "⚠️ Trade with caution";
                sb.AppendLine($"                    <tr>");
                sb.AppendLine($"                        <td>{i + 1}</td>");
                sb.AppendLine($"                        <td><strong>{slot.weekday} {slot.hour}</strong></td>");
                sb.AppendLine($"                        <td class='negative'>{slot.pnl:F1} ticks</td>");
                sb.AppendLine($"                        <td>{slot.winRate:F1}%</td>");
                sb.AppendLine($"                        <td>{slot.trades}</td>");
                sb.AppendLine($"                        <td>{recommendation}</td>");
                sb.AppendLine($"                    </tr>");
            }

            sb.AppendLine("                </table>");
            sb.AppendLine("            </div>");

            // 整体建议
            sb.AppendLine("            <div style='padding: 15px; background: white; border-left: 4px solid #667eea; border-radius: 4px;'>");
            sb.AppendLine("                <h4 style='color: #667eea; margin-bottom: 10px;'>💡 Key Takeaways</h4>");
            sb.AppendLine("                <ul style='line-height: 1.8;'>");

            if (topBest.Any())
            {
                var bestSlot = topBest.First();
                sb.AppendLine($"                    <li><strong>Best Performance:</strong> {bestSlot.weekday} at {bestSlot.hour} with {bestSlot.pnl:F1} cumulative ticks and {bestSlot.winRate:F1}% win rate</li>");
            }

            if (topWorst.Any())
            {
                var worstSlot = topWorst.First();
                sb.AppendLine($"                    <li><strong>Worst Performance:</strong> {worstSlot.weekday} at {worstSlot.hour} with {worstSlot.pnl:F1} cumulative ticks and {worstSlot.winRate:F1}% win rate</li>");
            }

            sb.AppendLine("                    <li><strong>Strategy:</strong> Focus trading activity during high-performing time slots and avoid or reduce size during low-performing periods</li>");
            sb.AppendLine("                    <li><strong>Time Management:</strong> Consider time-based filters in your trading strategy to automatically optimize entry timing</li>");
            sb.AppendLine("                </ul>");
            sb.AppendLine("            </div>");

            sb.AppendLine("        </div>");

            return sb.ToString();
        }

        /// <summary>
        /// 生成热力图HTML和JavaScript代码
        /// </summary>
        private string GenerateHeatmapsHtml(
            TradingAnalyzer.EntryHoldingHeatmap entryHoldingHeatmap,
            TradingAnalyzer.SessionDayHeatmap sessionDayHeatmap)
        {
            var sb = new StringBuilder();

            // 热力图容器
            sb.AppendLine("    <div class='section'>");
            sb.AppendLine("        <h2>Trading Heatmaps (24-Hour Intraday Analysis - UTC)</h2>");
            sb.AppendLine("        <div style='display: grid; grid-template-columns: 1fr 1fr; gap: 30px; margin-top: 20px;'>");

            // 入场时间×持仓时长热力图
            sb.AppendLine("            <div>");
            sb.AppendLine("                <h3 style='margin-bottom: 15px; color: #666;'>Entry Time × Holding Duration</h3>");
            sb.AppendLine("                <div id='entryHoldingHeatmap' style='width: 100%; height: 500px;'></div>");
            sb.AppendLine("            </div>");

            // 时段×星期热力图
            sb.AppendLine("            <div>");
            sb.AppendLine("                <h3 style='margin-bottom: 15px; color: #666;'>Global Session × Day of Week</h3>");
            sb.AppendLine("                <div id='sessionDayHeatmap' style='width: 100%; height: 500px;'></div>");
            sb.AppendLine("            </div>");

            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // Plotly.js 热力图JavaScript
            sb.AppendLine("    <script>");

            // === 入场时间×持仓时长热力图 ===
            sb.AppendLine("        // Entry Time × Holding Duration Heatmap");
            sb.AppendLine("        {");
            sb.AppendLine("            var xLabels = " + System.Text.Json.JsonSerializer.Serialize(entryHoldingHeatmap.TimeSlots) + ";");
            sb.AppendLine("            var yLabels = " + System.Text.Json.JsonSerializer.Serialize(entryHoldingHeatmap.HoldingBuckets) + ";");

            // Z值（平均盈亏）
            sb.AppendLine("            var zValues = [");
            for (int y = 0; y < entryHoldingHeatmap.HoldingBuckets.Count; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < entryHoldingHeatmap.TimeSlots.Count; x++)
                {
                    if (x > 0) sb.Append(", ");
                    sb.Append(entryHoldingHeatmap.AvgPnL[y, x].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.AppendLine("]" + (y < entryHoldingHeatmap.HoldingBuckets.Count - 1 ? "," : ""));
            }
            sb.AppendLine("            ];");

            // 文本标注
            sb.AppendLine("            var textValues = [");
            for (int y = 0; y < entryHoldingHeatmap.HoldingBuckets.Count; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < entryHoldingHeatmap.TimeSlots.Count; x++)
                {
                    if (x > 0) sb.Append(", ");
                    int count = entryHoldingHeatmap.TradeCounts[y, x];
                    decimal avgPnL = entryHoldingHeatmap.AvgPnL[y, x];
                    decimal winRate = entryHoldingHeatmap.WinRates[y, x];
                    sb.Append($"'{count}<br>${avgPnL:F0}<br>{winRate:F0}%'");
                }
                sb.AppendLine("]" + (y < entryHoldingHeatmap.HoldingBuckets.Count - 1 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var data = [{");
            sb.AppendLine("                type: 'heatmap',");
            sb.AppendLine("                x: xLabels,");
            sb.AppendLine("                y: yLabels,");
            sb.AppendLine("                z: zValues,");
            sb.AppendLine("                text: textValues,");
            sb.AppendLine("                hovertemplate: 'Entry: %{x}<br>Holding: %{y}<br>Avg P&L: $%{z:.2f}<br>%{text}<extra></extra>',");
            sb.AppendLine("                colorscale: 'RdYlGn',");
            sb.AppendLine("                showscale: true");
            sb.AppendLine("            }];");

            sb.AppendLine("            var layout = {");
            sb.AppendLine("                xaxis: { title: 'Entry Time (UTC)', side: 'bottom' },");
            sb.AppendLine("                yaxis: { title: 'Holding Duration' },");
            sb.AppendLine("                margin: { l: 100, r: 50, t: 30, b: 80 }");
            sb.AppendLine("            };");

            sb.AppendLine("            Plotly.newPlot('entryHoldingHeatmap', data, layout, { responsive: true });");
            sb.AppendLine("        }");

            // === 时段×星期热力图 ===
            sb.AppendLine("");
            sb.AppendLine("        // Global Session × Day of Week Heatmap");
            sb.AppendLine("        {");
            sb.AppendLine("            var xLabels = " + System.Text.Json.JsonSerializer.Serialize(sessionDayHeatmap.DaysOfWeek) + ";");
            sb.AppendLine("            var yLabels = " + System.Text.Json.JsonSerializer.Serialize(sessionDayHeatmap.Sessions) + ";");

            // Z值
            sb.AppendLine("            var zValues = [");
            for (int y = 0; y < sessionDayHeatmap.Sessions.Count; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < sessionDayHeatmap.DaysOfWeek.Count; x++)
                {
                    if (x > 0) sb.Append(", ");
                    sb.Append(sessionDayHeatmap.AvgPnL[y, x].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.AppendLine("]" + (y < sessionDayHeatmap.Sessions.Count - 1 ? "," : ""));
            }
            sb.AppendLine("            ];");

            // 文本标注
            sb.AppendLine("            var textValues = [");
            for (int y = 0; y < sessionDayHeatmap.Sessions.Count; y++)
            {
                sb.Append("                [");
                for (int x = 0; x < sessionDayHeatmap.DaysOfWeek.Count; x++)
                {
                    if (x > 0) sb.Append(", ");
                    int count = sessionDayHeatmap.TradeCounts[y, x];
                    decimal avgPnL = sessionDayHeatmap.AvgPnL[y, x];
                    decimal winRate = sessionDayHeatmap.WinRates[y, x];
                    sb.Append($"'{count}<br>${avgPnL:F0}<br>{winRate:F0}%'");
                }
                sb.AppendLine("]" + (y < sessionDayHeatmap.Sessions.Count - 1 ? "," : ""));
            }
            sb.AppendLine("            ];");

            sb.AppendLine("            var data = [{");
            sb.AppendLine("                type: 'heatmap',");
            sb.AppendLine("                x: xLabels,");
            sb.AppendLine("                y: yLabels,");
            sb.AppendLine("                z: zValues,");
            sb.AppendLine("                text: textValues,");
            sb.AppendLine("                hovertemplate: 'Day: %{x}<br>Session: %{y}<br>Avg P&L: $%{z:.2f}<br>%{text}<extra></extra>',");
            sb.AppendLine("                colorscale: 'RdYlGn',");
            sb.AppendLine("                showscale: true");
            sb.AppendLine("            }];");

            sb.AppendLine("            var layout = {");
            sb.AppendLine("                xaxis: { title: 'Day of Week', side: 'bottom' },");
            sb.AppendLine("                yaxis: { title: 'Trading Session (UTC)' },");
            sb.AppendLine("                margin: { l: 150, r: 50, t: 30, b: 80 }");
            sb.AppendLine("            };");

            sb.AppendLine("            Plotly.newPlot('sessionDayHeatmap', data, layout, { responsive: true });");
            sb.AppendLine("        }");
            sb.AppendLine("    </script>");

            return sb.ToString();
        }

        /// <summary>
        /// 生成权益曲线图JavaScript脚本
        /// </summary>
        private string GenerateEquityChartScript(List<TradingAnalyzer.TradeRecord> tradeRecords, decimal initialCapital)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    <script>");
            sb.AppendLine("        // Equity Curve Chart");
            sb.AppendLine("        {");
            sb.AppendLine("            const ctx = document.getElementById('equityChart').getContext('2d');");

            // 计算权益曲线数据
            var equityCurve = new List<decimal> { initialCapital };
            var labels = new List<string> { "Start" };
            decimal runningEquity = initialCapital;

            foreach (var trade in tradeRecords.OrderBy(t => t.CloseTime))
            {
                runningEquity += trade.PnLWithFee;
                equityCurve.Add(runningEquity);
                labels.Add(trade.CloseTime.ToString("MM/dd HH:mm"));
            }

            sb.AppendLine("            const labels = " + System.Text.Json.JsonSerializer.Serialize(labels) + ";");
            sb.AppendLine("            const equityData = " + System.Text.Json.JsonSerializer.Serialize(equityCurve.Select(e => (double)e)) + ";");

            sb.AppendLine("            new Chart(ctx, {");
            sb.AppendLine("                type: 'line',");
            sb.AppendLine("                data: {");
            sb.AppendLine("                    labels: labels,");
            sb.AppendLine("                    datasets: [{");
            sb.AppendLine("                        label: 'Account Equity',");
            sb.AppendLine("                        data: equityData,");
            sb.AppendLine("                        borderColor: '#667eea',");
            sb.AppendLine("                        backgroundColor: 'rgba(102, 126, 234, 0.1)',");
            sb.AppendLine("                        borderWidth: 2,");
            sb.AppendLine("                        fill: true,");
            sb.AppendLine("                        tension: 0.4");
            sb.AppendLine("                    }]");
            sb.AppendLine("                },");
            sb.AppendLine("                options: {");
            sb.AppendLine("                    responsive: true,");
            sb.AppendLine("                    maintainAspectRatio: true,");
            sb.AppendLine("                    plugins: {");
            sb.AppendLine("                        legend: { display: true, position: 'top' },");
            sb.AppendLine("                        tooltip: { mode: 'index', intersect: false }");
            sb.AppendLine("                    },");
            sb.AppendLine("                    scales: {");
            sb.AppendLine("                        y: { beginAtZero: false, ticks: { callback: function(value) { return '$' + value.toFixed(2); } } },");
            sb.AppendLine("                        x: { display: true, ticks: { maxTicksLimit: 10 } }");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine("            });");
            sb.AppendLine("        }");
            sb.AppendLine("    </script>");
            return sb.ToString();
        }

        #endregion
    }
}
