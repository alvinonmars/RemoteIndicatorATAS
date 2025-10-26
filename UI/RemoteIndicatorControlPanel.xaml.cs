using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using RemoteIndicator.ATAS.Monitoring;

namespace RemoteIndicator.ATAS.UI
{
    /// <summary>
    /// Remote Indicator 控制面板 - 单例WPF窗口
    ///
    /// 职责：
    /// 1. 展示所有注册插件的实时状态
    /// 2. 提供全局操作（刷新、重连、清空统计）
    /// 3. UI线程安全的状态更新
    /// 4. 定时自动刷新（1秒间隔）
    ///
    /// 单例模式：全局唯一实例，多次Show()调用激活到前台
    /// </summary>
    public partial class RemoteIndicatorControlPanel : Window, INotifyPropertyChanged
    {
        #region 单例模式

        private static RemoteIndicatorControlPanel _instance;
        private static readonly object _lockObject = new object();

        /// <summary>
        /// 显示控制面板（单例，UI线程安全）
        /// </summary>
        public static void Show()
        {
            // 确保在UI线程执行
            // ATAS may not have Application.Current, so check Dispatcher directly from existing window
            var dispatcher = _instance?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;

            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(Show);
                return;
            }

            lock (_lockObject)
            {
                try
                {
                    if (_instance == null || !_instance.IsLoaded)
                    {
                        _instance = new RemoteIndicatorControlPanel();
                        _instance.Closed += (s, e) =>
                        {
                            _instance = null;
                        };
                        // 明确调用基类Window的Show方法（避免与静态方法Show()冲突）
                        ((Window)_instance).Show();

                        System.Diagnostics.Debug.WriteLine("Remote Indicator Control Panel opened successfully");
                    }
                    else
                    {
                        // 窗口已存在，激活到前台
                        _instance.Activate();
                        _instance.Topmost = true;
                        _instance.Topmost = false;

                        System.Diagnostics.Debug.WriteLine("Remote Indicator Control Panel activated");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to show Control Panel: {ex.Message}");
                    throw;
                }
            }
        }

        #endregion

        #region Private Fields

        private ObservableCollection<PluginStatusViewModel> _pluginStatuses;
        private DispatcherTimer _refreshTimer;
        private DateTime _lastRefreshTime;

        #endregion

        #region Public Properties (Data Binding)

        /// <summary>插件状态列表（数据绑定）</summary>
        public ObservableCollection<PluginStatusViewModel> PluginStatuses
        {
            get => _pluginStatuses;
            set
            {
                _pluginStatuses = value;
                OnPropertyChanged(nameof(PluginStatuses));
            }
        }

        /// <summary>状态栏文本</summary>
        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = value;
                OnPropertyChanged(nameof(StatusText));
            }
        }

        #endregion

        #region Constructor

        private RemoteIndicatorControlPanel()
        {
            InitializeComponent();
            DataContext = this;

            _pluginStatuses = new ObservableCollection<PluginStatusViewModel>();
            _lastRefreshTime = DateTime.Now;

            // 订阅注册表事件
            PluginRegistry.PluginRegistered += OnPluginRegistered;
            PluginRegistry.PluginUnregistered += OnPluginUnregistered;

            // 加载已注册插件
            RefreshPluginList();

            // 启动定时刷新
            StartAutoRefresh();

            UpdateStatusText();
        }

        #endregion

        #region Plugin Event Handlers

        private void OnPluginRegistered(IMonitorablePlugin plugin)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var vm = new PluginStatusViewModel(plugin);
                    _pluginStatuses.Add(vm);

                    // 订阅插件状态变化
                    plugin.StatusChanged += OnPluginStatusChanged;

                    UpdateStatusText();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"OnPluginRegistered error: {ex}");
                }
            }));
        }

        private void OnPluginUnregistered(string pluginId)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var vm = _pluginStatuses.FirstOrDefault(p => p.PluginId == pluginId);
                    if (vm != null)
                    {
                        vm.Plugin.StatusChanged -= OnPluginStatusChanged;
                        _pluginStatuses.Remove(vm);
                    }

                    UpdateStatusText();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"OnPluginUnregistered error: {ex}");
                }
            }));
        }

        private void OnPluginStatusChanged(IMonitorablePlugin plugin, string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var vm = _pluginStatuses.FirstOrDefault(p => p.PluginId == plugin.PluginId);
                    vm?.Refresh();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"OnPluginStatusChanged error: {ex}");
                }
            }));
        }

        #endregion

        #region Auto Refresh

        private void StartAutoRefresh()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // 刷新所有插件状态
                foreach (var vm in _pluginStatuses)
                {
                    vm.Refresh();
                }

                _lastRefreshTime = DateTime.Now;
                UpdateStatusText();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshTimer_Tick error: {ex}");
            }
        }

        #endregion

        #region Button Click Handlers

        private void RefreshAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (var vm in _pluginStatuses)
                {
                    vm.Refresh();
                }

                StatusText = $"Refreshed at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                StatusText = $"Refresh error: {ex.Message}";
            }
        }

        private void ReconnectAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int count = 0;
                foreach (var vm in _pluginStatuses)
                {
                    vm.Plugin.ExecuteAction("Reconnect", null);
                    count++;
                }

                StatusText = $"Reconnect triggered for {count} plugin(s) at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                StatusText = $"Reconnect error: {ex.Message}";
            }
        }

        private void ClearStats_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Note: 清空统计需要插件支持"ClearStats" action
                int count = 0;
                foreach (var vm in _pluginStatuses)
                {
                    var result = vm.Plugin.ExecuteAction("ClearStats", null);
                    if (!result.StartsWith("Unknown"))
                        count++;
                }

                StatusText = $"Stats cleared for {count} plugin(s) at {DateTime.Now:HH:mm:ss}";

                // 刷新显示
                foreach (var vm in _pluginStatuses)
                {
                    vm.Refresh();
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Clear stats error: {ex.Message}";
            }
        }

        #endregion

        #region Helper Methods

        private void RefreshPluginList()
        {
            try
            {
                _pluginStatuses.Clear();

                var plugins = PluginRegistry.GetAll();
                foreach (var plugin in plugins)
                {
                    var vm = new PluginStatusViewModel(plugin);
                    _pluginStatuses.Add(vm);

                    // 订阅状态变化
                    plugin.StatusChanged += OnPluginStatusChanged;
                }

                UpdateStatusText();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshPluginList error: {ex}");
            }
        }

        private void UpdateStatusText()
        {
            var total = _pluginStatuses.Count;
            var connected = _pluginStatuses.Count(p => p.Plugin.IsConnected);

            StatusText = $"{total} plugin(s) registered | {connected} connected | Last update: {_lastRefreshTime:HH:mm:ss}";
        }

        #endregion

        #region Window Events

        protected override void OnClosing(CancelEventArgs e)
        {
            try
            {
                // 停止定时器
                _refreshTimer?.Stop();

                // 取消订阅事件
                PluginRegistry.PluginRegistered -= OnPluginRegistered;
                PluginRegistry.PluginUnregistered -= OnPluginUnregistered;

                foreach (var vm in _pluginStatuses)
                {
                    vm.Plugin.StatusChanged -= OnPluginStatusChanged;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnClosing error: {ex}");
            }

            base.OnClosing(e);
        }

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
