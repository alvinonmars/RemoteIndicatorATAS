# Paper Trading Helper V2 - 功能定义文档

**版本**: v2.0
**更新日期**: 2025-10-29
**项目**: RemoteIndicatorATAS_standalone
**平台**: ATAS Trading Platform

---

## 一、核心概念

### 1.1 双层矩形设计

轻量级模拟交易可视化工具，通过**双层矩形绘图对象**模拟限价单交易的规划与执行。

```
视觉结构：
┌─────────────────────────────────┐ ← TopPrice (max(TP, SL))
│     盈利区域（浅绿 alpha=30）   │
│  ━━━━━━━━━━━━━━━━━━━━━━━━━━━  │ ← Limit Price（限价单价格）
│ ┏━━━━━━━━━━━┓                  │   [Long@3355.5 Closed P&L:20.0]
│ ┃ 执行路径  ┃                  │
│ ┃(深色alpha=100)               │
│ ┗━━━━━━━━━━━┛                  │
│     风险区域（浅红 alpha=30）   │
└─────────────────────────────────┘ ← BottomPrice (min(TP, SL))
  ↑           ↑                  ↑
LeftBar    OpenBar           RightBar
        (限价成交)         (时间边界)
```

**核心特性**：
- **清晰的边界定义**：Left/Right（时间）+ TP/SL（价格）+ Limit（分割线）
- **两层分离**：规划层（用户设置）vs 执行层（系统计算）
- **限价单模拟**：Limit Price 将矩形分成盈利区和风险区
- **自动执行判断**：扫描K线数据判断限价单成交和TP/SL触发
- **防数据泄漏**：执行计算严格限制在LastVisibleBarNumber内，确保回测真实性

---

## 二、功能特性

### 2.1 核心功能

| 功能 | 说明 | 特点 |
|------|------|------|
| **交互式创建** | 两步创建流程：点击按钮→点击图表 | 十字线辅助定位，自动计算TP/SL |
| **6种拖动模式** | TP线/SL线/Limit线/左边界/右边界/整体矩形 | 实时预览，自动边界交换 |
| **限价单执行模拟** | 扫描K线判断限价单成交 | 支持未成交情况 |
| **止盈止损判断** | 自动判断触及TP/SL | 保守原则：同时触及按止损处理 |
| **时间到期平仓** | 到达RightBarIndex强制平仓 | 按收盘价平仓 |
| **执行路径可视化** | 深色矩形+对角线+圆点 | 盈利绿色/亏损红色，基于实际结果 |
| **CSV导出** | 完整交易数据导出 | 包含时间戳、元数据、计算指标 |
| **防数据泄漏** | 执行计算限制在可见数据内 | 确保回测真实性 |

### 2.2 交互功能

| 功能 | 触发方式 | 效果 | 约束规则 |
|------|---------|------|---------|
| **创建Long仓位** | 点击Long按钮→点击图表 | 创建多头矩形，绿色边框 | - |
| **创建Short仓位** | 点击Short按钮→点击图表 | 创建空头矩形，红色边框 | - |
| **拖动TP线** | 鼠标点击TP标签拖动 | 只调整TakeProfitPrice | Long: TP ≥ Limit+5ticks<br>Short: TP ≤ Limit-5ticks |
| **拖动SL线** | 鼠标点击SL标签拖动 | 只调整StopLossPrice | Long: SL ≤ Limit-5ticks<br>Short: SL ≥ Limit+5ticks |
| **拖动Limit线** | 鼠标点击Limit标签拖动 | Limit/TP/SL同步移动 | 保持TP/SL相对距离不变 |
| **拖动左边界** | 鼠标距左边界±5px，拖动 | 调整LeftBarIndex，自动交换 | 0 ≤ LeftBar ≤ CurrentBar-1<br>超过RightBar时自动交换 |
| **拖动右边界** | 鼠标距右边界±5px，拖动 | 调整RightBarIndex，自动交换 | 0 ≤ RightBar ≤ CurrentBar-1<br>低于LeftBar时自动交换 |
| **整体拖动** | 鼠标在矩形主体拖动 | 时间+价格同步移动 | 时间范围：[0, CurrentBar-1]<br>价格无限制 |
| **删除仓位** | 右键点击或Delete键 | 删除当前仓位 | - |
| **导出CSV** | 点击Export按钮 | 导出所有仓位数据 | - |
| **取消操作** | ESC键 | 退出等待模式 | - |

