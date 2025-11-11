using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using ATAS.Indicators;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OFT.Rendering;
using OFT.Rendering.Context;
using OFT.Rendering.Control;
using OFT.Rendering.Tools;

namespace RemoteIndicatorATAS_standalone.Indicators
{
    /// <summary>
    /// 推格子助手 V2 - 双层矩形设计的模拟交易可视化工具
    /// 规划层（Planning Layer）：用户设置的计划
    /// 执行层（Execution Layer）：系统计算的实际结果
    /// </summary>
    [DisplayName("Paper Trading Helper V2")]
    [Category("Trading Tools")]
    public class PaperTradingHelperV2 : Indicator
    {
        #region 枚举定义

        /// <summary>
        /// 交互模式
        /// </summary>
        private enum InteractionMode
        {
            Normal,        // 正常模式
            WaitingLong,   // 等待Long进场点
            WaitingShort   // 等待Short进场点
        }

        /// <summary>
        /// 仓位方向
        /// </summary>
        private enum PositionDirection
        {
            Long,   // 多头
            Short   // 空头
        }

        /// <summary>
        /// 标签信息（包含绘制和交互信息）
        /// </summary>
        private class LabelInfo
        {
            public Rectangle Rect { get; set; }        // 标签矩形区域（用于绘制和点击检测）
            public string Text { get; set; }           // 标签文字
            public Color BackgroundColor { get; set; } // 背景色
            public DragTarget DragTarget { get; set; } // 对应的拖动目标类型
        }

        /// <summary>
        /// 拖动目标
        /// </summary>
        private enum DragTarget
        {
            None,
            TakeProfitLine,     // TP线
            StopLossLine,       // SL线
            LimitPriceLine,     // Limit Price线（拖动时TP/SL同步移动）
            LeftBorder,         // 左边界（LeftBarIndex）
            RightBorder,        // 右边界（RightBarIndex）
            WholeRectangle      // 整体矩形
        }

        /// <summary>
        /// 平仓原因
        /// </summary>
        private enum ExitReason
        {
            TakeProfit,   // 止盈
            StopLoss,     // 止损
            TimeExpired   // 时间到期
        }

        /// <summary>
        /// 按钮位置
        /// </summary>
        public enum ButtonPosition
        {
            TopLeft,      // 左上角
            TopRight,     // 右上角
            BottomLeft,   // 左下角
            BottomRight   // 右下角
        }

        #endregion

        #region 数据结构定义

        /// <summary>
        /// 模拟仓位对象（规划层 - Planning Layer）
        /// 用户设置的"计划"：边界、限价单、TP/SL
        /// </summary>
        private class PaperPosition
        {
            public Guid Id { get; set; }
            public PositionDirection Direction { get; set; }

            // ========== 矩形边界定义 ==========

            // 垂直边（时间维度）- 保证不变量：Left <= Right
            public int LeftBarIndex { get; set; }      // 矩形左边界（最早时间）
            public int RightBarIndex { get; set; }     // 矩形右边界（最晚时间）

            // 水平边（价格维度）
            public decimal TakeProfitPrice { get; set; }  // 止盈价（矩形的一条水平边）
            public decimal StopLossPrice { get; set; }    // 止损价（矩形的另一条水平边）

            // 内部分割线（限价单价格）
            public decimal LimitPrice { get; set; }       // 限价单价格（分割盈利/风险区）

            // 注意：矩形的上下边由 TP 和 SL 动态决定
            // TopPrice = max(TakeProfitPrice, StopLossPrice)
            // BottomPrice = min(TakeProfitPrice, StopLossPrice)

            // ========== 标签信息（绘制时填充）==========
            public LabelInfo LimitLabel { get; set; }  // Limit Price 标签
            public LabelInfo TPLabel { get; set; }      // Take Profit 标签
            public LabelInfo SLLabel { get; set; }      // Stop Loss 标签

            // ========== 拖动触发区域（绘制时填充）==========
            public Rectangle LeftBorderHitbox { get; set; }   // 左边界触发区域
            public Rectangle RightBorderHitbox { get; set; }  // 右边界触发区域
            public Rectangle BodyHitbox { get; set; }         // 矩形主体触发区域

            // 性能优化：缓存执行结果
            public PositionExecution CachedExecution { get; set; }
            public int LastCalculatedBar { get; set; } = -1;  // 最后一次计算时的bar
            public bool IsDirty { get; set; } = true;  // 是否需要重新计算
        }

        /// <summary>
        /// 仓位执行结果（执行层 - Execution Layer）
        /// 系统计算的"实际"：成交情况、开平仓时间/价格
        /// </summary>
        private class PositionExecution
        {
            public bool IsExecuted { get; set; }       // 限价单是否成交

            // 实际开平仓时间
            public int OpenBarIndex { get; set; }      // 开仓bar（限价单成交的bar）
            public int CloseBarIndex { get; set; }     // 平仓bar（触及TP/SL或时间到期）

            // 实际开平仓价格
            public decimal OpenPrice { get; set; }     // 开仓价 = LimitPrice
            public decimal ClosePrice { get; set; }    // 平仓价（TP/SL/时间到期收盘价）

            // 平仓原因
            public ExitReason Reason { get; set; }
        }

        /// <summary>
        /// 收益指标（计算结果）
        /// </summary>
        private class PositionMetrics
        {
            public decimal RiskTicks { get; set; }      // 风险（跳数）
            public decimal RewardTicks { get; set; }    // 收益（跳数）
            public decimal RRRatio { get; set; }        // 风报比
            public int HoldingBars { get; set; }        // 持仓K线数
            public decimal ActualPnL { get; set; }      // 实际盈亏（价格差）
        }

        /// <summary>
        /// 序列化的仓位数据（仅包含核心业务字段，用于持久化）
        /// 关键设计：使用时间戳而非Bar索引，保证数据稳定性
        /// </summary>
        private class SerializedPosition
        {
            public Guid Id { get; set; }
            public string Direction { get; set; }  // 枚举转字符串便于兼容性

            // ===== 时间边界（替代Bar索引）=====
            public DateTime LeftBarTime { get; set; }   // 左边界K线的开始时间
            public DateTime RightBarTime { get; set; }  // 右边界K线的开始时间

            // 价格信息
            public decimal LimitPrice { get; set; }
            public decimal TakeProfitPrice { get; set; }
            public decimal StopLossPrice { get; set; }
        }

        /// <summary>
        /// 完整的保存数据（包含元数据和版本信息）
        /// </summary>
        private class SaveData
        {
            // 版本控制（便于未来扩展和兼容性）
            public string Version { get; set; } = "1.0";

            // 保存时间戳
            public DateTime SaveTime { get; set; }

            // ===== 数据范围元数据（关键：用于文件命名和校验）=====
            public DateTime DataRangeStart { get; set; }  // GetCandle(0).Time
            public DateTime DataRangeEnd { get; set; }    // GetCandle(CurrentBar-1).Time
            public int TotalBars { get; set; }            // CurrentBar

            // 图表配置
            public string Symbol { get; set; }
            public string ChartType { get; set; }
            public string TimeFrame { get; set; }

            // 仓位列表
            public List<SerializedPosition> Positions { get; set; }

            // 扩展元数据
            public Dictionary<string, string> Metadata { get; set; }
        }

        #endregion

        #region 常量

        private const int MaxPositions = 1000;
        private const int ButtonWidth = 100;
        private const int ButtonHeight = 25;
        private const int ButtonSpacing = 10;
        private const int ButtonMargin = 10;

        // 标签常量
        private const int LabelWidth = 500;            // 标签固定宽度（像素）
        private const int LabelHeight = 24;            // 标签固定高度（像素）
        private const int BorderHitboxWidth = 10;      // 边界触发区域宽度（像素）

        // 价格拖动约束常量
        private const int MinPriceDistanceInTicks = 5;  // TP/SL与Limit的最小距离（tick数）
        private const int MouseHoverExpandPx = 20;      // 低可视模式判断的鼠标扩展区域（像素）

        #endregion

        #region 字段

        private List<PaperPosition> _positions;
        private InteractionMode _mode;
        private DragTarget _dragTarget;
        private PaperPosition _draggingPosition;

        // 拖动状态记录
        private Point _dragStartMousePos;
        private int _dragStartBar;           // 鼠标起始位置的bar
        private int _dragStartLeftBar;       // 拖动开始时的LeftBar
        private int _dragStartRightBar;      // 拖动开始时的RightBar
        private decimal _dragStartLimit;     // 拖动开始时的Limit
        private decimal _dragStartTP;        // 拖动开始时的TP
        private decimal _dragStartSL;        // 拖动开始时的SL

        // UI元素
        private Rectangle _longButton;
        private Rectangle _shortButton;
        private Rectangle _exportButton;
        private Rectangle _analyzeButton;
        private Rectangle _saveButton;
        private Rectangle _loadButton;
        private RenderFont _font;
        private RenderStringFormat _centerFormat;

        // 保存/加载相关
        private static readonly string SaveFolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ATAS_PaperTrading");

        // 颜色定义（按需求文档）
        private readonly Color _longColor = Color.FromArgb(255, 73, 160, 105);
        private readonly Color _longColorHover = Color.FromArgb(255, 90, 195, 143);
        private readonly Color _shortColor = Color.FromArgb(255, 202, 93, 93);
        private readonly Color _shortColorHover = Color.FromArgb(255, 240, 106, 106);

        // 规划层颜色（浅色，alpha=30）
        private readonly Color _profitZoneColor = Color.FromArgb(30, 0, 255, 0);
        private readonly Color _riskZoneColor = Color.FromArgb(30, 255, 0, 0);

        // 执行层颜色（深色，alpha=100，与TP/SL区域同色系）
        private readonly Color _execProfitColor = Color.FromArgb(100, 0, 255, 0);  // 深绿色（盈利）
        private readonly Color _execLossColor = Color.FromArgb(100, 255, 0, 0);    // 深红色（亏损）

        // 价格线颜色
        private readonly Color _limitLineColor = Color.White;
        private readonly Color _tpLineColor = Color.FromArgb(255, 50, 180, 80);
        private readonly Color _slLineColor = Color.FromArgb(255, 180, 50, 50);

        // 标签和圆点颜色
        private readonly Color _labelColor = Color.Gold;
        private readonly Color _tpDotColor = Color.LimeGreen;
        private readonly Color _slDotColor = Color.OrangeRed;
        private readonly Color _timeExpiredDotColor = Color.Gold;

        // Analyze按钮颜色
        private readonly Color _analyzeColor = Color.FromArgb(255, 218, 165, 32);      // 金色
        private readonly Color _analyzeColorHover = Color.FromArgb(255, 238, 185, 52); // 亮金色

        #endregion

        #region 配置参数

        private int _defaultStopTicks = 20;
        private int _defaultRectWidthPx = 300;

        [Display(GroupName = "开仓设置", Name = "默认止损(跳)", Order = 10)]
        public int DefaultStopTicks
        {
            get => _defaultStopTicks;
            set => _defaultStopTicks = Math.Max(1, value);
        }

