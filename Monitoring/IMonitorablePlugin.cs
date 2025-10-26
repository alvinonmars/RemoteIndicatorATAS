using System;
using System.Collections.Generic;

namespace RemoteIndicator.ATAS.Monitoring
{
    /// <summary>
    /// 可监控插件接口 - 所有需要注册到控制面板的插件必须实现此接口
    ///
    /// 职责：
    /// 1. 提供插件标识信息（ID、名称、类型）
    /// 2. 暴露连接状态和活跃时间
    /// 3. 提供统计数据快照
    /// 4. 触发状态变化事件
    /// 5. 支持扩展操作（登录、风控等预留）
    ///
    /// Thread-Safety: 实现类需要保证所有属性的线程安全访问
    /// </summary>
    public interface IMonitorablePlugin
    {
        #region 基础标识

        /// <summary>
        /// 插件唯一ID
        /// 格式: PluginType_Symbol_ChartType_Timeframe
        /// 示例: "DataTerminal_ES_TIMEFRAME_M5", "RemoteIndicator_NQ_SECONDS_30"
        /// </summary>
        string PluginId { get; }

        /// <summary>
        /// 插件显示名称（用于UI显示）
        /// 示例: "Data Terminal (ES/M5)", "Peak Zones (NQ/30s)"
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// 插件类型
        /// </summary>
        PluginType Type { get; }

        #endregion

        #region 连接状态

        /// <summary>
        /// 是否已连接到Service
        /// DataTerminal: Channel B + C都连接
        /// RemoteIndicator: Channel A连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 连接建立时间（null表示从未连接或已断开）
        /// </summary>
        DateTime? ConnectedSince { get; }

        /// <summary>
        /// 最后活跃时间（收/发消息时更新）
        /// </summary>
        DateTime? LastActiveTime { get; }

        #endregion

        #region 通信统计

        /// <summary>
        /// 获取实时统计数据快照（线程安全）
        /// </summary>
        /// <returns>不可变统计数据结构</returns>
        PluginStatistics GetStatistics();

        #endregion

        #region 状态变化事件

        /// <summary>
        /// 状态变化事件（连接/断开/错误等）
        /// </summary>
        /// <param name="plugin">触发事件的插件</param>
        /// <param name="message">状态描述消息</param>
        event Action<IMonitorablePlugin, string> StatusChanged;

        #endregion

        #region 扩展点（未来使用，现在预留）

        /// <summary>
        /// 执行自定义操作（预留扩展点）
        ///
        /// Phase 2支持的操作：
        /// - "Reconnect": 触发重连
        /// - "Login": 登录Service（参数: Username, ApiKey）
        ///
        /// Phase 3支持的操作：
        /// - "EnableRiskControl": 启用风控（参数: Enabled, MaxDailyLoss）
        ///
        /// </summary>
        /// <param name="actionType">操作类型</param>
        /// <param name="parameters">操作参数（可为null）</param>
        /// <returns>操作结果描述</returns>
        string ExecuteAction(string actionType, Dictionary<string, object> parameters);

        #endregion
    }

    /// <summary>
    /// 插件类型枚举
    /// </summary>
    public enum PluginType
    {
        /// <summary>数据终端（Channel B推送 + Channel C查询）</summary>
        DataTerminal,

        /// <summary>远程指标（Channel A请求-响应）</summary>
        RemoteIndicator
    }

    /// <summary>
    /// 插件统计数据（不可变结构体，线程安全）
    /// </summary>
    public struct PluginStatistics
    {
        #region 通用统计

        /// <summary>发送的消息总数</summary>
        public long MessagesSent { get; init; }

        /// <summary>接收的消息总数</summary>
        public long MessagesReceived { get; init; }

        /// <summary>发送失败次数</summary>
        public long SendFailures { get; init; }

        /// <summary>接收失败次数</summary>
        public long ReceiveFailures { get; init; }

        #endregion

        #region Channel特定统计

        /// <summary>DataTerminal: Channel B推送的bar数量</summary>
        public long BarsPushed { get; init; }

        /// <summary>DataTerminal: Channel C查询返回的bar数量</summary>
        public long BarsQueried { get; init; }

        /// <summary>RemoteIndicator: 发送的指标请求数量</summary>
        public long IndicatorRequests { get; init; }

        /// <summary>RemoteIndicator: 接收的指标元素数量</summary>
        public long ElementsReceived { get; init; }

        #endregion

        #region 错误信息

        /// <summary>最后一次错误消息</summary>
        public string LastError { get; init; }

        /// <summary>最后一次错误时间</summary>
        public DateTime? LastErrorTime { get; init; }

        #endregion

        /// <summary>
        /// 计算总错误数
        /// </summary>
        public long TotalErrors => SendFailures + ReceiveFailures;

        /// <summary>
        /// 判断是否有错误
        /// </summary>
        public bool HasErrors => TotalErrors > 0;

        /// <summary>
        /// 格式化统计信息（用于日志）
        /// </summary>
        public override string ToString()
        {
            return $"Sent={MessagesSent}, Recv={MessagesReceived}, Errors={TotalErrors}";
        }
    }
}
