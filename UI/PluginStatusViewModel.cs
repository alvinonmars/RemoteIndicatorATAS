using System;
using System.ComponentModel;
using System.Windows.Media;
using RemoteIndicator.ATAS.Monitoring;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace RemoteIndicator.ATAS.UI
{
    /// <summary>
    /// 插件状态ViewModel - 用于WPF DataGrid数据绑定
    ///
    /// 职责：
    /// 1. 包装IMonitorablePlugin，提供UI友好的属性
    /// 2. 实现INotifyPropertyChanged，支持数据绑定
    /// 3. 提供格式化的显示文本和颜色
    /// 4. 缓存统计数据，避免频繁调用GetStatistics()
    /// </summary>
    public class PluginStatusViewModel : INotifyPropertyChanged
    {
        #region Private Fields

        private readonly IMonitorablePlugin _plugin;
        private PluginStatistics _cachedStatistics;

        #endregion

        #region Constructor

        public PluginStatusViewModel(IMonitorablePlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            Refresh(); // 初始化缓存
        }

        #endregion

        #region Public Properties (Data Binding)

        /// <summary>插件实例（用于ExecuteAction）</summary>
        public IMonitorablePlugin Plugin => _plugin;

        /// <summary>插件ID</summary>
        public string PluginId => _plugin.PluginId;

        /// <summary>显示名称</summary>
        public string DisplayName => _plugin.DisplayName;

        /// <summary>插件类型文本</summary>
        public string TypeText => _plugin.Type switch
        {
            PluginType.DataTerminal => "Data",
            PluginType.RemoteIndicator => "Indicator",
            _ => "Unknown"
        };

        /// <summary>状态文本</summary>
        public string StatusText => _plugin.IsConnected ? "Connected" : "Disconnected";

        /// <summary>状态颜色</summary>
        public Brush StatusColor => _plugin.IsConnected
            ? new SolidColorBrush(Color.FromRgb(72, 160, 101))  // 🟢 绿色 #48A065
            : new SolidColorBrush(Color.FromRgb(202, 93, 93));   // 🔴 红色 #CA5D5D

        /// <summary>连接时长文本</summary>
        public string UptimeText
        {
            get
            {
                if (_plugin.ConnectedSince == null)
                    return "-";

                var uptime = DateTime.Now - _plugin.ConnectedSince.Value;
                if (uptime.TotalDays >= 1)
                    return $"{(int)uptime.TotalDays}d {uptime.Hours}h";
                if (uptime.TotalHours >= 1)
                    return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
                if (uptime.TotalMinutes >= 1)
                    return $"{(int)uptime.TotalMinutes}m";
                return $"{(int)uptime.TotalSeconds}s";
            }
        }

        /// <summary>最后活跃时间文本</summary>
        public string LastActiveText
        {
            get
            {
                if (_plugin.LastActiveTime == null)
                    return "-";

                var elapsed = DateTime.Now - _plugin.LastActiveTime.Value;
                if (elapsed.TotalSeconds < 60)
                    return "just now";
                if (elapsed.TotalMinutes < 60)
                    return $"{(int)elapsed.TotalMinutes}m ago";
                if (elapsed.TotalHours < 24)
                    return $"{(int)elapsed.TotalHours}h ago";
                return $"{(int)elapsed.TotalDays}d ago";
            }
        }

        /// <summary>发送消息数（格式化）</summary>
        public string SentText => FormatCount(_cachedStatistics.MessagesSent);

        /// <summary>接收消息数（格式化）</summary>
        public string ReceivedText => FormatCount(_cachedStatistics.MessagesReceived);

        /// <summary>错误数</summary>
        public string ErrorsText => _cachedStatistics.TotalErrors.ToString();

        /// <summary>错误数颜色</summary>
        public Brush ErrorsColor => _cachedStatistics.TotalErrors > 0
            ? new SolidColorBrush(Color.FromRgb(255, 165, 0))  // 🟡 橙色 (警告)
            : new SolidColorBrush(Colors.White);

        /// <summary>详细统计（Tooltip用）</summary>
        public string DetailedStats
        {
            get
            {
                var stats = _cachedStatistics;
                var lines = new System.Collections.Generic.List<string>
                {
                    $"Plugin ID: {_plugin.PluginId}",
                    $"Type: {_plugin.Type}",
                    $"Status: {StatusText}",
                    "",
                    "Statistics:",
                    $"  Messages Sent: {stats.MessagesSent}",
                    $"  Messages Received: {stats.MessagesReceived}",
                    $"  Send Failures: {stats.SendFailures}",
                    $"  Receive Failures: {stats.ReceiveFailures}"
                };

                // 根据插件类型添加特定统计
                if (_plugin.Type == PluginType.DataTerminal)
                {
                    lines.Add($"  Bars Pushed: {stats.BarsPushed}");
                    lines.Add($"  Bars Queried: {stats.BarsQueried}");
                }
                else if (_plugin.Type == PluginType.RemoteIndicator)
                {
                    lines.Add($"  Indicator Requests: {stats.IndicatorRequests}");
                    lines.Add($"  Elements Received: {stats.ElementsReceived}");
                }

                // 添加错误信息
                if (!string.IsNullOrEmpty(stats.LastError))
                {
                    lines.Add("");
                    lines.Add("Last Error:");
                    lines.Add($"  Time: {stats.LastErrorTime:HH:mm:ss}");
                    lines.Add($"  Message: {stats.LastError}");
                }

                return string.Join("\n", lines);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 刷新缓存数据（从插件获取最新统计）
        /// UI线程调用此方法更新显示
        /// </summary>
        public void Refresh()
        {
            _cachedStatistics = _plugin.GetStatistics();
            OnPropertyChanged(string.Empty); // 通知所有属性变化
        }

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// 格式化数字（K = 千，M = 百万）
        /// </summary>
        private string FormatCount(long count)
        {
            if (count >= 1_000_000)
                return $"{count / 1_000_000.0:F1}M";
            if (count >= 1_000)
                return $"{count / 1_000.0:F1}K";
            return count.ToString();
        }

        #endregion
    }
}
