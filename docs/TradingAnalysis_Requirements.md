# 日内交易记录分析系统 - 功能定义文档

**版本**: v1.0
**更新日期**: 2025-10-29
**项目**: RemoteIndicatorATAS_standalone - Paper Trading Helper V2
**目的**: 对推格子交易记录进行全面量化分析

---

## 一、核心需求概述

### 1.1 功能目标

为推格子助手添加专业的交易分析功能，提供：
- ✅ 全面的统计指标（收益、胜率、盈亏比、回撤等）
- ✅ 分维度分析（Long/Short、时段、手续费）
- ✅ 高级量化指标（Sharpe、Calmar比率）
- ✅ 可视化收益曲线
- ✅ 可配置的手续费计算

### 1.2 触发方式

**新增按钮**：`Analyze` 按钮
- 位置：与Long/Short/Export并列
- 颜色：金色系 `Color.FromArgb(255, 218, 165, 32)`
- 文字：`Analyze({N})` - N为可分析的交易数
- 功能：点击后弹出分析窗口或导出分析报告

---

## 二、配置参数

### 2.1 交易品种配置

| 参数名 | 类型 | 默认值 | 说明 | 示例（GC） |
|--------|------|--------|------|-----------|
| **Symbol** | string | - | 交易品种代码 | "GC" |
| **TickValue** | decimal | 10.0 | 每tick价值（美元） | $10.00 |
| **CommissionPerSide** | decimal | 2.2 | 单边手续费（美元） | $2.20 |
| **CommissionRoundTrip** | decimal | 4.4 | 双边手续费（美元） | $4.40 |
| **InitialCapital** | decimal | 5000.0 | 初始资金（美元） | $5,000.00 |

**重要说明**：
- `CommissionRoundTrip = CommissionPerSide × 2`
- 手续费以美元计价，与tick价值无关
- 不同品种的配置可预设（GC、ES、NQ等）

### 2.2 品种预设表

| 品种 | 名称 | TickValue | CommissionPerSide | CommissionRoundTrip |
|------|------|-----------|-------------------|---------------------|
| **GC** | 黄金 | $10.00 | $2.20 | $4.40 |
| **ES** | 标普500 | $12.50 | $1.25 | $2.50 |
| **NQ** | 纳斯达克 | $5.00 | $1.25 | $2.50 |
| **CL** | 原油 | $10.00 | $1.50 | $3.00 |
| **6E** | 欧元 | $12.50 | $2.50 | $5.00 |

---

## 三、计算指标体系

### 3.1 基础统计指标（Core Metrics）

#### 3.1.1 交易统计

| 指标名称 | 英文名 | 计算公式 | 说明 |
|---------|--------|----------|------|
| **总交易次数** | Total Trades | COUNT(已成交) | 只统计IsExecuted=true的交易 |
| **盈利次数** | Winning Trades | COUNT(ActualPnL > 0) | 考虑手续费后盈利的交易 |
| **亏损次数** | Losing Trades | COUNT(ActualPnL < 0) | 考虑手续费后亏损的交易 |
| **盈亏平衡次数** | Breakeven Trades | COUNT(ActualPnL = 0) | 正好盈亏平衡 |

#### 3.1.2 胜率与收益

| 指标名称 | 英文名 | 计算公式 | 说明 |
|---------|--------|----------|------|
| **胜率** | Win Rate | WinningTrades / TotalTrades × 100% | 百分比形式 |
| **总盈利（含费）** | Gross Profit | SUM(ActualPnL > 0) - CommissionRoundTrip × WinningTrades | 扣除手续费的总盈利 |
| **总亏损（含费）** | Gross Loss | SUM(ActualPnL < 0) - CommissionRoundTrip × LosingTrades | 包含手续费的总亏损（负数） |
| **净收益（含费）** | Net Profit | GrossProfit + GrossLoss | 最终盈亏 |
| **总盈利（0费）** | Gross Profit (0 Fee) | SUM(ActualPnL > 0) | 不扣手续费 |
| **总亏损（0费）** | Gross Loss (0 Fee) | SUM(ActualPnL < 0) | 不含手续费 |
| **净收益（0费）** | Net Profit (0 Fee) | GrossProfit(0) + GrossLoss(0) | 理想情况 |

#### 3.1.3 盈亏比分析

| 指标名称 | 英文名 | 计算公式 | 说明 |
|---------|--------|----------|------|
| **平均盈利** | Average Win | GrossProfit / WinningTrades | 单笔平均盈利 |
| **平均亏损** | Average Loss | \|GrossLoss\| / LosingTrades | 单笔平均亏损（绝对值） |
| **盈亏比** | Profit Factor | GrossProfit / \|GrossLoss\| | 总盈利/总亏损，>1为盈利 |
| **风报比** | Risk/Reward Ratio | AverageWin / AverageLoss | 平均盈利/平均亏损 |

