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

| 功能 | 触发方式 | 效果 |
|------|---------|------|
| **创建Long仓位** | 点击Long按钮→点击图表 | 创建多头矩形，绿色边框 |
| **创建Short仓位** | 点击Short按钮→点击图表 | 创建空头矩形，红色边框 |
| **拖动TP线** | 鼠标距TP线±5px，拖动 | 只调整TakeProfitPrice |
| **拖动SL线** | 鼠标距SL线±5px，拖动 | 只调整StopLossPrice |
| **拖动Limit线** | 鼠标距Limit线±5px，拖动 | Limit/TP/SL同步移动 |
| **拖动左边界** | 鼠标距左边界±5px，拖动 | 调整LeftBarIndex，自动交换 |
| **拖动右边界** | 鼠标距右边界±5px，拖动 | 调整RightBarIndex，自动交换 |
| **整体拖动** | 鼠标在矩形内部拖动 | 时间+价格同步移动 |
| **删除仓位** | 右键点击或Delete键 | 删除当前仓位 |
| **导出CSV** | 点击Export按钮 | 导出所有仓位数据 |
| **取消操作** | ESC键 | 退出等待模式 |

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

## 十、技术要求

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

**文档版本**: v2.0
**状态**: 已完成实现
**维护者**: Paper Trading Helper 开发团队