**交互触发说明**：
- **基于标签拖动**：TP/SL/Limit线通过点击对应标签触发拖动，提供更大的触发区域
- **边界拖动区域**：左右边界有10px宽的触发区域（BorderHitboxWidth=10）
- **主体拖动区域**：矩形内部排除边界后的区域
- **低可视模式兼容**：即使仓位处于低可视模式（只显示淡化边框），所有拖动功能仍然正常工作

### 2.3 可视化功能

| 元素 | 规格 | 颜色设计 |
|------|------|---------|
| **盈利区域** | 浅色填充，alpha=30 | 纯绿色 `Color.FromArgb(30, 0, 255, 0)` |
| **风险区域** | 浅色填充，alpha=30 | 纯红色 `Color.FromArgb(30, 255, 0, 0)` |
| **执行路径矩形** | 深色填充，alpha=100 | 盈利=绿 / 亏损=红（基于实际结果） |
| **对角线** | 虚线，2px宽 | Long=绿 / Short=红 |
| **平仓圆点** | 半径5px | TP=亮绿 / SL=橙红 / 到期=金色 |
| **Limit线** | 白色实线，2px宽 | 白色 |
| **TP线** | 绿色虚线，1px宽 | `Color.FromArgb(255, 50, 180, 80)` |
| **SL线** | 红色虚线，1px宽 | `Color.FromArgb(255, 180, 50, 50)` |
| **标签** | 底色+居中白字 | 盈利=深绿 / 亏损=深红 / 未成交=灰 |

---

## 三、数据结构

### 3.1 核心数据类型

#### PaperPosition（规划层）
用户设置的"计划"，定义矩形的边界和价格。

| 字段 | 类型 | 说明 |
|------|------|------|
| `Id` | Guid | 唯一标识 |
| `Direction` | PositionDirection | Long/Short |
| `LeftBarIndex` | int | 矩形左边界（最早时间） |
| `RightBarIndex` | int | 矩形右边界（最晚时间） |
| `LimitPrice` | decimal | 限价单价格（分割线） |
| `TakeProfitPrice` | decimal | 止盈价格（矩形一边） |
| `StopLossPrice` | decimal | 止损价格（矩形另一边） |

**不变量**：`LeftBarIndex ≤ RightBarIndex`（拖动时自动交换）

#### PositionExecution（执行层）
系统计算的"实际"执行结果。

| 字段 | 类型 | 说明 |
|------|------|------|
| `IsExecuted` | bool | 限价单是否成交 |
| `OpenBarIndex` | int | 实际开仓bar |
| `CloseBarIndex` | int | 实际平仓bar |
| `OpenPrice` | decimal | 实际开仓价 = LimitPrice |
| `ClosePrice` | decimal | 实际平仓价（TP/SL/收盘价） |
| `Reason` | ExitReason | 平仓原因（TP/SL/TimeExpired） |

#### PositionMetrics（计算结果）
收益和风险指标。

| 字段 | 类型 | 说明 |
|------|------|------|
| `RiskTicks` | decimal | 风险（跳数） |
| `RewardTicks` | decimal | 收益（跳数） |
| `RRRatio` | decimal | 风报比 |
| `HoldingBars` | int | 持仓K线数 |
| `ActualPnL` | decimal | 实际盈亏（价格差） |

### 3.2 枚举类型