        [Display(GroupName = "开仓设置", Name = "风报比", Order = 20)]
        public decimal RRRatio { get; set; } = 2.0m;

        [Display(GroupName = "开仓设置", Name = "初始矩形宽度(像素)", Order = 30)]
        public int DefaultRectWidthPx
        {
            get => _defaultRectWidthPx;
            set => _defaultRectWidthPx = Math.Max(50, value);
        }

        [Display(GroupName = "UI设置", Name = "按钮位置", Order = 40)]
        public ButtonPosition ButtonsPosition { get; set; } = ButtonPosition.BottomLeft;

        // 分析配置参数
        [Display(GroupName = "分析设置", Name = "每Tick价值(美元)", Order = 50)]
        public decimal TickValue { get; set; } = 10.0m;  // GC默认$10/tick

        [Display(GroupName = "分析设置", Name = "单边手续费(美元)", Order = 60)]
        public decimal CommissionPerSide { get; set; } = 2.2m;  // GC默认$2.2/side

        [Display(GroupName = "分析设置", Name = "初始资金(美元)", Order = 70)]
        public decimal InitialCapital { get; set; } = 5000.0m;  // 默认$5000

        #endregion

        #region 构造函数

        public PaperTradingHelperV2() : base(true)
        {
            _positions = new List<PaperPosition>();
            _mode = InteractionMode.Normal;
            _dragTarget = DragTarget.None;

            _centerFormat = new RenderStringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            _font = new RenderFont("Arial", 9f);

            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);

            var valueSeries = (ValueDataSeries)DataSeries[0];
            valueSeries.IsHidden = true;
            valueSeries.VisualType = VisualMode.Hide;
        }

        #endregion

        #region 核心方法 - 创建和计算

        /// <summary>
        /// 创建仓位（两步创建流程）
        /// </summary>
        private void CreatePosition(PositionDirection direction, decimal limitPrice, int leftBar)
        {
            if (_positions.Count >= MaxPositions)
            {
                AddAlert("警告", $"已达到最大仓位数量 ({MaxPositions})");
                return;
            }

            decimal tickSize = InstrumentInfo.TickSize;
            decimal stopDistance = DefaultStopTicks * tickSize;
            decimal targetDistance = DefaultStopTicks * RRRatio * tickSize;

            var position = new PaperPosition
            {
                Id = Guid.NewGuid(),
                Direction = direction,
                LeftBarIndex = leftBar,
                LimitPrice = limitPrice
            };

            // 根据像素宽度反推 RightBarIndex
            int leftX = ChartInfo.GetXByBar(leftBar);
            int targetRightX = leftX + DefaultRectWidthPx;
            position.RightBarIndex = GetBarByX(targetRightX);

            // 确保 RightBarIndex > LeftBarIndex（至少相差 1）
            if (position.RightBarIndex <= position.LeftBarIndex)
            {
                position.RightBarIndex = position.LeftBarIndex + 1;
            }

            // 根据方向计算TP/SL
            if (direction == PositionDirection.Long)
            {
                position.StopLossPrice = limitPrice - stopDistance;
                position.TakeProfitPrice = limitPrice + targetDistance;
            }
            else
            {
                position.StopLossPrice = limitPrice + stopDistance;
                position.TakeProfitPrice = limitPrice - targetDistance;
            }

            _positions.Add(position);

            // 计算并显示指标
            var execution = GetExecution(position);
            var metrics = CalculateMetrics(position, execution);
            AddAlert("创建仓位",
                $"{direction} @{FormatPrice(limitPrice)}, R:R={metrics.RRRatio:F1}, " +
                $"Executed={execution.IsExecuted}");

            RedrawChart();
        }

        /// <summary>
        /// 获取仓位执行结果（带缓存优化）
        /// </summary>
        private PositionExecution GetExecution(PaperPosition pos)
        {
            // 检查是否需要重新计算
            int currentLastBar = Math.Max(0, ChartInfo.PriceChartContainer.LastVisibleBarNumber - 1);

            if (!pos.IsDirty && pos.CachedExecution != null && pos.LastCalculatedBar == currentLastBar)
            {
                return pos.CachedExecution;  // 使用缓存
            }

            // 重新计算
            pos.CachedExecution = CalculateExecution(pos);
            pos.LastCalculatedBar = currentLastBar;
            pos.IsDirty = false;

            return pos.CachedExecution;
        }

        /// <summary>
        /// 计算仓位实际执行结果（核心算法）
        ///
        /// 算法步骤：
        /// 1. 在 [LeftBarIndex, RightBarIndex] 范围内扫描K线，判断限价单是否成交
        /// 2. 如果成交，从成交后下一根K线开始扫描，判断是否触及TP/SL
        /// 3. 如果未触及TP/SL，到RightBarIndex时按收盘价强制平仓
        ///
        /// 防止未来数据泄漏（关键）：
        /// - LastVisibleBarNumber 是最后可见bar，但可能未完成（数据不完整）
        /// - 必须使用 LastVisibleBarNumber - 1（最后一根完成的bar）
        /// - 确保只使用完整、确定的历史数据进行判断
        /// </summary>
        private PositionExecution CalculateExecution(PaperPosition pos)
        {
            // 边界检查：确保扫描范围在有效区间内，且不超过当前可见的最后一根完成的K线
            int startBar = Math.Max(0, pos.LeftBarIndex);

            // 防止未来数据泄漏：限制在 LastVisibleBarNumber - 1（最后一根完成的bar）
            // 重要：LastVisibleBarNumber 可能是未完成的bar，不应使用
            int lastVisibleBar = ChartInfo.PriceChartContainer.LastVisibleBarNumber;
            int lastCompletedBar = Math.Max(0, lastVisibleBar - 1);  // 防止负数
            int endBar = Math.Min(pos.RightBarIndex, Math.Min(Math.Max(0, CurrentBar - 1), lastCompletedBar));

            // 如果范围无效，返回未执行
            if (startBar > endBar || startBar >= CurrentBar || CurrentBar <= 0)
            {
                return new PositionExecution { IsExecuted = false };
            }

            // 步骤1：在 [startBar, endBar] 范围内寻找限价单成交点
            int? openBar = null;

            for (int bar = startBar; bar <= endBar; bar++)
            {
                var candle = GetCandle(bar);
                if (candle == null) continue;

                // 判断限价单是否成交：K线的价格区间[Low, High]包含LimitPrice
                // 这意味着价格"接触"到了限价单价格，无论是从上方还是下方
                bool filled = candle.Low <= pos.LimitPrice && pos.LimitPrice <= candle.High;

                if (filled)
                {
                    openBar = bar;
                    break;
                }
            }

            // 如果限价单未成交，返回未执行状态
            if (!openBar.HasValue)
            {
                return new PositionExecution
                {
                    IsExecuted = false
                };
            }

            // 步骤2：从成交后的下一根K线开始寻找平仓点
            for (int bar = openBar.Value + 1; bar <= endBar; bar++)
            {
                var candle = GetCandle(bar);
                if (candle == null) continue;

                if (pos.Direction == PositionDirection.Long)
                {
                    // 多头：先检查止损（Low），再检查止盈（High）
                    // 保守原则：同一根K线同时触及时，按止损处理
                    if (candle.Low <= pos.StopLossPrice)
                    {
                        return new PositionExecution
                        {
                            IsExecuted = true,
                            OpenBarIndex = openBar.Value,
                            CloseBarIndex = bar,
                            OpenPrice = pos.LimitPrice,
                            ClosePrice = pos.StopLossPrice,
                            Reason = ExitReason.StopLoss
                        };
                    }
                    if (candle.High >= pos.TakeProfitPrice)
                    {
                        return new PositionExecution
                        {
                            IsExecuted = true,
                            OpenBarIndex = openBar.Value,
                            CloseBarIndex = bar,
                            OpenPrice = pos.LimitPrice,
                            ClosePrice = pos.TakeProfitPrice,
                            Reason = ExitReason.TakeProfit
                        };
                    }
                }
                else // Short
                {
                    // 空头：先检查止损（High），再检查止盈（Low）
                    if (candle.High >= pos.StopLossPrice)
                    {
                        return new PositionExecution
                        {
                            IsExecuted = true,
                            OpenBarIndex = openBar.Value,
                            CloseBarIndex = bar,
                            OpenPrice = pos.LimitPrice,
                            ClosePrice = pos.StopLossPrice,
                            Reason = ExitReason.StopLoss
                        };
                    }
                    if (candle.Low <= pos.TakeProfitPrice)
                    {
                        return new PositionExecution
                        {
                            IsExecuted = true,
                            OpenBarIndex = openBar.Value,
                            CloseBarIndex = bar,
                            OpenPrice = pos.LimitPrice,
                            ClosePrice = pos.TakeProfitPrice,
                            Reason = ExitReason.TakeProfit
                        };
                    }
                }
            }

            // 步骤3：时间到期，未触及TP/SL，按endBar的收盘价平仓
            var closeCandle = GetCandle(endBar);

            return new PositionExecution
            {
                IsExecuted = true,
                OpenBarIndex = openBar.Value,
                CloseBarIndex = endBar,
                OpenPrice = pos.LimitPrice,
                ClosePrice = closeCandle?.Close ?? pos.LimitPrice,
                Reason = ExitReason.TimeExpired
            };
        }

        /// <summary>
        /// 计算收益指标
        /// </summary>
        private PositionMetrics CalculateMetrics(PaperPosition pos, PositionExecution execution)
        {
            decimal tickSize = InstrumentInfo.TickSize;

            // 防止除零错误
            if (tickSize <= 0)
            {
                tickSize = 0.01m;  // 使用默认值
            }

            // 规划指标（基于Limit/TP/SL）
            decimal riskDistance = Math.Abs(pos.LimitPrice - pos.StopLossPrice);
            decimal rewardDistance = Math.Abs(pos.TakeProfitPrice - pos.LimitPrice);

            decimal riskTicks = riskDistance / tickSize;
            decimal rewardTicks = rewardDistance / tickSize;
            decimal rrRatio = riskTicks > 0 ? rewardTicks / riskTicks : 0;

            // 执行指标
            int holdingBars = 0;
            decimal actualPnL = 0;

            if (execution.IsExecuted)
            {
                holdingBars = execution.CloseBarIndex - execution.OpenBarIndex;

                // 实际盈亏
                actualPnL = pos.Direction == PositionDirection.Long
                    ? (execution.ClosePrice - execution.OpenPrice)
                    : (execution.OpenPrice - execution.ClosePrice);
            }

            return new PositionMetrics
            {
                RiskTicks = riskTicks,
                RewardTicks = rewardTicks,
                RRRatio = rrRatio,
                HoldingBars = holdingBars,
                ActualPnL = actualPnL
            };
        }

        #endregion

        #region 核心方法 - 绘制

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (layout != DrawingLayouts.Final)
                return;

            // 绘制仓位（只绘制可见的）
            int firstVisible = ChartInfo.PriceChartContainer.FirstVisibleBarNumber;
            int lastVisible = ChartInfo.PriceChartContainer.LastVisibleBarNumber;