**注意**：
- 盈亏比（Profit Factor）：看总体盈利能力
- 风报比（Risk/Reward Ratio）：看单笔交易质量

### 3.2 回撤指标（Drawdown Metrics）

#### 3.2.1 回撤计算原理

**回撤定义**：
- 从资金曲线的任一峰值到后续最低点的下跌幅度
- 用于衡量策略的风险承受能力

**计算步骤**：
1. 计算累计权益曲线（Equity Curve）
2. 计算运行最高权益（Running Maximum）
3. 当前回撤 = 运行最高权益 - 当前权益
4. 当前回撤率 = 当前回撤 / 运行最高权益

```csharp
decimal equity = InitialCapital;
decimal runningMax = InitialCapital;
decimal maxDrawdown = 0;
decimal maxDrawdownPct = 0;

foreach (var trade in trades)
{
    equity += trade.PnLWithFee;
    runningMax = Math.Max(runningMax, equity);
    decimal currentDrawdown = runningMax - equity;
    decimal currentDrawdownPct = currentDrawdown / runningMax;

    maxDrawdown = Math.Max(maxDrawdown, currentDrawdown);
    maxDrawdownPct = Math.Max(maxDrawdownPct, currentDrawdownPct);
}
```

#### 3.2.2 回撤指标

| 指标名称 | 英文名 | 计算公式 | 说明 |
|---------|--------|----------|------|
| **最大回撤金额** | Max Drawdown | MAX(RunningMax - Equity) | 美元金额 |
| **最大回撤率** | Max Drawdown % | MaxDrawdown / RunningMax × 100% | 百分比 |
| **最大单笔亏损** | Largest Loss | MIN(ActualPnL) | 单笔最大亏损 |
| **最大单笔盈利** | Largest Win | MAX(ActualPnL) | 单笔最大盈利 |

### 3.3 时间指标（Time Metrics）

| 指标名称 | 英文名 | 计算公式 | 说明 |
|---------|--------|----------|------|
| **平均持仓时间** | Avg Holding Time | AVG(CloseBarIndex - OpenBarIndex) | 以bar数量为单位 |
| **总交易天数** | Trading Days | COUNT(DISTINCT(日期)) | 去重后的交易日数 |
| **每日平均交易次数** | Avg Trades/Day | TotalTrades / TradingDays | 交易频率 |

### 3.4 高级量化指标（Advanced Metrics）

#### 3.4.1 Sharpe比率（夏普比率）

**定义**：衡量每单位风险的超额收益

**方法1：按笔计算（Per-Trade Sharpe）**

```
Sharpe = (平均收益 - 无风险收益率) / 收益标准差

其中：
- 平均收益 = 净收益 / 总交易次数
- 无风险收益率 = 0（简化假设）
- 收益标准差 = STDEV(每笔PnL)
```

**方法2：按天计算（Daily Sharpe）**

```
1. 将交易按日期分组
2. 计算每日净收益 = SUM(当天所有交易的PnL)
3. Sharpe = (平均日收益 - 0) / STDEV(日收益)
4. 年化Sharpe = Sharpe × sqrt(252)  // 252个交易日
```

| 指标名称 | 英文名 | 计算公式 | 说明 |
|---------|--------|----------|------|
| **Sharpe比率（按笔）** | Sharpe Ratio (Per-Trade) | MEAN(PnL) / STDEV(PnL) | 基于每笔交易 |
| **Sharpe比率（按天）** | Sharpe Ratio (Daily) | MEAN(DailyPnL) / STDEV(DailyPnL) | 基于每日聚合 |
| **年化Sharpe** | Annualized Sharpe | DailySharpe × sqrt(252) | 标准化到年 |

**Sharpe比率解读**：
- < 0：策略亏损
- 0 ~ 1：收益不理想
- 1 ~ 2：表现良好
- 2 ~ 3：表现优秀
- \> 3：表现卓越

#### 3.4.2 Calmar比率（卡玛比率）

**定义**：年化收益率与最大回撤率的比值

```
Calmar = 年化收益率 / 最大回撤率

其中：
- 年化收益率 = (净收益 / 初始资金) × (365 / 总交易天数)
- 最大回撤率 = 最大回撤金额 / 运行最高权益
```