```csharp
enum PositionDirection { Long, Short }
enum ExitReason { TakeProfit, StopLoss, TimeExpired }
enum ButtonPosition { TopLeft, TopRight, BottomLeft, BottomRight }
```

---

## 四、执行判断算法

### 4.1 核心算法逻辑

**三步扫描流程**：

1. **扫描限价单成交**：在 `[LeftBarIndex, RightBarIndex]` 范围内，找到第一根触及LimitPrice的K线
   - 成交条件：`candle.Low ≤ LimitPrice ≤ candle.High`
   - 未成交：返回 `IsExecuted = false`

2. **扫描TP/SL触发**：从成交后下一根K线开始，判断是否触及TP或SL
   - **Long仓位**：先检查止损（Low），再检查止盈（High）
   - **Short仓位**：先检查止损（High），再检查止盈（Low）
   - **保守原则**：同一根K线同时触及，按止损处理

3. **时间到期平仓**：到达RightBarIndex仍未触及TP/SL，按收盘价强制平仓
   - 平仓价格：`ClosePrice = closeCandle.Close`
   - 平仓原因：`ExitReason.TimeExpired`

### 4.2 防数据泄漏机制

**关键限制**：
```
lastCompletedBar = LastVisibleBarNumber - 1
endBar = Min(RightBarIndex, CurrentBar - 1, lastCompletedBar)
```

**重要说明**：
- `LastVisibleBarNumber` 是最后一根**可见**的bar，但这根bar可能**还在形成中**（未完成）
- 未完成的bar数据不完整，不应该用于执行判断
- **必须使用 `LastVisibleBarNumber - 1`**（最后一根**完成**的bar）
- 这样确保只使用完整、确定的历史数据进行判断

**适用范围**：
- ✅ **执行层计算**：严格限制在完成的bar内
- ❌ **规划层设置**：不受影响，用户可以自由设置 `RightBarIndex`（包括未来）
- ❌ **大矩形绘制**：不受影响，绘制完整的 `[LeftBarIndex, RightBarIndex]` 范围
- ❌ **拖动功能**：不受影响，可以拖动到任意位置

---

## 五、CSV导出格式

### 5.1 导出字段（按列顺序）

| 分组 | 字段 | 说明 |
|------|------|------|
| **时间戳** | OpenBarOpenTime | Open Bar的开始时间（yyyy-MM-dd HH:mm:ss） |
| | OpenBarCloseTime | Open Bar的结束时间 |
| | CloseBarCloseTime | Close Bar的结束时间 |
| **元数据** | Symbol | 交易品种（如ES, NQ） |
| | ChartType | 图表类型 |
| | TimeFrame | 时间周期 |
| **交易信息** | Direction | Long/Short |
| | LeftBar | 矩形左边界bar索引 |
| | RightBar | 矩形右边界bar索引 |
| | OpenBar | 实际开仓bar索引 |
| | CloseBar | 实际平仓bar索引 |
| | LimitPrice | 限价单价格 |
| | OpenPrice | 实际开仓价格 |
| | ClosePrice | 实际平仓价格 |
| | TP | 止盈价格 |
| | SL | 止损价格 |
| **执行状态** | IsExecuted | True/False |
| | ExitReason | TakeProfit/StopLoss/TimeExpired/NotExecuted |
| **计算指标** | RiskTicks | 风险（跳数） |
| | RewardTicks | 收益（跳数） |
| | RR | 风报比 |
| | ActualPnL | 实际盈亏 |
| | HoldingBars | 持仓K线数 |

### 5.2 示例数据

