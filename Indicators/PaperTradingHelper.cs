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
    /// 推格子助手 - 模拟交易可视化工具
    /// </summary>
    [DisplayName("Paper Trading Helper")]
    [Category("Trading Tools")]
    public class PaperTradingHelper : Indicator
    {
        #region 嵌套类型定义

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
        /// 拖动目标
        /// </summary>
        private enum DragTarget
        {
            None,
            TakeProfitLine,
            StopLossLine,
            EntryLine,
            LeftBorder,
            RightBorder,
            WholeRectangle
        }

        /// <summary>
        /// 模拟仓位对象
        /// </summary>
        private class PaperPosition
        {
            public Guid Id { get; set; }
            public PositionDirection Direction { get; set; }
            public int OpenBarIndex { get; set; }
            public int CloseBarIndex { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal TakeProfit { get; set; }
            public decimal StopLoss { get; set; }
        }

        /// <summary>
        /// 收益指标
        /// </summary>
        private class PositionMetrics
        {
            public decimal RiskTicks { get; set; }
            public decimal RewardTicks { get; set; }
            public decimal RRRatio { get; set; }
            public int HoldingBars { get; set; }
        }

        #endregion

        #region 常量

        private const int MaxPositions = 1000;
        private const int LineTolerance = 5; // 拖动检测容差(px)
        private const int ButtonWidth = 100;
        private const int ButtonHeight = 25;
        private const int ButtonSpacing = 5;

        #endregion

        #region 字段

        private List<PaperPosition> _positions;
        private InteractionMode _mode;
        private DragTarget _dragTarget;
        private PaperPosition _draggingPosition;

        // 拖动状态记录
        private Point _dragStartMousePos;
        private int _dragStartBar;        // 缓存鼠标起始位置的bar（优化）
        private int _dragStartOpenBar;
        private decimal _dragStartEntry;
        private decimal _dragStartTP;
        private decimal _dragStartSL;

        // UI元素
        private Rectangle _longButton;
        private Rectangle _shortButton;
        private Rectangle _exportButton;
        private RenderFont _font;
        private RenderStringFormat _centerFormat;

        // 颜色定义
        private readonly Color _longColor = Color.FromArgb(255, 73, 160, 105);
        private readonly Color _longColorHover = Color.FromArgb(255, 90, 195, 143);
        private readonly Color _shortColor = Color.FromArgb(255, 202, 93, 93);
        private readonly Color _shortColorHover = Color.FromArgb(255, 240, 106, 106);
        private readonly Color _entryLineColor = Color.White;
        private readonly Color _tpLineColor = Color.FromArgb(255, 50, 180, 80);
        private readonly Color _slLineColor = Color.FromArgb(255, 180, 50, 50);
        private readonly Color _profitZoneColor = Color.FromArgb(30, 0, 255, 0);
        private readonly Color _riskZoneColor = Color.FromArgb(30, 255, 0, 0);
        private readonly Color _labelColor = Color.Gold;

        #endregion

        #region 配置参数

        private int _defaultStopTicks = 20;
        private int _defaultBarWidth = 5;

        [Display(GroupName = "开仓设置", Name = "默认止损(跳)", Order = 10)]
        public int DefaultStopTicks
        {
            get => _defaultStopTicks;
            set => _defaultStopTicks = Math.Max(1, value); // 最小值1
        }

        [Display(GroupName = "开仓设置", Name = "风报比", Order = 20)]
        public decimal RRRatio { get; set; } = 2.0m;

        [Display(GroupName = "开仓设置", Name = "初始矩形宽度(K线数)", Order = 30)]
        public int DefaultBarWidth
        {
            get => _defaultBarWidth;
            set => _defaultBarWidth = Math.Max(1, value); // 最小值1
        }

        #endregion

        #region 构造函数

        public PaperTradingHelper() : base(true)
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
        /// 创建仓位
        /// </summary>
        private void CreatePosition(PositionDirection direction, decimal entry, int openBar)
        {
            if (_positions.Count >= MaxPositions)
            {
                AddAlert("警告", $"已达到最大仓位数量 ({MaxPositions})，无法创建新仓位");
                return;
            }

            var position = new PaperPosition
            {
                Id = Guid.NewGuid(),
                Direction = direction,
                OpenBarIndex = openBar,
                CloseBarIndex = openBar + DefaultBarWidth,
                EntryPrice = entry,
                TakeProfit = CalculateTP(direction, entry),
                StopLoss = CalculateSL(direction, entry)
            };

            _positions.Add(position);

            // 创建后立即计算指标
            var metrics = CalculateMetrics(position);
            AddAlert("创建仓位", $"{direction} @{entry:F2}, R:R={metrics.RRRatio:F2}, Hold={metrics.HoldingBars}");

            RedrawChart();
        }

        /// <summary>
        /// 计算止损价格
        /// </summary>
        private decimal CalculateSL(PositionDirection direction, decimal entry)
        {
            decimal tickSize = InstrumentInfo.TickSize;
            decimal stopDistance = DefaultStopTicks * tickSize;

            if (direction == PositionDirection.Long)
                return entry - stopDistance;
            else
                return entry + stopDistance;
        }

        /// <summary>
        /// 计算目标价格
        /// </summary>
        private decimal CalculateTP(PositionDirection direction, decimal entry)
        {
            decimal tickSize = InstrumentInfo.TickSize;
            decimal targetDistance = DefaultStopTicks * RRRatio * tickSize;

            if (direction == PositionDirection.Long)
                return entry + targetDistance;
            else
                return entry - targetDistance;
        }

        /// <summary>
        /// 计算收益指标
        /// </summary>
        private PositionMetrics CalculateMetrics(PaperPosition pos)
        {
            decimal tickSize = InstrumentInfo.TickSize;

            // 风险和收益距离
            decimal riskDistance = Math.Abs(pos.EntryPrice - pos.StopLoss);
            decimal rewardDistance = Math.Abs(pos.TakeProfit - pos.EntryPrice);

            // 转换为跳数
            decimal riskTicks = riskDistance / tickSize;
            decimal rewardTicks = rewardDistance / tickSize;

            // 风报比
            decimal rrRatio = riskTicks > 0 ? rewardTicks / riskTicks : 0;

            // 持仓K线数
            int holdingBars = pos.CloseBarIndex - pos.OpenBarIndex;

            return new PositionMetrics
            {
                RiskTicks = riskTicks,
                RewardTicks = rewardTicks,
                RRRatio = rrRatio,
                HoldingBars = holdingBars
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
                if (pos.CloseBarIndex < firstVisible || pos.OpenBarIndex > lastVisible)
                    continue;

                DrawPosition(context, pos);
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
        /// 绘制单个仓位
        /// </summary>
        private void DrawPosition(RenderContext context, PaperPosition position)
        {
            // 计算坐标（优化：先计算各线坐标，再根据方向设置topY/bottomY）
            int leftX = ChartInfo.GetXByBar(position.OpenBarIndex);
            int rightX = ChartInfo.GetXByBar(position.CloseBarIndex);
            int entryY = ChartInfo.PriceChartContainer.GetYByPrice(position.EntryPrice, false);
            int tpY = ChartInfo.PriceChartContainer.GetYByPrice(position.TakeProfit, false);
            int slY = ChartInfo.PriceChartContainer.GetYByPrice(position.StopLoss, false);

            // 根据方向设置矩形边界（复用tpY和slY）
            int topY = position.Direction == PositionDirection.Long ? tpY : slY;
            int bottomY = position.Direction == PositionDirection.Long ? slY : tpY;

            Rectangle rect = new Rectangle(leftX, topY, rightX - leftX, bottomY - topY);

            // 1. 区域填充（分两个区域）
            // 盈利区域：Entry到TP
            int profitTop = Math.Min(entryY, tpY);
            int profitBottom = Math.Max(entryY, tpY);
            Rectangle profitZone = new Rectangle(leftX, profitTop, rightX - leftX, profitBottom - profitTop);
            context.FillRectangle(_profitZoneColor, profitZone);

            // 风险区域：Entry到SL
            int riskTop = Math.Min(entryY, slY);
            int riskBottom = Math.Max(entryY, slY);
            Rectangle riskZone = new Rectangle(leftX, riskTop, rightX - leftX, riskBottom - riskTop);
            context.FillRectangle(_riskZoneColor, riskZone);

            // 2. 矩形边框
            Color borderColor = position.Direction == PositionDirection.Long ? _longColor : _shortColor;
            RenderPen borderPen = new RenderPen(borderColor, 1f);
            context.DrawRectangle(borderPen, rect);

            // 3. Entry价格线（白色实线2px）
            RenderPen entryPen = new RenderPen(_entryLineColor, 2f);
            context.DrawLine(entryPen, leftX, entryY, rightX, entryY);
            string entryLabel = $"{position.Direction.ToString().ToUpper()} @{position.EntryPrice:F2}";
            context.DrawString(entryLabel, _font, _entryLineColor, leftX + 5, entryY - 15);

            // 4. TP价格线（绿色虚线）
            RenderPen tpPen = new RenderPen(_tpLineColor, 1f, DashStyle.Dash);
            context.DrawLine(tpPen, leftX, tpY, rightX, tpY);
            string tpLabel = $"TP @{position.TakeProfit:F2}";
            context.DrawString(tpLabel, _font, Color.LightGreen, leftX + 5, tpY - 15);

            // 5. SL价格线（红色虚线）
            RenderPen slPen = new RenderPen(_slLineColor, 1f, DashStyle.Dash);
            context.DrawLine(slPen, leftX, slY, rightX, slY);
            string slLabel = $"SL @{position.StopLoss:F2}";
            context.DrawString(slLabel, _font, Color.LightCoral, leftX + 5, slY + 5);

            // 6. R:R标签（右上角）
            var metrics = CalculateMetrics(position);
            string rrLabel = $"1:{metrics.RRRatio:F1}  {metrics.HoldingBars} bars";
            context.DrawString(rrLabel, _font, _labelColor, rightX - 80, topY + 5);
        }

        /// <summary>
        /// 绘制按钮
        /// </summary>
        private void DrawButtons(RenderContext context)
        {
            int x = 3;
            int y = 3;

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

            // Export按钮（仅在有仓位时显示）
            if (_positions.Count > 0)
            {
                _exportButton = new Rectangle(x + (ButtonWidth + ButtonSpacing) * 2, y, ButtonWidth, ButtonHeight);
                Color exportColor = Color.FromArgb(255, 75, 139, 190);
                Color exportHover = Color.FromArgb(255, 95, 159, 210);
                Color exportBtnColor = IsMouseOver(_exportButton) ? exportHover : exportColor;
                context.FillRectangle(exportBtnColor, _exportButton);
                context.DrawString($"Export({_positions.Count})", _font, Color.White, _exportButton, _centerFormat);
            }
        }

        /// <summary>
        /// 绘制十字线（等待模式）
        /// </summary>
        private void DrawCrosshair(RenderContext context)
        {
            var mousePos = ChartInfo.MouseLocationInfo.LastPosition;

            // 获取当前价格
            decimal price = ChartInfo.PriceChartContainer.GetPriceByY(mousePos.Y);
            int bar = GetBarByX(mousePos.X);

            // 绘制十字线
            RenderPen crosshairPen = new RenderPen(Color.Gray, 1f, DashStyle.Dot);

            // 垂直线
            context.DrawLine(crosshairPen, mousePos.X, 0, mousePos.X, ChartInfo.PriceChartContainer.Region.Height);

            // 水平线
            context.DrawLine(crosshairPen, 0, mousePos.Y, ChartInfo.PriceChartContainer.Region.Width, mousePos.Y);

            // 显示价格提示
            string priceLabel = $"{price:F2}";
            context.DrawString(priceLabel, _font, Color.Yellow, mousePos.X + 10, mousePos.Y - 20);
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
                    AddAlert("删除仓位", $"{position.Direction} @{position.EntryPrice:F2}");
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

                // Export按钮
                if (_positions.Count > 0 && IsPointInRectangle(e.Location, _exportButton))
                {
                    ExportToCsv();
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

            // 遍历仓位，检测拖动目标
            foreach (var pos in _positions)
            {
                int tpY = ChartInfo.PriceChartContainer.GetYByPrice(pos.TakeProfit, false);
                int slY = ChartInfo.PriceChartContainer.GetYByPrice(pos.StopLoss, false);
                int entryY = ChartInfo.PriceChartContainer.GetYByPrice(pos.EntryPrice, false);
                int leftX = ChartInfo.GetXByBar(pos.OpenBarIndex);
                int rightX = ChartInfo.GetXByBar(pos.CloseBarIndex);

                // 优先级1-3：水平线（价格）
                if (IsNearLine(e.Location.Y, tpY, LineTolerance) &&
                    e.Location.X >= leftX && e.Location.X <= rightX)
                {
                    _dragTarget = DragTarget.TakeProfitLine;
                    _draggingPosition = pos;
                    return true;
                }

                if (IsNearLine(e.Location.Y, slY, LineTolerance) &&
                    e.Location.X >= leftX && e.Location.X <= rightX)
                {
                    _dragTarget = DragTarget.StopLossLine;
                    _draggingPosition = pos;
                    return true;
                }

                if (IsNearLine(e.Location.Y, entryY, LineTolerance) &&
                    e.Location.X >= leftX && e.Location.X <= rightX)
                {
                    _dragTarget = DragTarget.EntryLine;
                    _draggingPosition = pos;
                    return true;
                }

                // 优先级4-5：垂直边界（时间）
                int topY = ChartInfo.PriceChartContainer.GetYByPrice(
                    pos.Direction == PositionDirection.Long ? pos.TakeProfit : pos.StopLoss, false);
                int bottomY = ChartInfo.PriceChartContainer.GetYByPrice(
                    pos.Direction == PositionDirection.Long ? pos.StopLoss : pos.TakeProfit, false);

                if (IsNearLine(e.Location.X, leftX, LineTolerance) &&
                    e.Location.Y >= topY && e.Location.Y <= bottomY)
                {
                    _dragTarget = DragTarget.LeftBorder;
                    _draggingPosition = pos;
                    return true;
                }

                if (IsNearLine(e.Location.X, rightX, LineTolerance) &&
                    e.Location.Y >= topY && e.Location.Y <= bottomY)
                {
                    _dragTarget = DragTarget.RightBorder;
                    _draggingPosition = pos;
                    return true;
                }

                // 优先级6：矩形内部
                Rectangle rect = new Rectangle(leftX, topY, rightX - leftX, bottomY - topY);
                if (IsPointInRectangle(e.Location, rect))
                {
                    _dragTarget = DragTarget.WholeRectangle;
                    _draggingPosition = pos;
                    _dragStartMousePos = e.Location;
                    _dragStartBar = GetBarByX(e.Location.X);  // 缓存起始bar
                    _dragStartOpenBar = pos.OpenBarIndex;
                    _dragStartEntry = pos.EntryPrice;
                    _dragStartTP = pos.TakeProfit;
                    _dragStartSL = pos.StopLoss;
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
                    _draggingPosition.TakeProfit = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
                    break;

                case DragTarget.StopLossLine:
                    _draggingPosition.StopLoss = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
                    break;

                case DragTarget.EntryLine:
                    decimal newEntry = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
                    decimal delta = newEntry - _draggingPosition.EntryPrice;
                    _draggingPosition.EntryPrice = newEntry;
                    _draggingPosition.TakeProfit += delta;
                    _draggingPosition.StopLoss += delta;
                    break;

                case DragTarget.LeftBorder:
                    int newOpenBar = GetBarByX(e.Location.X);
                    // 边界检查：至少保留1根K线宽度
                    if (newOpenBar >= _draggingPosition.CloseBarIndex)
                        newOpenBar = _draggingPosition.CloseBarIndex - 1;
                    _draggingPosition.OpenBarIndex = newOpenBar;
                    break;

                case DragTarget.RightBorder:
                    int newCloseBar = GetBarByX(e.Location.X);
                    // 边界检查：至少保留1根K线宽度
                    if (newCloseBar <= _draggingPosition.OpenBarIndex)
                        newCloseBar = _draggingPosition.OpenBarIndex + 1;
                    _draggingPosition.CloseBarIndex = newCloseBar;
                    break;

                case DragTarget.WholeRectangle:
                    // X方向：时间（优化：使用缓存的_dragStartBar）
                    int currentMouseBar = GetBarByX(e.Location.X);
                    int barDelta = currentMouseBar - _dragStartBar;
                    int barWidth = _draggingPosition.CloseBarIndex - _draggingPosition.OpenBarIndex;

                    _draggingPosition.OpenBarIndex = _dragStartOpenBar + barDelta;
                    _draggingPosition.CloseBarIndex = _draggingPosition.OpenBarIndex + barWidth;

                    // Y方向：价格
                    decimal mousePrice = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
                    decimal priceDelta = mousePrice - _dragStartEntry;

                    _draggingPosition.EntryPrice = _dragStartEntry + priceDelta;
                    _draggingPosition.TakeProfit = _dragStartTP + priceDelta;
                    _draggingPosition.StopLoss = _dragStartSL + priceDelta;
                    break;
            }

            RedrawChart();
            return true;
        }

        public override bool ProcessMouseUp(RenderControlMouseEventArgs e)
        {
            if (_dragTarget != DragTarget.None && _draggingPosition != null)
            {
                // 计算收益指标
                var metrics = CalculateMetrics(_draggingPosition);

                // 日志输出
                AddAlert("仓位更新",
                    $"R:R={metrics.RRRatio:F2}, " +
                    $"Risk={metrics.RiskTicks:F0} ticks, " +
                    $"Reward={metrics.RewardTicks:F0} ticks, " +
                    $"Hold={metrics.HoldingBars} bars");

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
            // ESC键取消等待模式
            if (e.Key == Key.Escape)
            {
                if (_mode != InteractionMode.Normal)
                {
                    _mode = InteractionMode.Normal;
                    RedrawChart();
                    return true;
                }
            }
            return base.ProcessKeyDown(e);
        }

        public override StdCursor GetCursor(RenderControlMouseEventArgs e)
        {
            // 检查按钮区域
            if (IsPointInRectangle(e.Location, _longButton) ||
                IsPointInRectangle(e.Location, _shortButton) ||
                (_positions.Count > 0 && IsPointInRectangle(e.Location, _exportButton)))
                return StdCursor.Hand;

            foreach (var pos in _positions)
            {
                int tpY = ChartInfo.PriceChartContainer.GetYByPrice(pos.TakeProfit, false);
                int slY = ChartInfo.PriceChartContainer.GetYByPrice(pos.StopLoss, false);
                int entryY = ChartInfo.PriceChartContainer.GetYByPrice(pos.EntryPrice, false);
                int leftX = ChartInfo.GetXByBar(pos.OpenBarIndex);
                int rightX = ChartInfo.GetXByBar(pos.CloseBarIndex);

                // 水平线 → SizeNS（上下调整）
                if ((IsNearLine(e.Location.Y, tpY, LineTolerance) ||
                     IsNearLine(e.Location.Y, slY, LineTolerance) ||
                     IsNearLine(e.Location.Y, entryY, LineTolerance)) &&
                    e.Location.X >= leftX && e.Location.X <= rightX)
                    return StdCursor.SizeNS;

                // 垂直边界 → SizeWE（左右调整）
                int topY = ChartInfo.PriceChartContainer.GetYByPrice(
                    pos.Direction == PositionDirection.Long ? pos.TakeProfit : pos.StopLoss, false);
                int bottomY = ChartInfo.PriceChartContainer.GetYByPrice(
                    pos.Direction == PositionDirection.Long ? pos.StopLoss : pos.TakeProfit, false);

                if ((IsNearLine(e.Location.X, leftX, LineTolerance) ||
                     IsNearLine(e.Location.X, rightX, LineTolerance)) &&
                    e.Location.Y >= topY && e.Location.Y <= bottomY)
                    return StdCursor.SizeWE;

                // 矩形内部 → SizeAll（全方向移动）
                Rectangle rect = new Rectangle(leftX, topY, rightX - leftX, bottomY - topY);
                if (IsPointInRectangle(e.Location, rect))
                    return StdCursor.SizeAll;
            }

            return StdCursor.Arrow;
        }

        #endregion

        #region 核心方法 - CSV导出

        /// <summary>
        /// CSV字段转义（防止注入和格式错误）
        /// </summary>
        private string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return field;

            // 如果包含逗号、引号或换行符，需要转义
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
            {
                // 用双引号包裹，内部的引号翻倍
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }

            return field;
        }

        /// <summary>
        /// 导出到CSV
        /// </summary>
        private void ExportToCsv()
        {
            string fileName = $"PaperTrades_{InstrumentInfo.Instrument}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), fileName);

            try
            {
                var lines = new List<string>();

                // CSV头
                lines.Add("Direction,OpenTime,CloseTime,OpenBar,CloseBar,HoldBars,Entry,TP,SL,RiskTicks,RewardTicks,RR");

                // 数据行
                foreach (var pos in _positions)
                {
                    var openCandle = GetCandle(pos.OpenBarIndex);
                    var closeCandle = GetCandle(pos.CloseBarIndex);

                    // 使用N/A代替null，确保所有仓位都导出
                    string openTime = openCandle?.Time.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";
                    string closeTime = closeCandle?.Time.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";

                    var metrics = CalculateMetrics(pos);

                    lines.Add(string.Join(",",
                        pos.Direction,
                        openTime,
                        closeTime,
                        pos.OpenBarIndex,
                        pos.CloseBarIndex,
                        metrics.HoldingBars,
                        pos.EntryPrice.ToString("F2"),
                        pos.TakeProfit.ToString("F2"),
                        pos.StopLoss.ToString("F2"),
                        metrics.RiskTicks.ToString("F1"),
                        metrics.RewardTicks.ToString("F1"),
                        metrics.RRRatio.ToString("F2")
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

        #region 辅助方法

        /// <summary>
        /// 根据X坐标获取Bar索引
        /// </summary>
        private int GetBarByX(int x)
        {
            // 获取第一个可见bar的X坐标
            int firstBar = ChartInfo.PriceChartContainer.FirstVisibleBarNumber;
            int firstX = ChartInfo.GetXByBar(firstBar);

            // 计算bar宽度
            decimal barWidth = ChartInfo.PriceChartContainer.BarsWidth;

            // 防止除零错误
            if (barWidth <= 0)
                return firstBar;

            // 计算偏移量（向下取整）
            int barOffset = (int)((x - firstX) / barWidth);

            // 返回bar索引（带边界检查）
            int barIndex = firstBar + barOffset;

            // 边界限制：确保不小于0，不大于CurrentBar-1
            return Math.Max(0, Math.Min(barIndex, CurrentBar - 1));
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
        /// 判断点是否接近线
        /// </summary>
        private bool IsNearLine(int pointCoord, int lineCoord, int tolerance)
        {
            return Math.Abs(pointCoord - lineCoord) <= tolerance;
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
        /// 查找点击位置的仓位
        /// </summary>
        private PaperPosition FindPositionAtPoint(Point point)
        {
            foreach (var pos in _positions)
            {
                int leftX = ChartInfo.GetXByBar(pos.OpenBarIndex);
                int rightX = ChartInfo.GetXByBar(pos.CloseBarIndex);
                int topY = ChartInfo.PriceChartContainer.GetYByPrice(
                    pos.Direction == PositionDirection.Long ? pos.TakeProfit : pos.StopLoss, false);
                int bottomY = ChartInfo.PriceChartContainer.GetYByPrice(
                    pos.Direction == PositionDirection.Long ? pos.StopLoss : pos.TakeProfit, false);

                Rectangle rect = new Rectangle(leftX, topY, rightX - leftX, bottomY - topY);
                if (IsPointInRectangle(point, rect))
                {
                    return pos;
                }
            }
            return null;
        }

        #endregion

        #region 生命周期

        protected override void OnCalculate(int bar, decimal value)
        {
            // 基础计算
        }

        protected override void OnDispose()
        {
            base.OnDispose();
        }

        #endregion
    }
}