| 指标名称 | 英文名 | 计算公式 | 说明 |
|---------|--------|----------|------|
| **年化收益率** | Annualized Return | (NetProfit / InitialCapital) × (365 / TradingDays) | 百分比 |
| **Calmar比率** | Calmar Ratio | AnnualizedReturn / MaxDrawdownPct | 收益/风险比 |

**Calmar比率解读**：
- < 0：策略亏损
- 0 ~ 1：风险过高
- 1 ~ 3：可接受
- \> 3：优秀

---

## 四、分维度分析

### 4.1 分方向分析（Long vs Short）

**目的**：判断策略是否在某个方向上有优势

**计算内容**：
- 对Long和Short分别计算第三章的所有指标
- 比较Long和Short的胜率、盈亏比、Sharpe等

**输出格式**：

| 指标 | All Trades | Long Only | Short Only |
|------|-----------|-----------|------------|
| 总交易次数 | 100 | 60 | 40 |
| 胜率 | 55% | 60% | 47.5% |
| 净收益 | $1,000 | $800 | $200 |
| Sharpe（按笔） | 1.5 | 1.8 | 1.0 |
| 最大回撤率 | 10% | 8% | 12% |

### 4.2 分时段分析（Session Analysis）

**时段定义**（UTC时间，需要转换）：

| 时段 | 名称 | UTC时间 | 说明 |
|------|------|---------|------|
| **亚盘** | Asian Session | 00:00 - 08:00 UTC | 东京、悉尼、香港市场 |
| **欧盘** | European Session | 08:00 - 16:00 UTC | 伦敦、法兰克福市场 |
| **美盘** | US Session | 16:00 - 24:00 UTC | 纽约、芝加哥市场 |

**注意**：
- CSV中的时间戳是本地时间，需要转换到UTC
- 根据OpenBarOpenTime判断时段
- 夏令时/冬令时可能影响时段划分

**计算内容**：
- 对每个时段分别计算所有基础指标
- 比较不同时段的表现

**输出格式**：

| 指标 | All Sessions | Asian | European | US |
|------|-------------|-------|----------|-----|
| 总交易次数 | 100 | 20 | 35 | 45 |
| 胜率 | 55% | 50% | 60% | 53% |
| 净收益 | $1,000 | $100 | $600 | $300 |
| 平均持仓时间 | 150 bars | 180 bars | 120 bars | 160 bars |

### 4.3 手续费对比分析（Fee Comparison）

**目的**：评估手续费对策略的影响

**计算内容**：
- 所有指标分别计算0手续费和正常手续费两个版本
- 计算手续费影响百分比

**输出格式**：

| 指标 | 0手续费 | 正常手续费 | 差异 | 影响% |
|------|---------|-----------|------|-------|
| 净收益 | $1,440.00 | $1,000.00 | -$440.00 | -30.6% |
| 胜率 | 60% | 55% | -5% | -8.3% |
| Sharpe（按笔） | 2.0 | 1.5 | -0.5 | -25.0% |
| 盈利因子 | 2.5 | 2.0 | -0.5 | -20.0% |

**影响% 计算**：
```
影响% = (正常手续费 - 0手续费) / 0手续费 × 100%
```

---

## 五、收益曲线（Equity Curve）

### 5.1 曲线定义

**权益曲线（Equity Curve）**：
- X轴：交易序号或时间
- Y轴：累计权益（美元）
- 起点：初始资金（如$5,000）
- 计算：权益[i] = 权益[i-1] + PnL[i] - 手续费[i]

### 5.2 绘制要求

**双曲线对比**：
1. **蓝色曲线**：0手续费权益曲线
2. **橙色曲线**：正常手续费权益曲线

**X轴选项**：
- 选项1：交易序号（1, 2, 3, ...）
- 选项2：时间轴（按OpenBarOpenTime）

**Y轴**：
- 权益金额（美元）
- 基准线：初始资金（虚线）

**图表元素**：
- 标题：`Equity Curve - {Symbol} ({StartDate} to {EndDate})`
- 图例：`0 Fee` / `With Fee ($4.40/RT)`
- 网格线：辅助阅读
- 标注：最高点、最低点、最终权益

### 5.3 绘制方法（建议）

**方法1：导出到CSV，用Excel/Python绘制**
- 优点：灵活、美观
- 缺点：需要额外工具

**方法2：在ATAS中使用OFT.Rendering绘制**
- 优点：集成到工具内
- 缺点：绘制能力有限

**方法3：生成HTML报告（推荐）**
- 使用Chart.js或Plotly.js
- 生成standalone HTML文件
- 优点：美观、交互式、易分享

---

## 六、分析报告格式

### 6.1 控制台文本报告（Console Report）