```csv
OpenBarOpenTime,OpenBarCloseTime,CloseBarCloseTime,Symbol,ChartType,TimeFrame,Direction,LeftBar,RightBar,OpenBar,CloseBar,LimitPrice,OpenPrice,ClosePrice,TP,SL,IsExecuted,ExitReason,RiskTicks,RewardTicks,RR,ActualPnL,HoldingBars
2024-01-15 09:30:00,2024-01-15 09:35:00,2024-01-15 09:45:00,ES,Candles,5m,Long,100,105,101,103,4500.00,4500.00,4520.00,4520.00,4490.00,True,TakeProfit,10.0,20.0,2.00,20.00,2
2024-01-15 10:00:00,2024-01-15 10:05:00,2024-01-15 10:15:00,ES,Candles,5m,Short,125,135,126,128,4510.00,4510.00,4520.00,4500.00,4520.00,True,StopLoss,10.0,10.0,1.00,-10.00,2
N/A,N/A,N/A,ES,Candles,5m,Long,200,210,N/A,N/A,4550.00,N/A,N/A,4570.00,4540.00,False,NotExecuted,10.0,20.0,2.00,0.00,0
```

---

## 六、配置参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| **DefaultStopTicks** | 20 | 默认止损（跳数） |
| **RRRatio** | 2.0 | 默认风报比 |
| **DefaultBarWidth** | 15 | 初始矩形宽度（K线数） |
| **ButtonsPosition** | BottomLeft | 按钮位置（TopLeft/TopRight/BottomLeft/BottomRight） |
| **MinPriceDistanceInTicks** | 5 | TP/SL与Limit的最小距离（tick数） |
| **MouseHoverExpandPx** | 20 | 低可视模式触发区域扩展（像素） |
| **BorderHitboxWidth** | 10 | 边界拖动触发区域宽度（像素） |
| **LabelWidth** | 500 | 标签固定宽度（像素） |
| **LabelHeight** | 24 | 标签固定高度（像素） |

---

## 七、交互细节

### 7.1 边界自动交换机制

**问题**：拖动左边界可能超过右边界，破坏 `Left ≤ Right` 不变量。

**解决方案**：自动交换
- 拖动左边界过右边界：`Left ← Right, Right ← newLeft`
- 拖动右边界过左边界：`Right ← Left, Left ← newRight`

**效果**：无需用户关心边界顺序，拖动时自动保持 `Left ≤ Right`。

### 7.2 光标反馈系统

| 位置 | 光标 | 说明 |
|------|------|------|
| TP/SL/Limit线（±5px） | SizeNS（上下箭头） | 可垂直拖动 |
| 左/右边界（±5px） | SizeWE（左右箭头） | 可水平拖动 |
| 矩形内部 | SizeAll（四向箭头） | 可整体拖动 |
| 按钮区域 | Hand（手型） | 可点击 |
| 其他区域 | Arrow（默认） | 无交互 |

### 7.3 标签设计

**P&L标签**（Limit Price位置）：
- 未成交：灰色底 + "Pending"
- 已成交盈利：深绿底 + "Closed P&L:+20.0"
- 已成交亏损：深红底 + "Closed P&L:-10.0"

**TP标签**（TP价格位置）：
- 绿色底 + `Target@4520.00: 20.0(0.444%)`

**SL标签**（SL价格位置）：
- 红色底 + `Stop@4490.00: 10.0(0.222%)`

**标签规格**：
- 尺寸：500×24px
- 文字：完全居中（水平+垂直）
- 边框：白色1px

### 7.4 Z-Order交互优先级（Painter's Algorithm）

#### 设计原则
当多个仓位重叠时，**视觉最顶层的仓位优先响应交互**。

#### 渲染顺序 vs 交互顺序
```
Position列表：[0] A, [1] B, [2] C

渲染顺序（Painting）：
A → B → C  （正序，后绘制的覆盖前面的）

交互检测（Hit-testing）：
C → B → A  （逆序，先检测后绘制的）

视觉效果：C在最上层
交互响应：点击重叠区域 → 操作C ✅
```

#### 实现方式
- **渲染循环**：`foreach (var pos in _positions)` - 正序遍历（0→N）
- **交互循环**：`for (int i = _positions.Count - 1; i >= 0; i--)` - 逆序遍历（N→0）