            foreach (var pos in _positions)
            {
                // 快速剔除：矩形完全在可见区域外
                if (pos.RightBarIndex < firstVisible || pos.LeftBarIndex > lastVisible)
                    continue;
                bool isLastPosition = pos == _positions[^1];
                DrawPosition(context, pos, isLastPosition);
            }

            // 绘制按钮
            DrawButtons(context);

            // 等待模式：绘制十字线
            if (_mode != InteractionMode.Normal)
            {
                DrawCrosshair(context);
            }
        }

        /// <summary>
        /// 绘制单个仓位（双层矩形设计 + 低可视模式）
        /// </summary>
        private void DrawPosition(RenderContext context, PaperPosition position, bool isLastPosition)
        {
            // === 1. 计算坐标 ===
            int leftX = ChartInfo.GetXByBar(position.LeftBarIndex);
            int rightX = ChartInfo.GetXByBar(position.RightBarIndex);
            int limitY = ChartInfo.PriceChartContainer.GetYByPrice(position.LimitPrice, false);
            int tpY = ChartInfo.PriceChartContainer.GetYByPrice(position.TakeProfitPrice, false);
            int slY = ChartInfo.PriceChartContainer.GetYByPrice(position.StopLossPrice, false);

            // 动态上下边 要把标签的高度考虑进去，如果标签超出矩形边界，则扩大矩形范围
            int topY = Math.Min(tpY, slY);
            int bottomY = Math.Max(tpY, slY);

            // === 2. 更新Hitbox（关键：无论渲染模式，都必须更新以支持交互）===
            UpdatePositionHitboxes(position, leftX, rightX, topY, bottomY, limitY, tpY, slY);

            // 计算执行结果（使用缓存优化）
            var execution = GetExecution(position);

            // === 3. 判断是否使用低可视模式 ===
            var mousePos = ChartInfo.MouseLocationInfo.LastPosition;
            // 构建大矩形区域, 要左右扩展一些，方便鼠标进入
            Rectangle bigRect = new Rectangle(leftX - MouseHoverExpandPx, topY - 2*LabelHeight, rightX - leftX + 2 * MouseHoverExpandPx, (bottomY + 2*LabelHeight) - (topY - 2*LabelHeight));

            //对于最新的交易仓位，始终使用完整模式
            bool isLowVisibility = !IsPointInRectangle(mousePos, bigRect) && !isLastPosition;

            // === 4. 根据模式选择绘制方式 ===
            if (isLowVisibility)
            {
                // 低可视模式：只绘制边框和对角线
                DrawLowVisibilityMode(context, position, execution, leftX, rightX, topY, bottomY);
            }
            else
            {
                // 完整模式：绘制所有内容
                DrawPlanningLayer(context, position, leftX, rightX, limitY, tpY, slY);

                if (execution.IsExecuted)
                {
                    DrawExecutionLayer(context, position, execution);
                }

                DrawBordersAndLines(context, position, leftX, rightX, limitY, tpY, slY, topY, bottomY);
                DrawLabels(context, position, execution, leftX, rightX, limitY, tpY, slY, topY);
            }
        }

        /// <summary>
        /// 绘制低可视模式（鼠标不在矩形内时使用，减少视觉干扰）
        /// 只显示：大矩形边框（淡化）+ 对角线（淡化）
        /// </summary>
        private void DrawLowVisibilityMode(RenderContext context, PaperPosition position,
            PositionExecution execution, int leftX, int rightX, int topY, int bottomY)
        {
            // 1. 绘制大矩形边框（淡化颜色 alpha=60）
            Rectangle rect = new Rectangle(leftX, topY, rightX - leftX, bottomY - topY);
            Color borderColor = position.Direction == PositionDirection.Long ? _longColor : _shortColor;
            Color fadedBorderColor = Color.FromArgb(30, borderColor.R, borderColor.G, borderColor.B);
            RenderPen borderPen = new RenderPen(fadedBorderColor, 1f);
            context.DrawRectangle(borderPen, rect);

            // 2. 如果已执行，绘制对角线（淡化颜色 alpha=30）
            if (execution.IsExecuted)
            {
                DrawExecutionLayer(context, position, execution);
                /*
                int openX = ChartInfo.GetXByBar(execution.OpenBarIndex);
                int closeX = ChartInfo.GetXByBar(execution.CloseBarIndex);
                int openY = ChartInfo.PriceChartContainer.GetYByPrice(execution.OpenPrice, false);
                int closeY = ChartInfo.PriceChartContainer.GetYByPrice(execution.ClosePrice, false);

                Color lineColor = position.Direction == PositionDirection.Long ? _longColor : _shortColor;
                Color fadedLineColor = Color.FromArgb(30, lineColor.R, lineColor.G, lineColor.B);
                RenderPen diagonalPen = new RenderPen(fadedLineColor, 2f, DashStyle.Dash);
                context.DrawLine(diagonalPen, openX, openY, closeX, closeY);
                */
            }
        }

        /// <summary>
        /// 绘制规划层（外层大矩形，浅色背景）
        /// </summary>
        private void DrawPlanningLayer(RenderContext context, PaperPosition position,
            int leftX, int rightX, int limitY, int tpY, int slY)
        {
            // 第1层：盈利区域
            if (position.Direction == PositionDirection.Long)
            {
                // Long: 盈利区在上方（Limit → TP）
                int profitTop = Math.Min(limitY, tpY);
                int profitBottom = Math.Max(limitY, tpY);
                Rectangle profitZone = new Rectangle(leftX, profitTop, rightX - leftX, profitBottom - profitTop);
                context.FillRectangle(_profitZoneColor, profitZone);
            }
            else
            {
                // Short: 盈利区在下方（TP → Limit）
                int profitTop = Math.Min(tpY, limitY);
                int profitBottom = Math.Max(tpY, limitY);
                Rectangle profitZone = new Rectangle(leftX, profitTop, rightX - leftX, profitBottom - profitTop);
                context.FillRectangle(_profitZoneColor, profitZone);
            }

            // 第2层：风险区域
            if (position.Direction == PositionDirection.Long)
            {
                // Long: 风险区在下方（SL → Limit）
                int riskTop = Math.Min(slY, limitY);
                int riskBottom = Math.Max(slY, limitY);
                Rectangle riskZone = new Rectangle(leftX, riskTop, rightX - leftX, riskBottom - riskTop);
                context.FillRectangle(_riskZoneColor, riskZone);
            }
            else
            {
                // Short: 风险区在上方（Limit → SL）
                int riskTop = Math.Min(limitY, slY);
                int riskBottom = Math.Max(limitY, slY);
                Rectangle riskZone = new Rectangle(leftX, riskTop, rightX - leftX, riskBottom - riskTop);
                context.FillRectangle(_riskZoneColor, riskZone);
            }
        }

        /// <summary>
        /// 绘制执行层（内层小矩形+对角线+圆点，深色前景）
        /// </summary>
        private void DrawExecutionLayer(RenderContext context, PaperPosition position, PositionExecution execution)
        {
            int openX = ChartInfo.GetXByBar(execution.OpenBarIndex);
            int closeX = ChartInfo.GetXByBar(execution.CloseBarIndex);
            int openY = ChartInfo.PriceChartContainer.GetYByPrice(execution.OpenPrice, false);
            int closeY = ChartInfo.PriceChartContainer.GetYByPrice(execution.ClosePrice, false);

            // 第3层：小矩形执行区域（深色，alpha=100）
            int execTop = Math.Min(openY, closeY);
            int execBottom = Math.Max(openY, closeY);
            int execWidth = Math.Max(3, closeX - openX);  // 最小3像素，确保可见
            Rectangle execZone = new Rectangle(openX, execTop, execWidth, execBottom - execTop);

            // 根据执行结果决定颜色（盈利=绿色，亏损=红色）
            Color execColor;
            if (execution.Reason == ExitReason.TakeProfit)
            {
                execColor = _execProfitColor;  // 止盈：绿色
            }
            else if (execution.Reason == ExitReason.StopLoss)
            {
                execColor = _execLossColor;    // 止损：红色
            }
            else  // TimeExpired
            {
                // 时间到期：根据实际盈亏决定颜色
                var metrics = CalculateMetrics(position, execution);
                execColor = metrics.ActualPnL >= 0 ? _execProfitColor : _execLossColor;
            }
            context.FillRectangle(execColor, execZone);

            // 第4层：对角线 Open → Close
            Color lineColor = position.Direction == PositionDirection.Long ? _longColor : _shortColor;
            RenderPen diagonalPen = new RenderPen(lineColor, 2f, DashStyle.Dash);
            context.DrawLine(diagonalPen, openX, openY, closeX, closeY);

            // 第5层：平仓点圆点
            int dotRadius = 5;
            Color dotColor = execution.Reason == ExitReason.TakeProfit
                ? _tpDotColor
                : execution.Reason == ExitReason.StopLoss
                    ? _slDotColor
                    : _timeExpiredDotColor;

            // 使用DrawEllipse API绘制圆点（使用适中的笔触宽度）
            Rectangle dotRect = new Rectangle(closeX - dotRadius, closeY - dotRadius, dotRadius * 2, dotRadius * 2);
            RenderPen dotPen = new RenderPen(dotColor, 3f);  // 3px笔触，足以突出显示
            context.DrawEllipse(dotPen, dotRect);
        }

        /// <summary>
        /// 绘制边框和价格线
        /// </summary>
        private void DrawBordersAndLines(RenderContext context, PaperPosition position,
            int leftX, int rightX, int limitY, int tpY, int slY, int topY, int bottomY)
        {
            // 第6层：大矩形边框
            Rectangle rect = new Rectangle(leftX, topY, rightX - leftX, bottomY - topY);
            Color borderColor = position.Direction == PositionDirection.Long ? _longColor : _shortColor;
            RenderPen borderPen = new RenderPen(borderColor, 1f);
            context.DrawRectangle(borderPen, rect);

            // 第7层：Limit Price线（白色实线2px）
            RenderPen limitPen = new RenderPen(_limitLineColor, 2f);
            context.DrawLine(limitPen, leftX, limitY, rightX, limitY);

            // 第8层：TP价格线（绿色虚线）
            RenderPen tpPen = new RenderPen(_tpLineColor, 1f, DashStyle.Dash);
            context.DrawLine(tpPen, leftX, tpY, rightX, tpY);

            // 第9层：SL价格线（红色虚线）
            RenderPen slPen = new RenderPen(_slLineColor, 1f, DashStyle.Dash);
            context.DrawLine(slPen, leftX, slY, rightX, slY);
        }

        /// <summary>
        /// 绘制标签并填充 hitbox
        /// </summary>
        private void DrawLabels(RenderContext context, PaperPosition position, PositionExecution execution,
            int leftX, int rightX, int limitY, int tpY, int slY, int topY)
        {
            var metrics = CalculateMetrics(position, execution);
            decimal tickSize = InstrumentInfo.TickSize;

            // 防止除零
            if (tickSize <= 0) tickSize = 0.01m;

            // === 1. 计算标签文字和颜色 ===

            // Limit 标签
            string limitText;
            Color limitBgColor;
            if (execution.IsExecuted)
            {
                decimal pnlTicks = metrics.ActualPnL / tickSize;
                limitText = $"{position.Direction}@{FormatPrice(position.LimitPrice)} Closed P&L:{pnlTicks:F1} RR:{metrics.RRRatio:F1}";
                limitBgColor = metrics.ActualPnL > 0
                    ? Color.FromArgb(200, 0, 180, 0)     // 盈利：绿色
                    : metrics.ActualPnL < 0
                        ? Color.FromArgb(200, 200, 50, 50)  // 亏损：红色
                        : Color.FromArgb(200, 100, 100, 100); // 平：灰色
            }
            else
            {
                limitText = $"{position.Direction}@{FormatPrice(position.LimitPrice)} Pending";
                limitBgColor = Color.FromArgb(200, 100, 100, 100);
            }

            // TP 标签
            decimal tpDist = Math.Abs(position.TakeProfitPrice - position.LimitPrice);
            decimal tpTicks = tpDist / tickSize;
            decimal tpPercentage = position.Direction == PositionDirection.Long
                ? (position.LimitPrice != 0 ? (position.TakeProfitPrice - position.LimitPrice) / position.LimitPrice * 100 : 0)
                : (position.LimitPrice != 0 ? (position.LimitPrice - position.TakeProfitPrice) / position.LimitPrice * 100 : 0);
            string tpText = $"Target@{FormatPrice(position.TakeProfitPrice)}: {tpTicks:F1}({tpPercentage:F3}%)";
            Color tpBgColor = Color.FromArgb(200, 0, 150, 0);

            // SL 标签
            decimal slDist = Math.Abs(position.LimitPrice - position.StopLossPrice);
            decimal slTicks = slDist / tickSize;
            decimal slPercentage = position.Direction == PositionDirection.Long
                ? (position.LimitPrice != 0 ? (position.LimitPrice - position.StopLossPrice) / position.LimitPrice * 100 : 0)
                : (position.LimitPrice != 0 ? (position.StopLossPrice - position.LimitPrice) / position.LimitPrice * 100 : 0);
            string slText = $"Stop@{FormatPrice(position.StopLossPrice)}: {slTicks:F1}({slPercentage:F3}%)";
            Color slBgColor = Color.FromArgb(200, 200, 50, 50);

            // === 2. 更新标签文字和颜色（Rect已在UpdatePositionHitboxes中设置）===

            // 更新 Limit 标签
            position.LimitLabel.Text = limitText;
            position.LimitLabel.BackgroundColor = limitBgColor;
            DrawSingleLabel(context, position.LimitLabel);

            // 更新 TP 标签
            position.TPLabel.Text = tpText;
            position.TPLabel.BackgroundColor = tpBgColor;
            DrawSingleLabel(context, position.TPLabel);

            // 更新 SL 标签
            position.SLLabel.Text = slText;
            position.SLLabel.BackgroundColor = slBgColor;
            DrawSingleLabel(context, position.SLLabel);
        }

        /// <summary>
        /// 更新仓位的Hitbox和标签Rect（用于交互检测）
        /// 关键：无论是否渲染，都必须更新Hitbox，确保低可视模式下也能交互
        /// </summary>
        private void UpdatePositionHitboxes(PaperPosition position,
            int leftX, int rightX, int topY, int bottomY,
            int limitY, int tpY, int slY)
        {
            // 计算标签X位置（与DrawLabels中的逻辑一致）
            int rectWidth = rightX - leftX;
            int labelX;

            if (rectWidth >= LabelWidth)
            {
                // 矩形够宽：居中对齐
                labelX = leftX + (rectWidth - LabelWidth) / 2;
            }
            else
            {
                // 矩形太窄：左对齐，保持小偏移
                labelX = leftX + 5;
            }

            // 更新标签Rect（用于交互检测，文字内容在DrawLabels中填充）
            if (position.LimitLabel == null) position.LimitLabel = new LabelInfo();
            position.LimitLabel.Rect = new Rectangle(labelX, limitY - 25, LabelWidth, LabelHeight);
            position.LimitLabel.DragTarget = DragTarget.LimitPriceLine;

            if (position.TPLabel == null) position.TPLabel = new LabelInfo();
            position.TPLabel.Rect = new Rectangle(labelX, tpY - 25, LabelWidth, LabelHeight);
            position.TPLabel.DragTarget = DragTarget.TakeProfitLine;

            if (position.SLLabel == null) position.SLLabel = new LabelInfo();
            position.SLLabel.Rect = new Rectangle(labelX, slY + 10, LabelWidth, LabelHeight);
            position.SLLabel.DragTarget = DragTarget.StopLossLine;

            // 更新边界Hitbox
            position.LeftBorderHitbox = new Rectangle(
                leftX - BorderHitboxWidth / 2,
                topY,
                BorderHitboxWidth,
                bottomY - topY
            );

            position.RightBorderHitbox = new Rectangle(
                rightX - BorderHitboxWidth / 2,
                topY,
                BorderHitboxWidth,
                bottomY - topY
            );

            // 更新矩形主体Hitbox
            position.BodyHitbox = new Rectangle(
                leftX + BorderHitboxWidth,
                topY,
                Math.Max(1, rightX - leftX - BorderHitboxWidth * 2),  // 防止负宽度
                bottomY - topY
            );
        }

        /// <summary>
        /// 绘制单个标签（辅助方法）
        /// </summary>
        private void DrawSingleLabel(RenderContext context, LabelInfo label)
        {
            if (label == null) return;

            // 绘制背景
            context.FillRectangle(label.BackgroundColor, label.Rect);

            // 绘制边框
            RenderPen borderPen = new RenderPen(Color.White, 1f);
            context.DrawRectangle(borderPen, label.Rect);

            // 绘制文字（居中，白色）
            context.DrawString(label.Text, _font, Color.White, label.Rect, _centerFormat);
        }


        /// <summary>
        /// 绘制按钮
        /// </summary>
        private void DrawButtons(RenderContext context)
        {
            // 根据配置确定按钮位置
            int x = ButtonMargin;
            int y = ButtonMargin;
            int chartWidth = ChartInfo.PriceChartContainer.Region.Width;
            int chartHeight = ChartInfo.PriceChartContainer.Region.Height;

            // 计算按钮数量（Long, Short, Save, Load, Analyze）
            int totalButtons = 5;
            int totalWidth = ButtonWidth * totalButtons + ButtonSpacing * (totalButtons - 1);

            switch (ButtonsPosition)
            {
                case ButtonPosition.TopLeft:
                    x = ButtonMargin;
                    y = ButtonMargin;
                    break;
                case ButtonPosition.TopRight:
                    x = chartWidth - totalWidth - ButtonMargin;
                    y = ButtonMargin;
                    break;
                case ButtonPosition.BottomLeft:
                    x = ButtonMargin;
                    y = chartHeight - ButtonHeight - ButtonMargin;
                    break;
                case ButtonPosition.BottomRight:
                    x = chartWidth - totalWidth - ButtonMargin;
                    y = chartHeight - ButtonHeight - ButtonMargin;
                    break;
            }

            // Long按钮
            _longButton = new Rectangle(x, y, ButtonWidth, ButtonHeight);
            Color longBtnColor = _mode == InteractionMode.WaitingLong ? _longColorHover :
                (IsMouseOver(_longButton) ? _longColorHover : _longColor);
            context.FillRectangle(longBtnColor, _longButton);
            context.DrawString("Long", _font, Color.White, _longButton, _centerFormat);

            // Short按钮
            _shortButton = new Rectangle(x + ButtonWidth + ButtonSpacing, y, ButtonWidth, ButtonHeight);
            Color shortBtnColor = _mode == InteractionMode.WaitingShort ? _shortColorHover :
                (IsMouseOver(_shortButton) ? _shortColorHover : _shortColor);
            context.FillRectangle(shortBtnColor, _shortButton);
            context.DrawString("Short", _font, Color.White, _shortButton, _centerFormat);

            // Save按钮（仅在有仓位时显示）
            if (_positions.Count > 0)
            {
                _saveButton = new Rectangle(x + (ButtonWidth + ButtonSpacing) * 2, y, ButtonWidth, ButtonHeight);
                Color saveColor = Color.FromArgb(255, 75, 139, 190);
                Color saveHover = Color.FromArgb(255, 95, 159, 210);
                Color saveBtnColor = IsMouseOver(_saveButton) ? saveHover : saveColor;
                context.FillRectangle(saveBtnColor, _saveButton);
                context.DrawString($"Save({_positions.Count})", _font, Color.White, _saveButton, _centerFormat);
            }

            // Load按钮（总是显示）
            _loadButton = new Rectangle(x + (ButtonWidth + ButtonSpacing) * 3, y, ButtonWidth, ButtonHeight);
            Color loadColor = Color.FromArgb(255, 139, 75, 190);
            Color loadHover = Color.FromArgb(255, 159, 95, 210);
            Color loadBtnColor = IsMouseOver(_loadButton) ? loadHover : loadColor;
            context.FillRectangle(loadBtnColor, _loadButton);
            context.DrawString("Load", _font, Color.White, _loadButton, _centerFormat);

            // Analyze按钮（仅在有已成交仓位时显示）
            int executedCount = _positions.Count(p => GetExecution(p).IsExecuted);
            if (executedCount > 0)
            {
                _analyzeButton = new Rectangle(x + (ButtonWidth + ButtonSpacing) * 4, y, ButtonWidth, ButtonHeight);
                Color analyzeBtnColor = IsMouseOver(_analyzeButton) ? _analyzeColorHover : _analyzeColor;
                context.FillRectangle(analyzeBtnColor, _analyzeButton);
                context.DrawString($"Analyze({executedCount})", _font, Color.White, _analyzeButton, _centerFormat);
            }
        }

        /// <summary>
        /// 绘制十字线（等待模式）
        /// </summary>
        private void DrawCrosshair(RenderContext context)
        {
            var mousePos = ChartInfo.MouseLocationInfo.LastPosition;

            RenderPen crosshairPen = new RenderPen(Color.Gray, 1f, DashStyle.Dot);

            // 垂直线
            context.DrawLine(crosshairPen, mousePos.X, 0, mousePos.X, ChartInfo.PriceChartContainer.Region.Height);

            // 水平线
            context.DrawLine(crosshairPen, 0, mousePos.Y, ChartInfo.PriceChartContainer.Region.Width, mousePos.Y);

            // 不显示价格标签，避免遮挡视线
        }

        #endregion

        #region 核心方法 - 鼠标事件

        public override bool ProcessMouseClick(RenderControlMouseEventArgs e)
        {
            // 右键删除
            if (e.Button == RenderControlMouseButtons.Right)
            {
                var position = FindPositionAtPoint(e.Location);
                if (position != null)
                {
                    _positions.Remove(position);
                    AddAlert("删除仓位", $"{position.Direction} @{FormatPrice(position.LimitPrice)}");
                    RedrawChart();
                    return true;
                }
            }

            // 左键点击按钮
            if (e.Button == RenderControlMouseButtons.Left)
            {
                if (IsPointInRectangle(e.Location, _longButton))
                {
                    _mode = _mode == InteractionMode.WaitingLong
                        ? InteractionMode.Normal
                        : InteractionMode.WaitingLong;
                    RedrawChart();
                    return true;
                }

                if (IsPointInRectangle(e.Location, _shortButton))
                {
                    _mode = _mode == InteractionMode.WaitingShort
                        ? InteractionMode.Normal
                        : InteractionMode.WaitingShort;
                    RedrawChart();
                    return true;
                }

                // Save按钮
                if (_positions.Count > 0 && IsPointInRectangle(e.Location, _saveButton))
                {
                    OnSaveButtonClick();
                    return true;
                }

                // Load按钮
                if (IsPointInRectangle(e.Location, _loadButton))
                {
                    OnLoadButtonClick();
                    return true;
                }

                // Analyze按钮
                int executedCount = _positions.Count(p => GetExecution(p).IsExecuted);
                if (executedCount > 0 && IsPointInRectangle(e.Location, _analyzeButton))
                {
                    PerformAnalysis();
                    return true;
                }

                // 等待模式：点击图表创建仓位
                if (_mode == InteractionMode.WaitingLong || _mode == InteractionMode.WaitingShort)
                {
                    int bar = GetBarByX(e.Location.X);
                    decimal price = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);

                    PositionDirection dir = _mode == InteractionMode.WaitingLong
                        ? PositionDirection.Long
                        : PositionDirection.Short;

                    CreatePosition(dir, price, bar);
                    _mode = InteractionMode.Normal;
                    return true;
                }
            }

            return false;
        }

        public override bool ProcessMouseDown(RenderControlMouseEventArgs e)
        {
            if (e.Button != RenderControlMouseButtons.Left)
                return false;

            // 逆序遍历仓位：后绘制的（视觉最顶层）优先检测
            for (int i = _positions.Count - 1; i >= 0; i--)
            {
                var pos = _positions[i];

                // 优先级1: 检测标签点击
                if (pos.TPLabel != null && IsPointInRectangle(e.Location, pos.TPLabel.Rect))
                {
                    _dragTarget = DragTarget.TakeProfitLine;
                    _draggingPosition = pos;
                    return true;
                }

                if (pos.SLLabel != null && IsPointInRectangle(e.Location, pos.SLLabel.Rect))
                {
                    _dragTarget = DragTarget.StopLossLine;
                    _draggingPosition = pos;
                    return true;
                }

                if (pos.LimitLabel != null && IsPointInRectangle(e.Location, pos.LimitLabel.Rect))
                {
                    _dragTarget = DragTarget.LimitPriceLine;
                    _draggingPosition = pos;
                    _dragStartLimit = pos.LimitPrice;
                    _dragStartTP = pos.TakeProfitPrice;
                    _dragStartSL = pos.StopLossPrice;
                    return true;
                }

                // 优先级2: 检测边界 hitbox
                if (IsPointInRectangle(e.Location, pos.LeftBorderHitbox))
                {
                    _dragTarget = DragTarget.LeftBorder;
                    _draggingPosition = pos;
                    _dragStartBar = GetBarByX(e.Location.X);
                    _dragStartLeftBar = pos.LeftBarIndex;
                    return true;
                }

                if (IsPointInRectangle(e.Location, pos.RightBorderHitbox))
                {
                    _dragTarget = DragTarget.RightBorder;
                    _draggingPosition = pos;
                    _dragStartBar = GetBarByX(e.Location.X);
                    _dragStartRightBar = pos.RightBarIndex;
                    return true;
                }

                // 优先级3: 检测矩形主体（整体拖动）
                if (IsPointInRectangle(e.Location, pos.BodyHitbox))
                {
                    _dragTarget = DragTarget.WholeRectangle;
                    _draggingPosition = pos;
                    _dragStartMousePos = e.Location;
                    _dragStartBar = GetBarByX(e.Location.X);
                    _dragStartLeftBar = pos.LeftBarIndex;
                    _dragStartRightBar = pos.RightBarIndex;
                    _dragStartLimit = pos.LimitPrice;
                    _dragStartTP = pos.TakeProfitPrice;
                    _dragStartSL = pos.StopLossPrice;
                    return true;
                }
            }

            return false;
        }

        public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
        {
            if (_mode != InteractionMode.Normal)
            {
                RedrawChart();
                return true;
            }

            if (_dragTarget == DragTarget.None || _draggingPosition == null)
                return false;

            switch (_dragTarget)
            {
                case DragTarget.TakeProfitLine:
                {
                    decimal newTP = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
                    decimal tickSize = InstrumentInfo.TickSize;
                    if (tickSize <= 0) tickSize = 0.01m;
                    decimal minDistance = MinPriceDistanceInTicks * tickSize;

                    // 约束：TP必须在盈利方向，且距离Limit至少minDistance
                    if (_draggingPosition.Direction == PositionDirection.Long)
                    {
                        // Long: TP必须 > Limit
                        newTP = Math.Max(newTP, _draggingPosition.LimitPrice + minDistance);
                    }
                    else
                    {
                        // Short: TP必须 < Limit
                        newTP = Math.Min(newTP, _draggingPosition.LimitPrice - minDistance);
                    }

                    _draggingPosition.TakeProfitPrice = newTP;
                    break;
                }

                case DragTarget.StopLossLine:
                {
                    decimal newSL = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
                    decimal tickSize = InstrumentInfo.TickSize;
                    if (tickSize <= 0) tickSize = 0.01m;
                    decimal minDistance = MinPriceDistanceInTicks * tickSize;

                    // 约束：SL必须在风险方向，且距离Limit至少minDistance
                    if (_draggingPosition.Direction == PositionDirection.Long)
                    {
                        // Long: SL必须 < Limit
                        newSL = Math.Min(newSL, _draggingPosition.LimitPrice - minDistance);
                    }
                    else
                    {
                        // Short: SL必须 > Limit
                        newSL = Math.Max(newSL, _draggingPosition.LimitPrice + minDistance);
                    }

                    _draggingPosition.StopLossPrice = newSL;
                    break;
                }

                case DragTarget.LimitPriceLine:
                {
                    // Limit/TP/SL保持相对距离同步移动
                    decimal newLimit = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
                    decimal delta = newLimit - _dragStartLimit;
                    _draggingPosition.LimitPrice = newLimit;
                    _draggingPosition.TakeProfitPrice = _dragStartTP + delta;
                    _draggingPosition.StopLossPrice = _dragStartSL + delta;
                    break;
                }


                case DragTarget.LeftBorder:
                {
                    // 使用相对移动，避免跳动
                    int currentMouseBar = GetBarByX(e.Location.X);
                    int barDelta = currentMouseBar - _dragStartBar;
                    int newLeftBar = _dragStartLeftBar + barDelta;

                    // 边界约束：确保在有效范围内
                    int maxBarIndex = CurrentBar > 0 ? CurrentBar - 1 : 0;
                    newLeftBar = Math.Clamp(newLeftBar, 0, maxBarIndex);

                    // 自动交换机制
                    if (newLeftBar > _draggingPosition.RightBarIndex)
                    {
                        // 拖过右边界，自动交换
                        _draggingPosition.LeftBarIndex = _draggingPosition.RightBarIndex;
                        _draggingPosition.RightBarIndex = newLeftBar;
                    }
                    else
                    {
                        _draggingPosition.LeftBarIndex = newLeftBar;
                    }
                    break;
                }

                case DragTarget.RightBorder:
                {
                    // 使用相对移动，避免跳动
                    int currentMouseBar = GetBarByX(e.Location.X);
                    int barDelta = currentMouseBar - _dragStartBar;
                    int newRightBar = _dragStartRightBar + barDelta;

                    // 边界约束：确保在有效范围内
                    int maxBarIndex = CurrentBar > 0 ? CurrentBar - 1 : 0;
                    newRightBar = Math.Clamp(newRightBar, 0, maxBarIndex);

                    // 自动交换机制
                    if (newRightBar < _draggingPosition.LeftBarIndex)
                    {
                        // 拖过左边界，自动交换
                        _draggingPosition.RightBarIndex = _draggingPosition.LeftBarIndex;
                        _draggingPosition.LeftBarIndex = newRightBar;
                    }
                    else
                    {
                        _draggingPosition.RightBarIndex = newRightBar;
                    }
                    break;
                }

                case DragTarget.WholeRectangle:
                {
                    // X方向：时间
                    int currentMouseBar = GetBarByX(e.Location.X);
                    int barDelta = currentMouseBar - _dragStartBar;

                    int newLeftBar = _dragStartLeftBar + barDelta;
                    int newRightBar = _dragStartRightBar + barDelta;

                    // 边界检查：确保在有效范围内
                    int barWidth = _dragStartRightBar - _dragStartLeftBar;
                    if (newLeftBar < 0)
                    {
                        newLeftBar = 0;
                        newRightBar = barWidth;
                    }
                    // 添加CurrentBar > 0检查，防止负数索引
                    if (CurrentBar > 0 && newRightBar >= CurrentBar)
                    {
                        newRightBar = CurrentBar - 1;
                        newLeftBar = newRightBar - barWidth;
                        if (newLeftBar < 0) newLeftBar = 0;
                    }

                    _draggingPosition.LeftBarIndex = Math.Max(0, newLeftBar);
                    _draggingPosition.RightBarIndex = Math.Max(0, newRightBar);

                    // Y方向：价格（无需边界检查，价格可以任意）
                    decimal mousePrice = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
                    decimal priceDelta = mousePrice - _dragStartLimit;

                    _draggingPosition.LimitPrice = _dragStartLimit + priceDelta;
                    _draggingPosition.TakeProfitPrice = _dragStartTP + priceDelta;
                    _draggingPosition.StopLossPrice = _dragStartSL + priceDelta;
                    break;
                }
            }

            RedrawChart();
            return true;
        }

        public override bool ProcessMouseUp(RenderControlMouseEventArgs e)
        {
            if (_dragTarget != DragTarget.None && _draggingPosition != null)
            {
                // 标记为需要重新计算（拖动结束）
                _draggingPosition.IsDirty = true;

                // 计算执行结果和指标
                var execution = GetExecution(_draggingPosition);
                var metrics = CalculateMetrics(_draggingPosition, execution);

                AddAlert("仓位更新",
                    $"R:R={metrics.RRRatio:F2}, " +
                    $"Risk={metrics.RiskTicks:F0} ticks, " +
                    $"Reward={metrics.RewardTicks:F0} ticks, " +
                    $"Executed={execution.IsExecuted}");

                // 重置拖动状态
                _dragTarget = DragTarget.None;
                _draggingPosition = null;

                RedrawChart();
                return true;
            }
            return false;
        }

        public override bool ProcessKeyDown(KeyEventArgs e)
        {
            // ESC：取消等待模式
            if (e.Key == Key.Escape)
            {
                if (_mode != InteractionMode.Normal)
                {
                    _mode = InteractionMode.Normal;
                    RedrawChart();
                    return true;
                }
            }

            // Delete：删除鼠标悬停的仓位
            if (e.Key == Key.Delete)
            {
                var mousePos = ChartInfo.MouseLocationInfo.LastPosition;
                var position = FindPositionAtPoint(mousePos);
                if (position != null)
                {
                    _positions.Remove(position);
                    AddAlert("删除仓位", $"{position.Direction} @{FormatPrice(position.LimitPrice)}");
                    RedrawChart();
                    return true;
                }
            }

            return base.ProcessKeyDown(e);
        }

        public override StdCursor GetCursor(RenderControlMouseEventArgs e)
        {
            // 检查按钮区域
            int executedCount = _positions.Count(p => GetExecution(p).IsExecuted);
            if (IsPointInRectangle(e.Location, _longButton) ||
                IsPointInRectangle(e.Location, _shortButton) ||
                (_positions.Count > 0 && IsPointInRectangle(e.Location, _saveButton)) ||
                IsPointInRectangle(e.Location, _loadButton) ||
                (executedCount > 0 && IsPointInRectangle(e.Location, _analyzeButton)))
                return StdCursor.Hand;

            // 逆序遍历：后绘制的（视觉最顶层）优先检测
            for (int i = _positions.Count - 1; i >= 0; i--)
            {
                var pos = _positions[i];

                // 优先级1: 标签区域 → 上下拖动光标
                if ((pos.TPLabel != null && IsPointInRectangle(e.Location, pos.TPLabel.Rect)) ||
                    (pos.SLLabel != null && IsPointInRectangle(e.Location, pos.SLLabel.Rect)) ||
                    (pos.LimitLabel != null && IsPointInRectangle(e.Location, pos.LimitLabel.Rect)))
                {
                    return StdCursor.SizeNS;
                }

                // 优先级2: 边界 hitbox → 左右拖动光标
                if (IsPointInRectangle(e.Location, pos.LeftBorderHitbox) ||
                    IsPointInRectangle(e.Location, pos.RightBorderHitbox))
                {
                    return StdCursor.SizeWE;
                }

                // 优先级3: 矩形主体 → 全方向拖动光标
                if (IsPointInRectangle(e.Location, pos.BodyHitbox))
                {
                    return StdCursor.SizeAll;
                }
            }

            return StdCursor.Arrow;
        }

        #endregion

        #region 核心方法 - 保存/加载

        /// <summary>
        /// 根据时间戳精确查找bar索引（二分查找 + 秒级精度）
        ///
        /// 设计原则：
        /// - 使用二分查找：O(log n)
        /// - 精确匹配：允许<1秒的误差（容忍DateTime序列化精度损失）
        /// - 找不到返回-1，不尝试"就近匹配"
        /// </summary>
        /// <param name="targetTime">目标时间戳</param>
        /// <returns>找到的bar索引，失败返回-1</returns>
        private int FindBarByTime(DateTime targetTime)
        {
            if (CurrentBar <= 0)
                return -1;

            int left = 0;
            int right = CurrentBar - 1;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                var candle = GetCandle(mid);

                if (candle == null)
                {
                    // K线获取失败，缩小搜索范围
                    right = mid - 1;
                    continue;
                }

                // 计算时间差（秒）
                double diffSeconds = Math.Abs((candle.Time - targetTime).TotalSeconds);

                // 精确匹配：秒级精度（容忍<1秒的误差）
                if (diffSeconds < 1.0)
                {
                    return mid;  // 找到
                }

                // 继续二分
                if (candle.Time < targetTime)
                {
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            // 未找到
            return -1;
        }

        /// <summary>
        /// 生成基础文件名（不含版本号）
        /// 格式：PaperPositions_{Symbol}_{ChartType}_{TimeFrame}_{StartTime}_to_{EndTime}
        /// </summary>
        private string GetBaseFileName(DateTime startTime, DateTime endTime)
        {
            string symbol = SafeFileName(InstrumentInfo?.Instrument ?? "Unknown");
            string chartType = SafeFileName(ChartInfo?.ChartType.ToString() ?? "Unknown");
            string timeFrame = SafeFileName(ChartInfo?.TimeFrame.ToString() ?? "Unknown");
            string start = startTime.ToString("yyyyMMdd_HHmmss");
            string end = endTime.ToString("yyyyMMdd_HHmmss");

            return $"PaperPositions_{symbol}_{chartType}_{timeFrame}_{start}_to_{end}";
        }

        /// <summary>
        /// 生成完整文件名（含版本号）
        /// </summary>
        private string GetVersionedFileName(string baseFileName, int version)
        {
            return $"{baseFileName}_v{version}.json";
        }

        /// <summary>
        /// 获取下一个可用的版本号
        /// </summary>
        private int GetNextVersionNumber(string baseFileName)
        {
            try
            {
                // 确保文件夹存在
                if (!Directory.Exists(SaveFolderPath))
                {
                    Directory.CreateDirectory(SaveFolderPath);
                    return 1;  // 首个版本
                }

                string pattern = baseFileName + "_v*.json";
                var files = Directory.GetFiles(SaveFolderPath, pattern);

                if (files.Length == 0)
                    return 1;  // 首个版本

                // 提取所有版本号
                int maxVersion = 0;
                foreach (var file in files)
                {
                    // 从文件名提取版本号：xxx_v3.json → 3
                    string name = Path.GetFileNameWithoutExtension(file);
                    int lastIndex = name.LastIndexOf("_v");
                    if (lastIndex >= 0)
                    {
                        string versionStr = name.Substring(lastIndex + 2);
                        if (int.TryParse(versionStr, out int version))
                        {
                            maxVersion = Math.Max(maxVersion, version);
                        }
                    }
                }

                return maxVersion + 1;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PaperTrading] GetNextVersionNumber错误: {ex}");
                return 1;
            }
        }

        /// <summary>
        /// 将当前仓位列表转换为可序列化的数据结构
        /// </summary>
        private SaveData ToSaveData(DateTime dataRangeStart, DateTime dataRangeEnd)
        {
            var data = new SaveData
            {
                Version = "1.0",
                SaveTime = DateTime.Now,
                DataRangeStart = dataRangeStart,
                DataRangeEnd = dataRangeEnd,
                TotalBars = CurrentBar,
                Symbol = InstrumentInfo?.Instrument ?? "Unknown",
                ChartType = ChartInfo?.ChartType.ToString() ?? "Unknown",
                TimeFrame = ChartInfo?.TimeFrame.ToString() ?? "Unknown",
                Positions = new List<SerializedPosition>(),
                Metadata = new Dictionary<string, string>
                {
                    { "IndicatorVersion", GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0.0" }
                }
            };

            // 直接转换，不做校验和过滤
            foreach (var pos in _positions)
            {
                var leftCandle = GetCandle(pos.LeftBarIndex);
                var rightCandle = GetCandle(pos.RightBarIndex);

                // 如果获取失败，说明有Bug，抛出异常
                if (leftCandle == null || rightCandle == null)
                {
                    throw new InvalidOperationException(
                        $"无法获取仓位时间戳：LeftBar={pos.LeftBarIndex}, RightBar={pos.RightBarIndex}, CurrentBar={CurrentBar}");
                }

                data.Positions.Add(new SerializedPosition
                {
                    Id = pos.Id,
                    Direction = pos.Direction.ToString(),
                    LeftBarTime = leftCandle.Time,
                    RightBarTime = rightCandle.Time,
                    LimitPrice = pos.LimitPrice,
                    TakeProfitPrice = pos.TakeProfitPrice,
                    StopLossPrice = pos.StopLossPrice
                });
            }

            return data;
        }

        /// <summary>
        /// 从保存数据恢复仓位列表
        /// </summary>
        private void FromSaveData(SaveData data)
        {
            if (data == null || data.Positions == null)
            {
                AddAlert("加载失败", "文件数据无效");
                return;
            }

            _positions.Clear();

            int successCount = 0;
            int failCount = 0;

            foreach (var serialized in data.Positions)
            {
                try
                {
                    // 时间戳 → Bar索引
                    int leftBar = FindBarByTime(serialized.LeftBarTime);
                    int rightBar = FindBarByTime(serialized.RightBarTime);

                    // 找不到就跳过（数据范围不匹配是预期的）
                    if (leftBar == -1 || rightBar == -1)
                    {
                        failCount++;
                        continue;
                    }

                    // 直接转换为PaperPosition
                    _positions.Add(new PaperPosition
                    {
                        Id = serialized.Id,
                        Direction = Enum.Parse<PositionDirection>(serialized.Direction),
                        LeftBarIndex = leftBar,
                        RightBarIndex = rightBar,
                        LimitPrice = serialized.LimitPrice,
                        TakeProfitPrice = serialized.TakeProfitPrice,
                        StopLossPrice = serialized.StopLossPrice,
                        IsDirty = true,
                        CachedExecution = null,
                        LastCalculatedBar = -1
                    });

                    successCount++;
                }
                catch (Exception ex)
                {
                    failCount++;
                    System.Diagnostics.Debug.WriteLine($"[PaperTrading] 加载仓位失败: {ex.Message}");
                }
            }

            // 反馈
            if (successCount > 0)
            {
                string msg = $"成功{successCount}笔";
                if (failCount > 0)
                    msg += $", 跳过{failCount}笔";
                AddAlert("加载完成", msg);
            }
            else if (failCount > 0)
            {
                AddAlert("加载失败", $"{failCount}笔仓位无法加载（数据范围不匹配？）");
            }
        }

        /// <summary>
        /// 保存仓位到JSON文件
        /// </summary>
        private void SavePositionsToFile(string filePath, DateTime dataRangeStart, DateTime dataRangeEnd)
        {
            try
            {
                // 确保文件夹存在
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 序列化
                var saveData = ToSaveData(dataRangeStart, dataRangeEnd);
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = System.Text.Json.JsonSerializer.Serialize(saveData, options);

                // 写入文件
                File.WriteAllText(filePath, json, Encoding.UTF8);

                System.Diagnostics.Debug.WriteLine($"[PaperTrading] JSON保存: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                throw new Exception($"JSON保存失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从文件加载仓位
        /// </summary>
        private void LoadPositionsFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    AddAlert("错误", "文件不存在");
                    return;
                }

                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var saveData = System.Text.Json.JsonSerializer.Deserialize<SaveData>(json, options);

                FromSaveData(saveData);
                RedrawChart();
            }
            catch (Exception ex)
            {
                AddAlert("加载失败", $"错误: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[PaperTrading] 加载失败: {ex}");
                _positions.Clear();
            }
        }

        /// <summary>
        /// Save按钮点击处理（保存JSON + 导出CSV）
        /// </summary>
        private void OnSaveButtonClick()
        {
            if (_positions.Count == 0)
            {
                AddAlert("无数据", "没有仓位需要保存");
                return;
            }

            var firstCandle = GetCandle(0);
            var lastCandle = GetCandle(CurrentBar - 1);

            if (firstCandle == null || lastCandle == null)
            {
                AddAlert("错误", "无法获取数据范围");
                return;
            }

            try
            {
                // 1. 保存JSON
                string baseFileName = GetBaseFileName(firstCandle.Time, lastCandle.Time);
                int nextVersion = GetNextVersionNumber(baseFileName);
                string jsonFileName = GetVersionedFileName(baseFileName, nextVersion);
                string jsonFilePath = Path.Combine(SaveFolderPath, jsonFileName);

                SavePositionsToFile(jsonFilePath, firstCandle.Time, lastCandle.Time);

                // 2. 导出CSV
                ExportToCsv();

                AddAlert("保存成功", $"{jsonFileName}, 共{_positions.Count}笔");
            }
            catch (Exception ex)
            {
                AddAlert("保存失败", $"错误: {ex.Message}");
            }
        }

        /// <summary>
        /// Load按钮点击处理（打开文件对话框）
        /// </summary>
        private void OnLoadButtonClick()
        {
            try
            {
                if (_positions.Count > 0)
                {
                    AddAlert("加载提示", $"当前有{_positions.Count}笔仓位，加载会替换");
                }

                using (var dialog = new System.Windows.Forms.OpenFileDialog())
                {
                    dialog.InitialDirectory = SaveFolderPath;
                    dialog.Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*";
                    dialog.FilterIndex = 1;
                    dialog.Title = "选择要加载的推格子记录";

                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        LoadPositionsFromFile(dialog.FileName);
                    }
                }
            }
            catch (Exception ex)
            {
                AddAlert("错误", $"打开文件对话框失败: {ex.Message}");
            }
        }

        #endregion

        #region 核心方法 - CSV导出

        /// <summary>
        /// 导出到CSV（含执行结果和完整元数据）
        /// </summary>
        private void ExportToCsv()
        {
            string safeSymbol = SafeFileName(InstrumentInfo?.Instrument ?? "Unknown");
            string fileName = $"PaperTrades_{safeSymbol}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), fileName);

            try
            {
                var lines = new List<string>();

                // 获取元数据
                string symbol = InstrumentInfo?.Instrument ?? "Unknown";
                string chartType = ChartInfo?.ChartType.ToString() ?? "Unknown";
                string timeFrame = ChartInfo?.TimeFrame.ToString() ?? "Unknown";

                // CSV头（合理排序：时间戳 -> 元数据 -> 交易信息 -> 执行状态 -> 计算指标）
                lines.Add("OpenBarOpenTime,OpenBarCloseTime,CloseBarCloseTime,Symbol,ChartType,TimeFrame,Direction,LeftBar,RightBar,OpenBar,CloseBar,LimitPrice,OpenPrice,ClosePrice,TP,SL,IsExecuted,ExitReason,RiskTicks,RewardTicks,RR,ActualPnL,HoldingBars");

                // 数据行
                foreach (var pos in _positions)
                {
                    var execution = GetExecution(pos);
                    var metrics = CalculateMetrics(pos, execution);

                    // Bar索引
                    string openBar = execution.IsExecuted ? execution.OpenBarIndex.ToString() : "N/A";
                    string closeBar = execution.IsExecuted ? execution.CloseBarIndex.ToString() : "N/A";

                    // 时间戳（完整的K线时间信息）
                    string openBarOpenTime = "N/A";
                    string openBarCloseTime = "N/A";
                    string closeBarCloseTime = "N/A";

                    if (execution.IsExecuted)
                    {
                        // Open Bar的开始时间
                        var openCandle = GetCandle(execution.OpenBarIndex);
                        openBarOpenTime = openCandle?.Time.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";

                        // Open Bar的结束时间（下一根K线的开始时间）
                        var openBarNextCandle = GetCandle(execution.OpenBarIndex + 1);
                        openBarCloseTime = openBarNextCandle?.Time.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";

                        // Close Bar的结束时间（下一根K线的开始时间）
                        var closeBarNextCandle = GetCandle(execution.CloseBarIndex + 1);
                        closeBarCloseTime = closeBarNextCandle?.Time.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";
                    }

                    // 价格
                    string openPrice = execution.IsExecuted ? FormatPrice(execution.OpenPrice) : "N/A";
                    string closePrice = execution.IsExecuted ? FormatPrice(execution.ClosePrice) : "N/A";
                    string exitReason = execution.IsExecuted ? execution.Reason.ToString() : "NotExecuted";

                    // 按合理顺序排列数据
                    lines.Add(string.Join(",",
                        // 时间戳组
                        openBarOpenTime,
                        openBarCloseTime,
                        closeBarCloseTime,
                        // 元数据组
                        symbol,
                        chartType,
                        timeFrame,
                        // 交易信息组
                        pos.Direction,
                        pos.LeftBarIndex,
                        pos.RightBarIndex,
                        openBar,
                        closeBar,
                        FormatPrice(pos.LimitPrice),
                        openPrice,
                        closePrice,
                        FormatPrice(pos.TakeProfitPrice),
                        FormatPrice(pos.StopLossPrice),
                        // 执行状态组
                        execution.IsExecuted,
                        exitReason,
                        // 计算指标组
                        metrics.RiskTicks.ToString("F1"),
                        metrics.RewardTicks.ToString("F1"),
                        metrics.RRRatio.ToString("F2"),
                        metrics.ActualPnL.ToString("F2"),
                        metrics.HoldingBars
                    ));
                }

                File.WriteAllLines(filePath, lines, Encoding.UTF8);
                AddAlert("导出成功", $"{filePath}, 共{_positions.Count}笔");
            }
            catch (Exception ex)
            {
                AddAlert("错误", $"导出CSV失败: {ex.Message}");
            }
        }

        #endregion

        #region 核心方法 - 交易分析

        /// <summary>
        /// 执行交易分析并生成报告
        /// </summary>
        private void PerformAnalysis()
        {
            try
            {
                // 1. 收集所有已成交的交易
                var tradeRecords = new List<TradingAnalyzer.TradeRecord>();

                foreach (var pos in _positions)
                {
                    var execution = GetExecution(pos);
                    if (!execution.IsExecuted) continue;

                    // 获取时间信息
                    var openCandle = GetCandle(execution.OpenBarIndex);
                    var closeCandle = GetCandle(execution.CloseBarIndex);

                    if (openCandle == null || closeCandle == null) continue;

                    // 计算盈亏（价格差）
                    var metrics = CalculateMetrics(pos, execution);

                    // 计算MAE/MFE（扫描持仓期间的所有K线）
                    decimal mae = 0m;  // Maximum Adverse Excursion
                    decimal mfe = 0m;  // Maximum Favorable Excursion

                    for (int bar = execution.OpenBarIndex + 1; bar <= execution.CloseBarIndex; bar++)
                    {
                        var candle = GetCandle(bar);
                        if (candle == null) continue;

                        if (pos.Direction == PositionDirection.Long)
                        {
                            // 多头：MAE = 最大浮亏（Low - OpenPrice的最小值）
                            decimal unrealizedPnL_Low = candle.Low - execution.OpenPrice;
                            mae = Math.Min(mae, unrealizedPnL_Low);

                            // MFE = 最大浮盈（High - OpenPrice的最大值）
                            decimal unrealizedPnL_High = candle.High - execution.OpenPrice;
                            mfe = Math.Max(mfe, unrealizedPnL_High);
                        }
                        else  // Short
                        {
                            // 空头：MAE = 最大浮亏（OpenPrice - High的最小值）
                            decimal unrealizedPnL_High = execution.OpenPrice - candle.High;
                            mae = Math.Min(mae, unrealizedPnL_High);

                            // MFE = 最大浮盈（OpenPrice - Low的最大值）
                            decimal unrealizedPnL_Low = execution.OpenPrice - candle.Low;
                            mfe = Math.Max(mfe, unrealizedPnL_Low);
                        }
                    }

                    // 滑点计算（对于限价单，滑点通常为0，但可扩展为市价单）
                    decimal slippage = execution.OpenPrice - pos.LimitPrice;

                    // 交易时段判断（根据开仓时间）
                    string tradingSession = DetermineTradingSession(openCandle.Time);

                    var record = new TradingAnalyzer.TradeRecord
                    {
                        OpenTime = openCandle.Time,
                        CloseTime = closeCandle.Time,
                        Direction = pos.Direction.ToString(),
                        ActualPnL = metrics.ActualPnL,
                        TickSize = InstrumentInfo.TickSize,
                        HoldingBars = metrics.HoldingBars,
                        ExitReason = execution.Reason.ToString(),
                        // 价格信息
                        OpenPrice = execution.OpenPrice,
                        ClosePrice = execution.ClosePrice,
                        TakeProfitPrice = pos.TakeProfitPrice,
                        StopLossPrice = pos.StopLossPrice,
                        // MAE/MFE
                        MAE = mae,
                        MFE = mfe,
                        // 滑点
                        Slippage = slippage,
                        // 交易时段
                        TradingSession = tradingSession
                    };

                    tradeRecords.Add(record);
                }

                if (tradeRecords.Count == 0)
                {
                    AddAlert("分析失败", "没有已成交的交易记录");
                    return;
                }

                // 2. 配置分析参数
                string rawSymbol = InstrumentInfo?.Instrument ?? "Unknown";
                string safeSymbol = SafeFileName(rawSymbol);

                var config = new TradingAnalyzer.AnalysisConfig
                {
                    TickValue = TickValue,
                    CommissionPerSide = CommissionPerSide,
                    InitialCapital = InitialCapital,
                    Symbol = rawSymbol  // 保存原始名称用于显示
                };

                // 3. 执行分析
                var analyzer = new TradingAnalyzer(config);
                var allMetrics = analyzer.Analyze(tradeRecords);
                var directionMetrics = analyzer.AnalyzeByDirection(tradeRecords);
                var sessionMetrics = analyzer.AnalyzeBySession(tradeRecords);

                // 计算热力图数据
                var entryHoldingHeatmap = analyzer.CalculateEntryHoldingHeatmap(tradeRecords);
                var sessionDayHeatmap = analyzer.CalculateSessionDayHeatmap(tradeRecords);

                // 计算新增的4个热图
                var dateHourHeatmap = analyzer.CalculateDateHourHeatmap(tradeRecords);
                var weekdayHourHeatmap = analyzer.CalculateWeekdayHourHeatmap(tradeRecords);
                var monthHourHeatmap = analyzer.CalculateMonthHourHeatmap(tradeRecords);
                var qualityHeatmap = analyzer.CalculateQualityHeatmap(tradeRecords);

                // 4. 生成控制台报告
                string consoleReport = analyzer.GenerateConsoleReport(allMetrics, directionMetrics, sessionMetrics);

                // 输出到ATAS日志
                AddAlert("分析完成", $"共分析{tradeRecords.Count}笔交易");
                System.Diagnostics.Debug.WriteLine(consoleReport);

                // 5. 导出分析CSV（使用安全文件名）
                string csvFileName = $"PaperTradingAnalysis_{safeSymbol}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string csvPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), csvFileName);
                analyzer.ExportAnalysisCSV(allMetrics, csvPath);

                // 6. 生成HTML报告（使用安全文件名，通过TradingReportGenerator）
                string htmlFileName = $"PaperTradingAnalysis_{safeSymbol}_{DateTime.Now:yyyyMMdd_HHmmss}.html";
                string htmlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), htmlFileName);
                var reportGenerator = new TradingReportGenerator();
                reportGenerator.GenerateHtmlReport(htmlPath, config, allMetrics, directionMetrics, sessionMetrics, tradeRecords,
                    entryHoldingHeatmap, sessionDayHeatmap, dateHourHeatmap, weekdayHourHeatmap, monthHourHeatmap, qualityHeatmap);

                // 7. 自动在浏览器中打开HTML报告
                try
                {
                    OpenInBrowser(htmlPath);
                    AddAlert("报告已生成并打开", $"CSV: {csvPath}\nHTML: {htmlPath}");
                }
                catch (Exception browserEx)
                {
                    // 如果打开浏览器失败，至少报告已生成
                    AddAlert("报告已生成", $"CSV: {csvPath}\nHTML: {htmlPath}\n(浏览器自动打开失败: {browserEx.Message})");
                }
            }
            catch (Exception ex)
            {
                AddAlert("错误", $"分析失败: {ex.Message}");
            }
        }

        #endregion

        #region 辅助方法

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
            sb.AppendLine("                hovertemplate: '<b>Entry:</b> %{x}<br><b>Holding:</b> %{y}<br><b>Avg P&L:</b> $%{z:.2f}<br>%{text}<extra></extra>',");
            sb.AppendLine("                colorscale: [[0, '#d73027'], [0.5, '#ffffbf'], [1, '#1a9850']],");
            sb.AppendLine("                zmid: 0,");
            sb.AppendLine("                colorbar: { title: { text: 'Avg P&L ($)', side: 'right' } },");
            sb.AppendLine("                texttemplate: '%{text}',");
            sb.AppendLine("                textfont: { size: 10, color: '#000' },");
            sb.AppendLine("                showscale: true");
            sb.AppendLine("            }];");

            sb.AppendLine("            var layout = {");
            sb.AppendLine("                xaxis: { title: 'Entry Time (UTC)', side: 'bottom' },");
            sb.AppendLine("                yaxis: { title: 'Holding Duration', autorange: 'reversed' },");
            sb.AppendLine("                margin: { l: 100, r: 50, t: 20, b: 100 },");
            sb.AppendLine("                paper_bgcolor: '#fafafa',");
            sb.AppendLine("                plot_bgcolor: '#fafafa'");
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
            sb.AppendLine("                hovertemplate: '<b>Day:</b> %{x}<br><b>Session:</b> %{y}<br><b>Avg P&L:</b> $%{z:.2f}<br>%{text}<extra></extra>',");
            sb.AppendLine("                colorscale: [[0, '#d73027'], [0.5, '#ffffbf'], [1, '#1a9850']],");
            sb.AppendLine("                zmid: 0,");
            sb.AppendLine("                colorbar: { title: { text: 'Avg P&L ($)', side: 'right' } },");
            sb.AppendLine("                texttemplate: '%{text}',");
            sb.AppendLine("                textfont: { size: 12, color: '#000' },");
            sb.AppendLine("                showscale: true");
            sb.AppendLine("            }];");

            sb.AppendLine("            var layout = {");
            sb.AppendLine("                xaxis: { title: 'Day of Week', side: 'bottom' },");
            sb.AppendLine("                yaxis: { title: 'Trading Session (UTC)', autorange: 'reversed' },");
            sb.AppendLine("                margin: { l: 150, r: 50, t: 20, b: 80 },");
            sb.AppendLine("                paper_bgcolor: '#fafafa',");
            sb.AppendLine("                plot_bgcolor: '#fafafa'");
            sb.AppendLine("            };");

            sb.AppendLine("            Plotly.newPlot('sessionDayHeatmap', data, layout, { responsive: true });");
            sb.AppendLine("        }");
            sb.AppendLine("    </script>");

            return sb.ToString();
        }

        /// <summary>
        /// 安全化文件名（移除非法字符）
        /// </summary>
        private string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Unknown";

            // 移除所有非法文件名字符
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            // 移除路径分隔符（额外的安全措施）
            name = name.Replace('/', '_').Replace('\\', '_');

            // 限制长度
            if (name.Length > 100)
                name = name.Substring(0, 100);

            return name;
        }

        /// <summary>
        /// HTML转义（防止XSS）
        /// </summary>
        private string HtmlEncode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return System.Net.WebUtility.HtmlEncode(text);
        }

        /// <summary>
        /// 根据TickSize计算价格精度（小数位数）
        /// </summary>
        private int GetPricePrecision()
        {
            decimal tickSize = InstrumentInfo.TickSize;

            // 如果tickSize >= 1，不需要小数
            if (tickSize >= 1)
                return 0;

            // 计算tickSize的小数位数
            string tickStr = tickSize.ToString("0.####################", System.Globalization.CultureInfo.InvariantCulture);
            int decimalPos = tickStr.IndexOf('.');

            if (decimalPos == -1)
                return 0;

            return tickStr.Length - decimalPos - 1;
        }

        /// <summary>
        /// 格式化价格（基于ticksize精度）
        /// </summary>
        private string FormatPrice(decimal price)
        {
            int precision = GetPricePrecision();
            return price.ToString($"F{precision}", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 根据X坐标获取Bar索引（使用反向验证确保精度）
        /// </summary>
        private int GetBarByX(int x)
        {
            int firstBar = ChartInfo.PriceChartContainer.FirstVisibleBarNumber;
            int firstX = ChartInfo.GetXByBar(firstBar);
            int distance = x - firstX;
            decimal barWidth = ChartInfo.PriceChartContainer.BarsWidth;
            //BarSpacing
            decimal barSpacing = ChartInfo.PriceChartContainer.BarSpacing;

            int barOffset = (int)Math.Round(distance / (barWidth + barSpacing));
            int barIndex = firstBar;
            int preBarIndex = firstBar;
            int postBarIndex = firstBar;

            barIndex = firstBar + barOffset;
            preBarIndex = barIndex - 1;
            postBarIndex = barIndex + 1;
            postBarIndex = Math.Min(postBarIndex, CurrentBar - 1);
            var barX = ChartInfo.GetXByBar(barIndex);
            var preX = ChartInfo.GetXByBar(preBarIndex);
            var postX = ChartInfo.GetXByBar(postBarIndex);

            if (Math.Abs(barX - x) <= Math.Abs(preX - x) && Math.Abs(barX - x) <= Math.Abs(postX - x))
            {
                return barIndex;
            }
            else if (Math.Abs(preX - x) < Math.Abs(barX - x))
            {
                return preBarIndex;
            }
            else
            {
                return postBarIndex;
            }
        }

        /// <summary>
        /// 判断点是否在矩形内
        /// </summary>
        private bool IsPointInRectangle(Point point, Rectangle rect)
        {
            return point.X >= rect.X && point.X <= rect.X + rect.Width &&
                   point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;
        }

        /// <summary>
        /// 判断鼠标是否悬停在矩形上
        /// </summary>
        private bool IsMouseOver(Rectangle rect)
        {
            var mousePos = ChartInfo.MouseLocationInfo.LastPosition;
            return IsPointInRectangle(mousePos, rect);
        }

        /// <summary>
        /// 在默认浏览器中打开文件
        /// </summary>
        private void OpenInBrowser(string filePath)
        {
            try
            {
                // 跨平台兼容的方式打开浏览器
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true  // 关键：使用操作系统的默认处理程序
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                // 如果上面的方法失败，尝试备用方案
                // Windows备用方案
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd",
                        Arguments = $"/c start \"\" \"{filePath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                // macOS备用方案
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                {
                    System.Diagnostics.Process.Start("open", filePath);
                }
                // Linux备用方案
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                {
                    System.Diagnostics.Process.Start("xdg-open", filePath);
                }
                else
                {
                    throw;  // 未知平台，抛出异常
                }
            }
        }

        /// <summary>
        /// 判断交易时段（根据时间分类）
        /// </summary>
        private string DetermineTradingSession(DateTime time)
        {
            var timeOfDay = time.TimeOfDay;
            double totalMinutes = timeOfDay.TotalMinutes;

            // 以美股为例（可根据具体市场调整）
            // 9:30-10:30: Opening
            // 10:30-15:00: Midday
            // 15:00-16:00: Closing
            if (totalMinutes >= 570 && totalMinutes < 630)  // 9:30-10:30
                return "Opening";
            else if (totalMinutes >= 630 && totalMinutes < 900)  // 10:30-15:00
                return "Midday";
            else if (totalMinutes >= 900 && totalMinutes < 960)  // 15:00-16:00
                return "Closing";
            else
                return "Extended";  // 盘前/盘后
        }

        /// <summary>
        /// 查找点击位置的仓位（逆序遍历，返回视觉最顶层的）
        /// </summary>
        private PaperPosition FindPositionAtPoint(Point point)
        {
            // 逆序遍历：后绘制的（视觉最顶层）优先检测
            for (int i = _positions.Count - 1; i >= 0; i--)
            {
                var pos = _positions[i];
                int leftX = ChartInfo.GetXByBar(pos.LeftBarIndex);
                int rightX = ChartInfo.GetXByBar(pos.RightBarIndex);
                int tpY = ChartInfo.PriceChartContainer.GetYByPrice(pos.TakeProfitPrice, false);
                int slY = ChartInfo.PriceChartContainer.GetYByPrice(pos.StopLossPrice, false);
                int topY = Math.Min(tpY, slY);
                int bottomY = Math.Max(tpY, slY);

                Rectangle rect = new Rectangle(leftX, topY, rightX - leftX, bottomY - topY);
                if (IsPointInRectangle(point, rect))
                {
                    return pos;
                }
            }
            return null;
        }

        /// <summary>
        /// 生成页面标题（带Symbol）
        /// </summary>
        private string GeneratePageTitleHtml(string symbol)
        {
            string safeSymbol = HtmlEncode(symbol);
            return $"<title>Paper Trading Analysis - {safeSymbol}</title>";
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

        #endregion

        #region 生命周期

        protected override void OnCalculate(int bar, decimal value)
        {
            // 基础计算
            // 当新K线到来时，标记所有仓位需要重新计算
            if (bar == CurrentBar - 1)  // 新K线
            {
                foreach (var pos in _positions)
                {
                    pos.IsDirty = true;
                }
            }
        }

        protected override void OnDispose()
        {
            // 自动保存（如果有数据）
            if (_positions.Count > 0)
            {
                try
                {
                    var firstCandle = GetCandle(0);
                    var lastCandle = GetCandle(CurrentBar - 1);

                    if (firstCandle != null && lastCandle != null)
                    {
                        string baseFileName = GetBaseFileName(firstCandle.Time, lastCandle.Time);
                        int nextVersion = GetNextVersionNumber(baseFileName);
                        string jsonFileName = GetVersionedFileName(baseFileName, nextVersion);
                        string jsonFilePath = Path.Combine(SaveFolderPath, jsonFileName);

                        SavePositionsToFile(jsonFilePath, firstCandle.Time, lastCandle.Time);

                        System.Diagnostics.Debug.WriteLine(
                            $"[PaperTrading] OnDispose自动保存: {jsonFileName}, {_positions.Count}笔");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PaperTrading] OnDispose保存失败: {ex}");
                    // 不抛出异常，不影响指标卸载
                }
            }

            // 释放GDI资源
            //_font?.Dispose();
            //_centerFormat?.Dispose();

            base.OnDispose();
        }

        #endregion
    }
}