```
================================================================================
              Paper Trading Analysis Report - GC
================================================================================
Period: 2024-01-01 to 2024-01-31
Symbol: GC (Gold Futures)
Tick Value: $10.00 | Commission: $4.40/RT | Initial Capital: $5,000.00
================================================================================

                        PERFORMANCE SUMMARY
--------------------------------------------------------------------------------
                                    0 Fee          With Fee        Impact
--------------------------------------------------------------------------------
Total Trades                        100            100             -
Winning Trades                      60             55              -8.3%
Win Rate                            60.00%         55.00%          -8.3%
--------------------------------------------------------------------------------
Net Profit                          $1,440.00      $1,000.00       -30.6%
Gross Profit                        $2,400.00      $1,900.00       -20.8%
Gross Loss                          -$960.00       -$900.00        +6.3%
--------------------------------------------------------------------------------
Profit Factor                       2.50           2.11            -15.6%
Average Win                         $40.00         $34.55          -13.6%
Average Loss                        $24.00         $20.00          -16.7%
Risk/Reward Ratio                   1.67           1.73            +3.6%
--------------------------------------------------------------------------------
Max Drawdown                        $200.00        $350.00         +75.0%
Max Drawdown %                      3.85%          6.54%           +69.9%
Largest Win                         $120.00        $115.60         -3.7%
Largest Loss                        -$80.00        -$84.40         +5.5%
--------------------------------------------------------------------------------
Sharpe Ratio (Per-Trade)            2.15           1.68            -21.9%
Sharpe Ratio (Daily)                1.95           1.52            -22.1%
Annualized Sharpe                   30.68          23.91           -22.1%
Calmar Ratio                        7.82           4.61            -41.0%
--------------------------------------------------------------------------------
Avg Holding Time (bars)             150            150             -
Trading Days                        20             20              -
Avg Trades per Day                  5.00           5.00            -
================================================================================

                        DIRECTION BREAKDOWN
--------------------------------------------------------------------------------
                        Long Trades (60)              Short Trades (40)
--------------------------------------------------------------------------------
Win Rate                55.00%                        55.00%
Net Profit              $600.00                       $400.00
Profit Factor           2.20                          2.00
Sharpe (Per-Trade)      1.80                          1.50
Max Drawdown %          5.00%                         8.00%
================================================================================

                        SESSION BREAKDOWN
--------------------------------------------------------------------------------
                        Asian          European       US
--------------------------------------------------------------------------------
Total Trades            20             35             45
Win Rate                50.00%         60.00%         53.33%
Net Profit              $100.00        $600.00        $300.00
Profit Factor           1.50           2.80           2.00
Sharpe (Per-Trade)      1.20           2.10           1.60
Max Drawdown %          10.00%         4.00%          7.00%
================================================================================

Report Generated: 2024-01-31 15:30:00
================================================================================
```

### 6.2 HTML交互式报告（推荐）

**包含内容**：
1. 顶部摘要卡片（关键指标）
2. 收益曲线图（Chart.js）
3. 详细指标表格
4. 分维度对比图表
5. 交易明细表（可排序、筛选）

**技术实现**：
- 生成standalone HTML文件
- 嵌入Chart.js CDN
- 使用Bootstrap美化

---

## 七、实现优先级

### Phase 1：核心计算引擎（必须）
1. ✅ 手续费配置参数
2. ✅ 基础统计指标计算
3. ✅ 回撤指标计算
4. ✅ Sharpe比率计算（按笔+按天）
5. ✅ Calmar比率计算

### Phase 2：分维度分析（必须）
6. ✅ Long/Short分析
7. ✅ 手续费对比（0费 vs 正常）
8. ✅ 时段分析（亚/欧/美）

### Phase 3：报告生成（必须）
9. ✅ 控制台文本报告
10. ⭐ HTML交互式报告（推荐）
11. ✅ CSV详细数据导出

### Phase 4：可视化（可选）
12. ⭐ 收益曲线绘制（HTML/Chart.js）
13. ⭐ 回撤曲线绘制
14. ⭐ 月度/周度收益热力图

---

## 八、技术实现要点

### 8.1 数据源

**输入**：CSV导出的交易记录

**必需字段**：
- `OpenBarOpenTime`：用于时段判断、日期聚合
- `Direction`：用于Long/Short分析
- `ActualPnL`：原始盈亏（价格差）
- `IsExecuted`：过滤未成交交易
- `HoldingBars`：持仓时间
- `TickSize`：计算PnL美元价值

### 8.2 计算流程