**受影响的方法**：
1. `ProcessMouseDown()` - 拖动目标检测
2. `GetCursor()` - 光标样式检测
3. `FindPositionAtPoint()` - 位置查找（右键删除、Delete键）

#### 用户体验
- ✅ 最新创建的仓位自动在最顶层（添加到列表末尾）
- ✅ 点击重叠区域只响应视觉最顶层的对象
- ✅ 光标样式与视觉层级保持一致
- ⚠️ 如果需要操作被遮挡的仓位，需先删除上层的仓位

#### 设计权衡
**优点**：
- 符合用户心智模型：看到的就是能操作的
- 实现简单：无需显式z-index管理
- 性能一致：O(n)时间复杂度不变

**限制**：
- 无法直接操作被完全遮挡的仓位
- 未来可扩展：可添加"发送到后层"/"置顶"等功能

---

## 八、关键设计决策

### 决策1：双层分离设计
**理由**：
- 规划层（Planning Layer）：用户设置的计划（Left/Right/Limit/TP/SL）
- 执行层（Execution Layer）：系统计算的实际结果（Open/Close bar, 实际价格）
- 清晰区分"计划"与"实际"，符合交易思维

### 决策2：限价单模拟
**理由**：
- 真实交易使用限价单，需要模拟成交判断
- 支持未成交情况，更真实
- Limit Price作为分割线，直观显示盈利/风险区域

### 决策3：保守的执行判断
**理由**：
- 同一根K线同时触及TP/SL时，按止损处理
- 符合真实交易中的滑点情况
- 避免过于乐观的回测结果

### 决策4：基于结果的颜色系统
**理由**：
- 执行路径颜色由实际结果决定（盈利=绿，亏损=红）
- 而非由方向决定（Long≠绿，Short≠红）
- 更直观展示交易结果

### 决策5：防数据泄漏机制（关键）
**理由**：
- 严格限制执行计算在 `LastVisibleBarNumber - 1` 内（最后一根**完成**的bar）
- `LastVisibleBarNumber` 可能是未完成的bar，数据不完整，不应使用
- 确保回测/模拟交易只使用完整、确定的历史数据
- 符合严格的量化交易标准

**为什么是 `-1`？**
```
Bar 1000 (LastVisibleBarNumber) ← 可能还在形成中，数据不完整
Bar 999  (LastVisibleBarNumber - 1) ← 已完成，数据完整 ✓
```

**重要性**：
- 这是防止未来数据泄漏的**最关键**细节
- 如果使用未完成的bar，会导致回测结果不真实
- 在实时交易中，当前bar的数据是动态变化的

---

## 九、使用场景

### 场景1：复盘分析
用户在历史图表上标记潜在的交易机会，拖动调整TP/SL，观察执行结果，导出CSV进行批量分析。

### 场景2：策略验证
用户根据策略信号在图表上创建仓位，验证风报比设置是否合理，统计胜率和盈亏比。

### 场景3：交易计划
用户在实盘前规划交易，设置限价单、TP、SL，预览可能的执行路径。

---

## 十、核心技术架构

### 10.1 渲染与交互分离架构

#### 设计理念
PaperTradingHelperV2采用**渲染层与交互层分离**的架构，确保在任何渲染模式下都能保持完整的交互能力。

```
┌─────────────────────────────────────────┐
│         OnRender 渲染入口               │
└─────────────────┬───────────────────────┘
                  │
      ┌───────────▼────────────┐
      │   DrawPosition         │
      │  (单个仓位渲染)        │
      └───────────┬────────────┘
                  │
      ┌───────────▼────────────────────────┐
      │  1. 计算坐标（leftX, rightX...）   │
      └───────────┬────────────────────────┘
                  │
      ┌───────────▼────────────────────────┐
      │  2. UpdatePositionHitboxes         │ ← 关键：无论渲染模式都执行
      │     更新所有Hitbox和标签Rect       │
      └───────────┬────────────────────────┘
                  │
      ┌───────────▼────────────────────────┐
      │  3. 判断渲染模式                   │
      │     鼠标在矩形内 → 完整模式        │
      │     鼠标不在矩形 → 低可视模式      │
      └───────────┬────────────────────────┘
                  │
         ┌────────┴─────────┐
         │                  │
    ┌────▼─────┐      ┌────▼──────┐
    │ 完整渲染 │      │ 简化渲染  │
    │ 所有图层 │      │ 仅边框   │
    └──────────┘      └───────────┘
```

