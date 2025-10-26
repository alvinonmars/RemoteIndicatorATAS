using System;
using System.Text.RegularExpressions;

namespace RemoteIndicator.ATAS.Utilities
{
    /// <summary>
    /// TimeFrame Converter - ATAS格式转换为Proto格式
    ///
    /// 职责:
    /// 1. ToProto(): ATAS (ChartType + TimeFrame) → Proto (resolution + num_units)
    /// 2. ValidateMatch(): 验证当前图表是否匹配请求的timeframe
    ///
    /// 支持的类型:
    /// - TimeFrame: M5, H4, D1 → MIN/H/D
    /// - Seconds: 30 → SECOND
    /// - Volume: 1000 → VOLUME
    /// - Tick: 100 → TICK
    ///
    /// 不支持: Renko, Range, 其他非标准类型
    ///
    /// CRITICAL: ChartType is STRING (from ChartInfo.ChartType), NOT an enum
    ///
    /// Thread-safe: 所有方法为静态且无状态
    /// </summary>
    public static class TimeframeConverter
    {
        #region 数据结构

        /// <summary>
        /// Proto格式的TimeFrame
        /// </summary>
        public struct ProtoTimeframe
        {
            public string Resolution;  // "SECOND", "MIN", "H", "D", "VOLUME", "TICK"
            public int NumUnits;       // 周期单位数量

            public ProtoTimeframe(string resolution, int numUnits)
            {
                Resolution = resolution;
                NumUnits = numUnits;
            }

            public override string ToString()
            {
                return $"{Resolution}({NumUnits})";
            }
        }

        #endregion

        #region 支持的图表类型

        /// <summary>
        /// 检查是否为支持的图表类型
        /// </summary>
        /// <param name="chartType">ATAS chart type string (from ChartInfo.ChartType)</param>
        /// <returns>True if supported</returns>
        public static bool IsSupportedChartType(string chartType)
        {
            if (string.IsNullOrEmpty(chartType))
                return false;

            return chartType.ToUpper() switch
            {
                "TIMEFRAME" => true,
                "SECONDS" => true,
                "VOLUME" => true,
                "TICK" => true,
                "RANGEUS" => true,
                _ => false
            };
        }

        #endregion

        #region ATAS → Proto

        /// <summary>
        /// 转换ATAS TimeFrame为Proto格式
        /// </summary>
        /// <param name="chartType">ATAS图表类型字符串 (from ChartInfo.ChartType)</param>
        /// <param name="timeFrame">ATAS TimeFrame字符串 (from ChartInfo.TimeFrame.ToString())</param>
        /// <returns>Proto格式的resolution和num_units</returns>
        /// <exception cref="NotSupportedException">不支持的图表类型</exception>
        /// <exception cref="FormatException">TimeFrame格式无法解析</exception>
        public static ProtoTimeframe ToProto(string chartType, string timeFrame)
        {
            if (string.IsNullOrWhiteSpace(timeFrame))
            {
                throw new ArgumentException("TimeFrame cannot be null or empty", nameof(timeFrame));
            }

            if (string.IsNullOrWhiteSpace(chartType))
            {
                throw new ArgumentException("ChartType cannot be null or empty", nameof(chartType));
            }

            if (!IsSupportedChartType(chartType))
            {
                throw new NotSupportedException(
                    $"Unsupported chart type: {chartType}. " +
                    $"Supported types: TimeFrame, Seconds, Volume, Tick"
                );
            }

            return chartType.ToUpper() switch
            {
                "TIMEFRAME" => ParseTimeFrameType(timeFrame),
                "SECONDS" => new ProtoTimeframe("SECOND", ParseNumericValue(timeFrame, "Seconds")),
                "VOLUME" => new ProtoTimeframe("VOLUME", ParseNumericValue(timeFrame, "Volume")),
                "TICK" => new ProtoTimeframe("TICK", ParseNumericValue(timeFrame, "Tick")),
                "RANGEUS" => new ProtoTimeframe("RANGEUS", ParseNumericValue(timeFrame, "RangeUS")),
                _ => throw new NotSupportedException($"Chart type {chartType} not handled")
            };
        }

        /// <summary>
        /// 解析TimeFrame类型（分钟/小时/日）
        /// </summary>
        private static ProtoTimeframe ParseTimeFrameType(string timeFrame)
        {
            // ATAS TimeFrame格式: "M5", "M15", "H1", "H4", "D1"
            // 提取前缀（M/H/D）和数字

            var match = Regex.Match(timeFrame, @"^([MHD])(\d+)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new FormatException(
                    $"Cannot parse TimeFrame: {timeFrame}. " +
                    $"Expected format: M5, H4, D1"
                );
            }

            string prefix = match.Groups[1].Value.ToUpper();
            int numUnits = int.Parse(match.Groups[2].Value);

            string resolution = prefix switch
            {
                "M" => "MIN",
                "H" => "H",
                "D" => "D",
                _ => throw new FormatException($"Unknown TimeFrame prefix: {prefix}")
            };

            return new ProtoTimeframe(resolution, numUnits);
        }

        /// <summary>
        /// 解析纯数字类型的TimeFrame（秒/成交量/Tick）
        /// </summary>
        private static int ParseNumericValue(string timeFrame, string typeName)
        {
            if (int.TryParse(timeFrame, out int value) && value > 0)
            {
                return value;
            }

            throw new FormatException(
                $"Cannot parse {typeName} TimeFrame: {timeFrame}. " +
                $"Expected positive integer (e.g., 30, 1000)"
            );
        }

        #endregion

        #region 验证匹配

        /// <summary>
        /// 验证当前图表是否匹配请求的timeframe
        /// </summary>
        /// <param name="currentChartType">当前图表类型字符串 (from ChartInfo.ChartType)</param>
        /// <param name="currentTimeFrame">当前TimeFrame字符串 (from ChartInfo.TimeFrame.ToString())</param>
        /// <param name="requestedResolution">请求的resolution</param>
        /// <param name="requestedNumUnits">请求的num_units</param>
        /// <returns>是否匹配</returns>
        /// <remarks>
        /// 用于DataTerminal响应DataRequest时验证请求是否匹配当前图表
        /// </remarks>
        public static bool ValidateMatch(
            string currentChartType,
            string currentTimeFrame,
            string requestedResolution,
            int requestedNumUnits)
        {
            try
            {
                // 将当前图表转换为Proto格式
                var currentProto = ToProto(currentChartType, currentTimeFrame);

                // 与请求的resolution/num_units比较
                return currentProto.Resolution.Equals(requestedResolution, StringComparison.OrdinalIgnoreCase) &&
                       currentProto.NumUnits == requestedNumUnits;
            }
            catch
            {
                // 如果转换失败（不支持的类型或格式错误），视为不匹配
                return false;
            }
        }

        /// <summary>
        /// 生成timeframe不匹配的错误消息
        /// </summary>
        public static string GenerateMismatchMessage(
            string currentChartType,
            string currentTimeFrame,
            string requestedResolution,
            int requestedNumUnits)
        {
            try
            {
                var current = ToProto(currentChartType, currentTimeFrame);
                return $"Timeframe mismatch: Chart={current.Resolution}({current.NumUnits}), Request={requestedResolution}({requestedNumUnits})";
            }
            catch
            {
                return $"Timeframe mismatch: Chart={currentChartType}/{currentTimeFrame}, Request={requestedResolution}({requestedNumUnits})";
            }
        }

        #endregion
    }
}