```csharp
// 1. 加载CSV数据
var trades = LoadTradesFromCSV(csvPath);

// 2. 过滤已成交
var executedTrades = trades.Where(t => t.IsExecuted).ToList();

// 3. 计算每笔PnL（美元）
foreach (var trade in executedTrades)
{
    trade.PnLDollars = trade.ActualPnL / trade.TickSize * TickValue;
    trade.PnLWithFee = trade.PnLDollars - CommissionRoundTrip;
    trade.PnLNoFee = trade.PnLDollars;
}

// 4. 计算基础指标
var metrics = CalculateMetrics(executedTrades);

// 5. 分维度分析
var longMetrics = CalculateMetrics(executedTrades.Where(t => t.Direction == "Long"));
var shortMetrics = CalculateMetrics(executedTrades.Where(t => t.Direction == "Short"));

// 6. 分时段分析
var asianTrades = executedTrades.Where(t => IsAsianSession(t.OpenTime));
var europeanTrades = executedTrades.Where(t => IsEuropeanSession(t.OpenTime));
var usTrades = executedTrades.Where(t => IsUSSession(t.OpenTime));

// 7. 生成报告
GenerateReport(metrics, longMetrics, shortMetrics, sessionMetrics);
```

### 8.3 Sharpe计算示例

```csharp
// 方法1：按笔计算
decimal[] pnls = executedTrades.Select(t => t.PnLWithFee).ToArray();
decimal meanPnL = pnls.Average();
decimal stdPnL = CalculateStdDev(pnls);
decimal sharpePerTrade = meanPnL / stdPnL;

// 方法2：按天计算
var dailyPnL = executedTrades
    .GroupBy(t => t.OpenTime.Date)
    .Select(g => g.Sum(t => t.PnLWithFee))
    .ToArray();
decimal meanDaily = dailyPnL.Average();
decimal stdDaily = CalculateStdDev(dailyPnL);
decimal sharpeDaily = meanDaily / stdDaily;
decimal sharpeAnnualized = sharpeDaily * Math.Sqrt(252);
```

---

## 九、UI设计

### 9.1 Analyze按钮

**位置**：
```
[Long] [Short] [Export] [Analyze]
```

**点击行为**：
- 检查是否有已成交的交易
- 如果没有，提示"No executed trades to analyze"
- 如果有，弹出分析选项对话框

### 9.2 分析选项对话框

**选项**：
1. ⚙️ Configure Parameters（配置参数）
   - Tick Value
   - Commission per Side
   - Initial Capital

2. 📊 Generate Console Report（生成控制台报告）
   - 打印到ATAS日志/弹窗

3. 📁 Export Analysis CSV（导出分析CSV）
   - 包含所有计算指标的CSV文件

4. 🌐 Generate HTML Report（生成HTML报告）⭐ 推荐
   - 包含图表和交互式表格

### 9.3 HTML报告预览

**文件名**：
```
PaperTradingAnalysis_{Symbol}_{Date}.html
```

**内容结构**：
```html
<div class="report-header">
  <h1>Paper Trading Analysis - GC</h1>
  <p>Period: 2024-01-01 to 2024-01-31</p>
</div>

<div class="summary-cards">
  <div class="card">Net Profit: $1,000</div>
  <div class="card">Win Rate: 55%</div>
  <div class="card">Sharpe: 1.68</div>
  <div class="card">Max DD: 6.54%</div>
</div>

<div class="equity-curve">
  <canvas id="equityChart"></canvas>
</div>

<div class="metrics-table">
  <table>...</table>
</div>
```

---

## 十、验证与测试

### 10.1 测试数据集

**最小测试集**：
- 10笔交易（5盈5亏）
- 包含Long和Short
- 跨越3个时段
- 跨越2个交易日

**标准测试集**：
- 100笔交易
- 胜率50-60%
- 包含连续盈利和亏损序列

**边界测试**：
- 0笔交易
- 全部盈利
- 全部亏损
- 只有1笔交易

### 10.2 指标验证

**手工验证**：
- 对10笔交易手工计算所有指标
- 与程序输出对比，确保准确

**对比验证**：
- 与专业回测平台（如QuantConnect、Backtrader）对比
- Sharpe、Calmar等指标应该一致

---

## 十一、扩展功能（Future）

### 11.1 高级分析
- 连胜/连亏分析
- 月度/周度收益分布
- 收益热力图
- Monte Carlo模拟

### 11.2 优化建议
- 基于分析结果给出参数优化建议
- 风险警告（回撤过大、Sharpe过低）

### 11.3 对比分析
- 多个策略对比
- 同一策略不同参数对比

---

**文档版本**: v1.0
**状态**: 待实现
**预估工时**: 8-12小时
**优先级**: 高