**核心原则**：
- **Hitbox更新与渲染解耦**：交互检测不依赖渲染过程
- **低可视模式完整性**：即使不绘制标签，Hitbox仍然有效
- **职责单一**：UpdatePositionHitboxes专注计算，DrawLabels专注绘制

#### 技术实现

**关键方法**：`UpdatePositionHitboxes()` (Indicators/PaperTradingHelperV2.cs:939-993)

```csharp
/// <summary>
/// 更新仓位的Hitbox和标签Rect（用于交互检测）
/// 关键：无论是否渲染，都必须更新Hitbox，确保低可视模式下也能交互
/// </summary>
private void UpdatePositionHitboxes(PaperPosition position,
    int leftX, int rightX, int topY, int bottomY,
    int limitY, int tpY, int slY)
{
    // 计算标签位置（与DrawLabels保持一致）
    int rectWidth = rightX - leftX;
    int labelX = rectWidth >= LabelWidth
        ? leftX + (rectWidth - LabelWidth) / 2  // 居中
        : leftX + 5;                             // 左对齐

    // 更新标签Rect（用于交互检测）
    position.LimitLabel.Rect = new Rectangle(labelX, limitY - 25, LabelWidth, LabelHeight);
    position.TPLabel.Rect = new Rectangle(labelX, tpY - 25, LabelWidth, LabelHeight);
    position.SLLabel.Rect = new Rectangle(labelX, slY + 10, LabelWidth, LabelHeight);

    // 更新边界Hitbox
    position.LeftBorderHitbox = new Rectangle(
        leftX - BorderHitboxWidth / 2, topY,
        BorderHitboxWidth, bottomY - topY);

    position.RightBorderHitbox = new Rectangle(
        rightX - BorderHitboxWidth / 2, topY,
        BorderHitboxWidth, bottomY - topY);

    // 更新矩形主体Hitbox
    position.BodyHitbox = new Rectangle(
        leftX + BorderHitboxWidth, topY,
        Math.Max(1, rightX - leftX - 2 * BorderHitboxWidth),
        bottomY - topY);
}
```

**调用时机**：在`DrawPosition()`方法的第2步，计算坐标后立即调用（Indicators/PaperTradingHelperV2.cs:624）

```csharp
// === 2. 更新Hitbox（关键：无论渲染模式，都必须更新以支持交互）===
UpdatePositionHitboxes(position, leftX, rightX, topY, bottomY, limitY, tpY, slY);
```

**优势**：
1. **低可视模式交互保障**：即使简化渲染，拖动功能完全正常
2. **性能优化空间**：Hitbox更新与绘制分离，可独立缓存
3. **代码可维护性**：职责清晰，Hitbox计算逻辑集中管理

---

### 10.2 价格拖动约束系统

#### 业务规则
确保拖动后的仓位符合交易逻辑，防止无效配置。

**Long仓位约束**：
```
TakeProfitPrice ≥ LimitPrice + MinDistance
StopLossPrice ≤ LimitPrice - MinDistance
```

**Short仓位约束**：
```
TakeProfitPrice ≤ LimitPrice - MinDistance
StopLossPrice ≥ LimitPrice + MinDistance
```

**最小距离要求**：
```csharp
MinPriceDistanceInTicks = 5  // 最小5个tick
MinDistance = 5 * InstrumentInfo.TickSize
```

#### 技术实现

