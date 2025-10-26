using System;

namespace RemoteIndicator.ATAS.Utilities
{
    /// <summary>
    /// DateTime Helper - 时区和时间戳转换工具
    ///
    /// 职责:
    /// 1. EnsureUtc(): 确保DateTime是UTC Kind
    /// 2. ToUnixMs(): 安全地转换为Unix时间戳（毫秒）
    ///
    /// ATAS API 行为:
    /// - candle.Time/LastTime: 实际是UTC时间，但Kind=Unspecified
    /// - trade.Time: 实际是UTC时间，但Kind=Unspecified
    ///
    /// 策略:
    /// - Utc → 保持不变
    /// - Unspecified → 修正Kind为Utc（假定ATAS返回UTC但标记错误）
    /// - Local → 转换为UTC
    ///
    /// Thread-safe: 所有方法为静态且无状态
    /// </summary>
    public static class DateTimeHelper
    {
        /// <summary>
        /// 确保DateTime是UTC Kind
        /// </summary>
        /// <param name="dateTime">输入时间</param>
        /// <returns>UTC Kind的DateTime</returns>
        /// <remarks>
        /// 策略（基于ATAS实际返回UTC但Kind=Unspecified的观察）:
        /// - Kind.Utc → 直接返回（已经正确）
        /// - Kind.Unspecified → 修正Kind为Utc（ATAS行为）
        /// - Kind.Local → 转换为UTC（真正的本地时间）
        /// </remarks>
        public static DateTime EnsureUtc(DateTime dateTime)
        {
            switch (dateTime.Kind)
            {
                case DateTimeKind.Utc:
                    // 已经是UTC，直接返回
                    return dateTime;

                case DateTimeKind.Unspecified:
                    // ATAS特殊处理：实际是UTC但标记为Unspecified
                    // 只修正Kind，不改变时间值
                    return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);

                case DateTimeKind.Local:
                    // 真正的本地时间，需要转换
                    return dateTime.ToUniversalTime();

                default:
                    // 不应该到达这里，但防御性编程
                    throw new ArgumentException($"Unknown DateTimeKind: {dateTime.Kind}", nameof(dateTime));
            }
        }

        /// <summary>
        /// 安全地将DateTime转换为Unix时间戳（毫秒）
        /// </summary>
        /// <param name="dateTime">输入时间</param>
        /// <returns>Unix时间戳（毫秒）</returns>
        /// <remarks>
        /// 自动调用EnsureUtc确保时区正确
        /// </remarks>
        public static long ToUnixMs(DateTime dateTime)
        {
            var utcTime = EnsureUtc(dateTime);
            return new DateTimeOffset(utcTime).ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// 从Unix时间戳（毫秒）转换为UTC DateTime
        /// </summary>
        /// <param name="unixMs">Unix时间戳（毫秒）</param>
        /// <returns>UTC DateTime</returns>
        public static DateTime FromUnixMs(long unixMs)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        }
    }
}
