using System;
using System.Collections.Generic;
using System.Linq;

namespace RemoteIndicator.ATAS.Monitoring
{
    /// <summary>
    /// 插件注册表 - 全局单例，线程安全
    ///
    /// 职责：
    /// 1. 管理所有已注册插件的生命周期
    /// 2. 提供线程安全的注册/注销操作
    /// 3. 触发注册/注销事件通知UI更新
    /// 4. 提供插件查询接口
    ///
    /// Thread-Safety: 所有操作使用lock保护
    /// </summary>
    public static class PluginRegistry
    {
        #region Private Fields

        /// <summary>插件字典（PluginId -> Plugin）</summary>
        private static readonly Dictionary<string, IMonitorablePlugin> _plugins
            = new Dictionary<string, IMonitorablePlugin>();

        /// <summary>线程锁</summary>
        private static readonly object _lock = new object();

        #endregion

        #region Events

        /// <summary>
        /// 插件注册事件
        /// 在UI线程中触发，可直接更新UI
        /// </summary>
        public static event Action<IMonitorablePlugin> PluginRegistered;

        /// <summary>
        /// 插件注销事件
        /// 在UI线程中触发，可直接更新UI
        /// </summary>
        public static event Action<string> PluginUnregistered;

        #endregion

        #region Public Methods

        /// <summary>
        /// 注册插件到全局注册表
        /// </summary>
        /// <param name="plugin">插件实例</param>
        /// <exception cref="ArgumentNullException">plugin为null</exception>
        /// <exception cref="InvalidOperationException">PluginId已存在</exception>
        public static void Register(IMonitorablePlugin plugin)
        {
            if (plugin == null)
                throw new ArgumentNullException(nameof(plugin));

            if (string.IsNullOrEmpty(plugin.PluginId))
                throw new ArgumentException("PluginId cannot be null or empty", nameof(plugin));

            bool isFirstPlugin = false;

            lock (_lock)
            {
                if (_plugins.ContainsKey(plugin.PluginId))
                {
                    throw new InvalidOperationException(
                        $"Plugin with ID '{plugin.PluginId}' is already registered. " +
                        $"Each plugin must have a unique ID."
                    );
                }

                // Check if this is the first plugin
                isFirstPlugin = _plugins.Count == 0;

                _plugins[plugin.PluginId] = plugin;
            }

            // 触发事件（在锁外，避免死锁）
            try
            {
                PluginRegistered?.Invoke(plugin);
            }
            catch (Exception ex)
            {
                // 防止事件处理器异常影响注册流程
                System.Diagnostics.Debug.WriteLine($"PluginRegistered event handler error: {ex}");
            }

            // Auto-show Control Panel when first plugin registers
            if (isFirstPlugin)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("First plugin registered - showing Control Panel");
                    UI.RemoteIndicatorControlPanel.Show();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to auto-show Control Panel: {ex.Message}");
                    // Don't throw - this shouldn't break plugin registration
                }
            }
        }

        /// <summary>
        /// 注销插件
        /// </summary>
        /// <param name="pluginId">插件ID</param>
        /// <returns>是否成功注销（false表示插件不存在）</returns>
        public static bool Unregister(string pluginId)
        {
            if (string.IsNullOrEmpty(pluginId))
                return false;

            bool removed;
            lock (_lock)
            {
                removed = _plugins.Remove(pluginId);
            }

            // 触发事件（在锁外）
            if (removed)
            {
                try
                {
                    PluginUnregistered?.Invoke(pluginId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"PluginUnregistered event handler error: {ex}");
                }
            }

            return removed;
        }

        /// <summary>
        /// 获取所有已注册插件（线程安全副本）
        /// </summary>
        /// <returns>插件列表（副本，可安全遍历）</returns>
        public static IReadOnlyList<IMonitorablePlugin> GetAll()
        {
            lock (_lock)
            {
                return _plugins.Values.ToList();
            }
        }

        /// <summary>
        /// 根据ID获取插件
        /// </summary>
        /// <param name="pluginId">插件ID</param>
        /// <returns>插件实例，不存在则返回null</returns>
        public static IMonitorablePlugin GetById(string pluginId)
        {
            if (string.IsNullOrEmpty(pluginId))
                return null;

            lock (_lock)
            {
                _plugins.TryGetValue(pluginId, out var plugin);
                return plugin;
            }
        }

        /// <summary>
        /// 获取已注册插件数量
        /// </summary>
        public static int Count
        {
            get
            {
                lock (_lock)
                {
                    return _plugins.Count;
                }
            }
        }

        /// <summary>
        /// 检查插件是否已注册
        /// </summary>
        /// <param name="pluginId">插件ID</param>
        /// <returns>是否存在</returns>
        public static bool Contains(string pluginId)
        {
            if (string.IsNullOrEmpty(pluginId))
                return false;

            lock (_lock)
            {
                return _plugins.ContainsKey(pluginId);
            }
        }

        /// <summary>
        /// 清空所有已注册插件（测试用）
        /// </summary>
        public static void Clear()
        {
            List<string> pluginIds;
            lock (_lock)
            {
                pluginIds = _plugins.Keys.ToList();
                _plugins.Clear();
            }

            // 触发注销事件
            foreach (var id in pluginIds)
            {
                try
                {
                    PluginUnregistered?.Invoke(id);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"PluginUnregistered event handler error: {ex}");
                }
            }
        }

        #endregion

        #region Statistics

        /// <summary>
        /// 获取注册表统计信息（调试用）
        /// </summary>
        public static string GetStatistics()
        {
            lock (_lock)
            {
                var dataTerminalCount = _plugins.Values.Count(p => p.Type == PluginType.DataTerminal);
                var indicatorCount = _plugins.Values.Count(p => p.Type == PluginType.RemoteIndicator);
                var connectedCount = _plugins.Values.Count(p => p.IsConnected);

                return $"Total: {_plugins.Count}, " +
                       $"DataTerminal: {dataTerminalCount}, " +
                       $"RemoteIndicator: {indicatorCount}, " +
                       $"Connected: {connectedCount}";
            }
        }

        #endregion
    }
}