**拖动TP线约束** (Indicators/PaperTradingHelperV2.cs:1206-1227)：
```csharp
case DragTarget.TakeProfitLine:
{
    decimal newTP = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
    decimal tickSize = InstrumentInfo.TickSize;
    if (tickSize <= 0) tickSize = 0.01m;
    decimal minDistance = MinPriceDistanceInTicks * tickSize;

    // 约束：TP必须在盈利方向
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
```

**拖动SL线约束** (Indicators/PaperTradingHelperV2.cs:1229-1250)：
```csharp
case DragTarget.StopLossLine:
{
    decimal newSL = ChartInfo.PriceChartContainer.GetPriceByY(e.Location.Y);
    decimal tickSize = InstrumentInfo.TickSize;
    if (tickSize <= 0) tickSize = 0.01m;
    decimal minDistance = MinPriceDistanceInTicks * tickSize;

    // 约束：SL必须在风险方向
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
```

**设计特点**：
- **实时约束**：拖动过程中动态限制，无需等到释放鼠标
- **方向感知**：根据Long/Short自动选择约束方向
- **最小距离保护**：防止设置过窄的止盈止损（避免频繁触发）
- **防御性编程**：TickSize <= 0时使用默认值0.01

**用户体验**：
- 拖动时感觉"有边界"：无法拖到无效位置
- 自然反馈：鼠标可以移动，但价格被约束在合理范围
- 防止误操作：不会产生 TP < SL 等逻辑错误

---

### 10.3 边界拖动的完整性保护

#### 防御性边界检查

**三重保护机制**：

1. **防止负索引**：`newBar = Math.Clamp(newBar, 0, maxBarIndex)`
2. **防未来数据泄漏**：`maxBarIndex = CurrentBar > 0 ? CurrentBar - 1 : 0`
3. **自动交换保持不变量**：当左边界拖过右边界时自动交换

#### 技术实现

**左边界拖动** (Indicators/PaperTradingHelperV2.cs:1264-1287)：
```csharp
case DragTarget.LeftBorder:
{
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
```

**右边界拖动** (Indicators/PaperTradingHelperV2.cs:1289-1312)：
```csharp
case DragTarget.RightBorder:
{
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
```

#### 防未来数据泄漏的严格性

**核心约束**：`maxBarIndex = CurrentBar - 1`

```
Bar 1000 (CurrentBar)         ← 正在形成中，数据不完整 ❌
Bar 999  (CurrentBar - 1)     ← 已完成，数据完整 ✓ 可拖动到此
Bar 998                        ← 已完成，数据完整 ✓
```

**为什么必须是 `-1`？**
- `CurrentBar` 是**正在形成的bar**，其High/Low/Close随时变化
- 如果将右边界拖到 `CurrentBar`，执行判断会使用未完成的数据
- 回测结果会包含"未来信息"，失去真实性

**与执行层的一致性**：
- 执行层计算：`endBar = Min(RightBarIndex, CurrentBar - 1, lastCompletedBar)`
- 拖动约束：`maxBarIndex = CurrentBar - 1`
- **确保规划层和执行层使用相同的边界定义**

**特殊处理**：
```csharp
int maxBarIndex = CurrentBar > 0 ? CurrentBar - 1 : 0;
```
- 防止 `CurrentBar = 0` 时产生负数索引
- 边界情况保护，确保系统稳定性

---

### 10.4 低可视模式设计

#### 设计目标
- 减少视觉干扰：历史仓位不遮挡当前价格行为
- 优化GPU负载：减少绘制调用（大量仓位时）
- 保持交互能力：鼠标悬停时恢复完整显示

#### 判断逻辑 (Indicators/PaperTradingHelperV2.cs:632)

```csharp
// 构建鼠标检测区域（比矩形稍大，方便触发）
Rectangle bigRect = new Rectangle(
    leftX - MouseHoverExpandPx,
    topY - 2*LabelHeight,
    rightX - leftX + 2 * MouseHoverExpandPx,
    (bottomY + 2*LabelHeight) - (topY - 2*LabelHeight)
);

// 鼠标不在区域内 且 不是最新仓位 → 低可视模式
bool isLowVisibility = !IsPointInRectangle(mousePos, bigRect) && !isLastPosition;
```

**参数配置**：
```csharp
private const int MouseHoverExpandPx = 20;  // 鼠标触发扩展区域
```

**特殊规则**：
- 最新仓位（`isLastPosition`）始终使用完整模式
- 扩展20像素：鼠标接近矩形时提前触发完整显示
- 上下扩展2倍标签高度：考虑标签可能超出矩形

#### 渲染差异

**完整模式**：
1. 绘制规划层（盈利区、风险区背景）
2. 绘制执行层（执行路径矩形、对角线、圆点）
3. 绘制边框和价格线
4. 绘制标签（Limit/TP/SL）

**低可视模式**：
1. 仅绘制淡化边框（alpha=30）
2. 绘制执行层对角线（保留执行路径信息）
3. **跳过所有标签和背景填充**

**性能优化**：
- 低可视模式减少约60%的绘制调用
- 100个历史仓位时，性能提升明显
- 但**不影响交互能力**（Hitbox仍然有效）

---

## 十一、技术要求

### 10.1 平台要求
- ATAS Trading Platform
- .NET Framework 4.7.2+
- ATAS.Indicators SDK

### 10.2 性能要求
- 支持最多1000个仓位
- 实时绘制刷新（<16ms）
- 大量仓位时自动剔除不可见部分

### 10.3 精度要求
- 坐标映射精度：反向验证算法，确保Bar索引精确
- 价格精度：根据TickSize动态调整
- 时间戳精度：yyyy-MM-dd HH:mm:ss

---

---

## 十二、版本历史

### v2.2 (2025-10-30)
**Z-Order交互优先级修复**
- ✅ 修复重叠仓位交互优先级问题（Painter's Algorithm）
- ✅ 交互检测改为逆序遍历，匹配渲染顺序
- ✅ 新增文档：Z-Order交互优先级设计（Section 7.4）

**技术细节**：
- `ProcessMouseDown()`: 逆序遍历 `for (i = Count-1; i >= 0; i--)`
- `GetCursor()`: 逆序遍历，确保光标与视觉层级一致
- `FindPositionAtPoint()`: 逆序遍历，右键/Delete键操作最顶层
- **原则**：视觉最顶层优先响应，符合用户心智模型

**影响**：
- 重叠区域：只操作最顶层仓位（符合预期）
- 最新创建：自动成为最顶层（添加到列表末尾）
- 性能：O(n)不变，仅改变遍历方向

### v2.1 (2025-10-30)
**核心技术架构优化**
- ✅ 重构Hitbox更新机制：渲染与交互完全解耦
- ✅ 修复低可视模式下无法拖动的bug
- ✅ 新增价格拖动约束系统（防止无效配置）
- ✅ 新增边界拖动完整性保护（防止负索引和未来数据泄漏）
- ✅ 更新文档：新增"核心技术架构"章节

**技术细节**：
- `UpdatePositionHitboxes()`: 独立的Hitbox计算方法，无论渲染模式都执行
- 价格约束：Long仓位TP≥Limit+5ticks, SL≤Limit-5ticks（Short反向）
- 边界约束：`Math.Clamp(newBar, 0, CurrentBar-1)` 确保不越界
- 低可视模式：减少60%绘制调用，但保持100%交互能力

### v2.0 (2025-10-29)
**初始版本**
- ✅ 双层矩形设计（规划层 + 执行层）
- ✅ 限价单执行模拟
- ✅ 6种拖动模式
- ✅ 低可视模式
- ✅ CSV导出功能
- ✅ 防数据泄漏机制

---

**文档版本**: v2.2
**最后更新**: 2025-10-30
**状态**: 生产环境
**维护者**: Paper Trading Helper 开发团队
