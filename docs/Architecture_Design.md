# 交易分析系统架构演进计划

> **文档性质：规划设计**
> 本文档描述的是系统未来的目标架构，而非当前已实施的架构。

**版本**: v2.1 (计划)
**更新日期**: 2025-10-30
**项目**: RemoteIndicatorATAS_standalone
**状态**: 🚧 设计阶段（未实施）

---

## 文档说明

**当前架构（已实施）：**
```
Indicators/
├── PaperTradingHelperV2.cs     (~2100行，交易+分析+报告一体)
├── TradingAnalyzer.cs          (~1800行，分析引擎)
└── TradingReportGenerator.cs   (~500行，HTML报告生成)

特点：三层分离，但仍在同一DLL中，依赖ATAS平台
```

**目标架构（本文档规划）：**
```
完全解耦的独立模块
├── TradingAnalyzer.Core        (独立库，零依赖)
├── TradingReportGenerator.Core (独立库)
└── TradingAnalyzer.CLI         (独立可执行程序)

特点：跨平台、可独立运行、通过JSON数据交互
```

---

## 目录

1. [当前架构现状](#一当前架构现状)
2. [演进目标与愿景](#二演进目标与愿景)
3. [核心设计原则](#三核心设计原则)
4. [目标系统架构](#四目标系统架构)
5. [数据契约设计](#五数据契约设计)
6. [技术实现方案](#六技术实现方案)
7. [实施路径](#七实施路径)

---

## 一、当前架构现状

### 1.1 现有模块划分

**已完成的重构（v2.1）：**
```
PaperTradingHelperV2.cs
  职责：交易模拟、图表绘制、用户交互
  依赖：ATAS平台DLL
  ↓ 内存调用
TradingAnalyzer.cs
  职责：统计计算、热力图生成
  依赖：ATAS平台DLL（通过PaperTradingHelperV2）
  ↓ 内存调用
TradingReportGenerator.cs
  职责：HTML报告生成
  依赖：ATAS平台DLL（通过TradingAnalyzer）
```

**当前特点：**
- ✅ 职责分离清晰（三个类各司其职）
- ✅ 代码模块化（便于维护）
- ❌ 仍在同一DLL中（无法独立运行）
- ❌ 依赖ATAS平台（无法跨平台）
- ❌ 无法离线分析历史数据

### 1.2 现存问题

| 问题 | 影响 | 示例场景 |
|------|------|----------|
| **平台锁定** | 无法在非ATAS环境运行 | 想在Linux服务器上批量分析 |
| **无法离线分析** | 必须在ATAS中操作 | 想对历史CSV进行深度分析 |
| **不支持自动化** | 无法集成到CI/CD | 想每日自动生成报告并邮件 |
| **扩展困难** | 添加新功能需重新编译 | 想添加PDF导出功能 |

---

## 二、演进目标与愿景

### 2.1 演进目标

**从紧耦合到完全解耦：让分析逻辑独立于交易平台**

```
目标架构（待实施）
────────────────────────────────────────────────────
交易执行层 (ATAS)     → trades.json →
                                      ↓
                          分析引擎层 (CLI Tool)
                                      ↓
                          可视化层 (HTML/PDF/Excel)
```

**预期价值：**
- **灵活性** - 分析逻辑将独立于ATAS平台
- **复用性** - 分析引擎可被任何交易系统引用
- **自动化** - 将支持批量处理和CI/CD集成
- **跨平台** - 可在Windows/Linux/Mac上运行

### 2.2 目标使用场景

**场景1：ATAS内实时分析**（向后兼容）
```
交易者在ATAS中模拟交易
    ↓ 点击"生成报告"按钮
快速生成HTML报告（内存调用，秒级）
```

**场景2：离线深度分析**
```
导出历史交易记录为trades.json
    ↓ 使用独立CLI工具
深度分析 + 自定义配置 + 多格式导出
```

**场景3：批量自动化处理**
```
定时任务/GitHub Actions
    ↓ 扫描多个交易记录文件
批量生成报告 → 邮件通知 → 上传云存储
```

**场景4：团队协作分析**
```
团队成员各自导出trades.json
    ↓ 统一的分析标准
横向对比不同交易者的表现
```

---

## 二、核心设计原则

### 2.1 数据是唯一的桥梁

**原则：模块之间只通过标准化数据格式交互，不存在代码依赖**

```
交易引擎 ──[trades.json]──→ 分析引擎 ──[analysis.json]──→ 可视化引擎
(任意平台)                   (标准计算)                    (多格式输出)
```

**好处：**
- 交易平台可替换（ATAS → MT5 → 其他平台）
- 分析引擎可独立演进（无需重新编译ATAS指标）
- 可视化格式可扩展（HTML → PDF → Excel）

### 2.2 零依赖的Core库

**原则：核心分析库不依赖任何平台特定的DLL**

**TradingAnalyzer.Core 的依赖清单：**
- ✅ System.Text.Json（.NET标准库）
- ✅ System.Linq（纯计算）
- ❌ ATAS平台DLL
- ❌ WPF/WinForms
- ❌ 重型第三方库

**好处：**
- 跨平台运行（Linux/Mac/Windows）
- 单元测试简单
- 性能最优（无多余依赖）

### 2.3 两种模式并存

**原则：向后兼容 + 提供高级选项**

| 模式 | 特点 | 适用场景 |
|------|------|----------|
| **Embedded模式** | 内存调用，速度快 | 日常使用，快速反馈 |
| **CLI模式** | 完全解耦，灵活性高 | 自动化、批量处理 |

**实现：**
```csharp
public enum ReportGenerationMode
{
    Embedded,  // 默认：内存调用Core库
    CLI        // 高级：导出JSON + 调用CLI工具
}
```

---

## 四、目标系统架构

### 4.1 计划项目结构

```
RemoteIndicatorATAS_standalone/
│
├── Indicators/
│   └── PaperTradingHelperV2.cs
│       职责：交易模拟 + JSON导出
│       依赖：ATAS平台
│
├── TradingAnalyzer.Core/                (新建)
│   ├── TradingAnalyzer.cs              核心分析引擎
│   ├── Models/
│   │   ├── TradeRecord.cs              标准化交易记录
│   │   ├── AnalysisMetrics.cs          统计指标
│   │   ├── HeatmapData.cs              热力图数据
│   │   └── AnalysisConfig.cs           分析配置
│   ├── Serialization/
│   │   ├── TradeRecordSerializer.cs    JSON序列化
│   │   └── AnalysisResultSerializer.cs
│   └── Validators/
│       └── TradeRecordValidator.cs      数据验证
│   依赖：无（纯.NET标准库）
│
├── TradingReportGenerator.Core/         (新建)
│   ├── Generators/
│   │   ├── HtmlReportGenerator.cs
│   │   ├── PdfReportGenerator.cs       (未来)
│   │   └── ExcelReportGenerator.cs     (未来)
│   └── Templates/
│       └── HtmlReportTemplate.txt
│   依赖：TradingAnalyzer.Core
│
├── TradingAnalyzer.CLI/                 (新建)
│   ├── Program.cs                       命令行入口
│   ├── Commands/
│   │   ├── AnalyzeCommand.cs           分析命令
│   │   ├── ReportCommand.cs            报告命令
│   │   └── BatchCommand.cs             批量处理
│   └── TradingAnalyzer.CLI.csproj
│   依赖：TradingAnalyzer.Core + TradingReportGenerator.Core
│
└── TradingAnalyzer.Desktop/             (未来)
    └── MainWindow.xaml                  WPF桌面GUI
```

### 3.2 依赖关系图

```
┌─────────────────────────────────────────────────┐
│  PaperTradingHelperV2 (ATAS Indicator)         │
│  - 模拟交易                                      │
│  - 导出trades.json                              │
│  - [可选] 调用Core库快速生成报告                 │
└─────────────┬───────────────────────────────────┘
              │ 可选依赖（Embedded模式）
              ↓
┌─────────────────────────────────────────────────┐
│  TradingAnalyzer.Core (零依赖)                  │
│  - 读取trades.json                              │
│  - 统计计算（Sharpe/MAE/MFE/Kelly等）           │
│  - 生成热力图数据                                │
│  - 输出analysis.json                            │
└─────────────┬───────────────────────────────────┘
              │
              ↓
┌─────────────────────────────────────────────────┐
│  TradingReportGenerator.Core                    │
│  - 读取analysis.json                            │
│  - 生成HTML/PDF/Excel报告                       │
└─────────────┬───────────────────────────────────┘
              │
              ↓
┌─────────────────────────────────────────────────┐
│  TradingAnalyzer.CLI (独立可执行程序)           │
│  - 命令行接口                                    │
│  - 批量处理                                      │
│  - 自动化友好                                    │
└─────────────────────────────────────────────────┘
```

### 3.3 数据流设计

**完整数据流：**
```
交易执行 → 数据采集 → 标准化 → 分析计算 → 结果存储 → 多格式可视化
   ↓           ↓          ↓          ↓          ↓            ↓
ATAS     TradeRecord  trades.json  Analyzer  analysis.json  HTML/PDF
```

**关键环节：**
1. **TradeRecord** - 内存数据结构（C#类）
2. **trades.json** - 持久化交换格式（跨平台）
3. **AnalysisMetrics** - 分析结果（C#类）
4. **analysis.json** - 中间结果（可调试、可缓存）
5. **report.html** - 最终报告（可分享）

---

## 五、数据契约设计（计划）

### 5.1 trades.json（交易记录格式设计）

**计划的Schema定义：**
```json
{
  "version": "1.0",
  "metadata": {
    "symbol": "GC",
    "exportTime": "2025-01-15T12:00:00Z",
    "source": "ATAS PaperTradingHelperV2",
    "timeZone": "UTC"
  },
  "config": {
    "tickValue": 0.1,
    "commissionPerSide": 2.5,
    "initialCapital": 10000.0
  },
  "trades": [
    {
      "id": 1,
      "openTime": "2025-01-15T09:30:00Z",
      "closeTime": "2025-01-15T10:15:00Z",
      "direction": "Long",
      "entryPrice": 2050.5,
      "exitPrice": 2055.3,
      "quantity": 1,
      "pnl": {
        "noFee": 48.0,
        "withFee": 43.0,
        "fee": 5.0
      },
      "levels": {
        "stopLoss": 2045.0,
        "takeProfit": 2060.0
      },
      "execution": {
        "mae": -2.5,
        "mfe": 6.8,
        "holdingMinutes": 45
      }
    }
  ]
}
```

**字段说明：**
| 字段路径 | 类型 | 必填 | 说明 |
|---------|------|------|------|
| version | string | ✅ | 数据格式版本（语义化版本） |
| metadata.symbol | string | ✅ | 交易品种 |
| metadata.exportTime | ISO8601 | ✅ | 导出时间 |
| metadata.timeZone | string | ✅ | 时区（固定UTC） |
| config.tickValue | decimal | ✅ | 每跳价值 |
| config.commissionPerSide | decimal | ✅ | 单边手续费 |
| trades[].openTime | ISO8601 | ✅ | 开仓时间（UTC） |
| trades[].direction | enum | ✅ | Long/Short |
| trades[].execution.mae | decimal | ✅ | 最大不利偏移 |
| trades[].execution.mfe | decimal | ✅ | 最大有利偏移 |

### 5.2 analysis.json（分析结果格式设计）

**计划的Schema定义：**
```json
{
  "version": "1.0",
  "timestamp": "2025-01-15T12:00:00Z",
  "input": {
    "file": "trades.json",
    "hash": "sha256:abc123..."
  },
  "summary": {
    "totalTrades": 50,
    "winningTrades": 28,
    "winRate": 56.0,
    "profitFactor": 1.85,
    "netProfit": 1250.0,
    "netProfitPct": 12.5,
    "maxDrawdown": -350.0,
    "maxDrawdownPct": -3.5
  },
  "metrics": {
    "sharpe": {
      "qualityRatio": 0.85,
      "dailySharpe": 1.42,
      "annualizedSharpe": 2.15
    },
    "risk": {
      "kellyPercent": 12.5,
      "riskOfRuin": 0.002,
      "calmarRatio": 3.57
    },
    "execution": {
      "avgMae": -15.2,
      "avgMfe": 35.6,
      "mfeRealizationRate": 0.58,
      "maeViolationRate": 0.12
    }
  },
  "heatmaps": {
    "entryHolding": {
      "timeSlots": ["00:00-02:00", "02:00-04:00", ...],
      "holdingBuckets": ["<15min", "15-30min", ...],
      "data": {
        "avgPnL": [[10.5, -5.2, ...], ...],
        "tradeCounts": [[5, 8, ...], ...],
        "winRates": [[60.0, 45.0, ...], ...]
      }
    },
    "sessionDay": {
      "daysOfWeek": ["Mon", "Tue", ...],
      "sessions": ["Asian (00:00-08:00)", ...],
      "data": {
        "avgPnL": [[15.0, -8.0, ...], ...],
        "tradeCounts": [[10, 5, ...], ...],
        "winRates": [[65.0, 40.0, ...], ...]
      }
    }
  },
  "warnings": [
    {
      "code": "MAE_VIOLATION_HIGH",
      "message": "MAE violation rate is 18%, consider widening stop loss",
      "severity": "warning"
    }
  ]
}
```

### 4.3 数据验证层次

**三层验证策略：**

**Layer 1: Schema验证（格式）**
```csharp
// 使用JSON Schema验证
- 字段类型是否正确（string/decimal/datetime）
- 必填字段是否存在
- 枚举值是否合法（Long/Short）
```

**Layer 2: 逻辑验证（一致性）**
```csharp
// 业务逻辑验证
- OpenTime < CloseTime
- Price > 0
- Quantity > 0
- HoldingMinutes = (CloseTime - OpenTime).TotalMinutes
- PnL计算正确性
```

**Layer 3: 业务验证（合理性）**
```csharp
// 交易合理性验证
- 多头：MAE <= 0, MFE >= 0
- 空头：MAE >= 0, MFE <= 0
- HoldingMinutes < 24h（日内交易）
- 价格变动 < 10%（防止异常数据）
- |MAE| <= |StopLoss - EntryPrice|（逻辑一致）
```

**错误处理：**
```json
{
  "validationErrors": [
    {
      "tradeId": 25,
      "field": "openTime",
      "error": "OpenTime (2025-01-15T10:00:00Z) is after CloseTime (2025-01-15T09:00:00Z)",
      "severity": "error"
    },
    {
      "tradeId": 30,
      "field": "holdingMinutes",
      "error": "HoldingMinutes (1450) exceeds 24 hours, not typical for intraday trading",
      "severity": "warning"
    }
  ]
}
```

---

## 五、技术实现方案

### 5.1 CLI命令设计

**命令行接口规范：**

```bash
# 一站式：从交易记录到HTML报告
TradingAnalyzer.CLI analyze trades.json --output report.html

# 分步式：先分析（保存中间结果）
TradingAnalyzer.CLI analyze trades.json --save-analysis analysis.json

# 分步式：从分析结果生成报告
TradingAnalyzer.CLI report analysis.json --output report.html --theme dark

# 批量处理：分析多个交易记录
TradingAnalyzer.CLI batch ./trades/*.json --output-dir ./reports/

# 支持CSV直接导入
TradingAnalyzer.CLI analyze trades.csv --format csv --output report.html

# 配置文件支持（YAML/JSON）
TradingAnalyzer.CLI analyze trades.json --config my_analysis.yml

# 验证模式：检查数据有效性
TradingAnalyzer.CLI validate trades.json --verbose

# 转换格式
TradingAnalyzer.CLI convert trades.csv --to json --output trades.json
```

**退出码规范：**
```csharp
public enum ExitCode
{
    Success = 0,                  // 成功
    InvalidArguments = 1,         // 命令行参数错误
    InvalidData = 2,              // 数据验证失败
    AnalysisError = 3,            // 分析计算错误
    ReportGenerationError = 4,    // 报告生成错误
    FileIOError = 5               // 文件读写错误
}
```

### 5.2 配置文件设计

**analysis_config.yml：**
```yaml
# 分析配置
analysis:
  # 日内交易定义
  intradayMaxHours: 24

  # 风险参数
  riskFreeRate: 0.03  # 3% 无风险利率

  # 过度交易阈值
  overtradingThreshold: 15

  # 复仇交易窗口（分钟）
  revengeTradeWindow: 5

  # 热力图配置
  heatmaps:
    entryHolding:
      timeSlotHours: 2  # 2小时间隔
      holdingBuckets:
        - label: "<15min"
          maxMinutes: 15
        - label: "15-30min"
          maxMinutes: 30
        - label: "30min-1h"
          maxMinutes: 60
        - label: "1h-2h"
          maxMinutes: 120
        - label: "2h-4h"
          maxMinutes: 240
        - label: ">4h"
          maxMinutes: null

# 报告配置
report:
  format: html
  theme: light  # light/dark
  includeCharts: true
  includeHeatmaps: true

  # HTML特定配置
  html:
    responsive: true
    chartLibrary: "chart.js"  # chart.js/plotly

# 输出配置
output:
  saveAnalysis: true
  analysisFile: "analysis.json"
  reportFile: "report.html"
  verbose: false
```

### 5.3 性能优化策略

**大数据集处理：流式解析**
```csharp
// 避免一次性加载全部数据到内存
public async Task<AnalysisResult> AnalyzeStreamAsync(Stream jsonStream)
{
    using var reader = new StreamReader(jsonStream);
    using var jsonReader = new Utf8JsonStreamReader(reader.BaseStream);

    // 逐条解析交易记录
    await foreach (var trade in DeserializeTradesAsync(jsonReader))
    {
        // 增量更新统计指标
        UpdateMetrics(trade);
    }

    return ComputeFinalMetrics();
}
```

**并行处理：批量分析**
```csharp
// 批量处理多个文件，利用多核CPU
public async Task<BatchResult> BatchAnalyzeAsync(string[] files)
{
    var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

    await Parallel.ForEachAsync(files, options, async (file, ct) =>
    {
        var result = await AnalyzeFileAsync(file, ct);
        // 保存结果
    });
}
```

**缓存机制：避免重复计算**
```csharp
// 缓存中间结果
public class AnalysisCache
{
    private readonly Dictionary<string, AnalysisResult> _cache = new();

    public AnalysisResult GetOrCompute(string fileHash, Func<AnalysisResult> compute)
    {
        if (_cache.TryGetValue(fileHash, out var cached))
        {
            return cached;
        }

        var result = compute();
        _cache[fileHash] = result;
        return result;
    }
}
```

### 5.4 跨平台兼容性

**路径处理：**
```csharp
// ✅ 正确：使用跨平台API
var outputPath = Path.Combine(outputDir, "report.html");

// ❌ 错误：硬编码路径分隔符
var outputPath = outputDir + "\\report.html";  // 在Linux上失败
```

**换行符处理：**
```csharp
// ✅ 正确：使用Environment.NewLine
sb.AppendLine($"Total Trades: {count}");

// ❌ 错误：硬编码\r\n
sb.Append($"Total Trades: {count}\r\n");  // 在Linux上可能显示异常
```

**字符编码：**
```csharp
// ✅ 正确：显式指定UTF-8
File.WriteAllText(path, content, Encoding.UTF8);

// ❌ 错误：使用默认编码（可能是GBK）
File.WriteAllText(path, content);
```

### 5.5 扩展性设计

**插件式报告生成器：**
```csharp
// 接口定义
public interface IReportGenerator
{
    string Format { get; }  // "html", "pdf", "excel"
    string[] SupportedExtensions { get; }
    void Generate(AnalysisResult result, string outputPath, ReportConfig config);
}

// 注册机制
public class ReportGeneratorRegistry
{
    private readonly Dictionary<string, IReportGenerator> _generators = new();

    public void Register(IReportGenerator generator)
    {
        _generators[generator.Format] = generator;
    }

    public IReportGenerator Get(string format)
    {
        return _generators[format];
    }
}

// 使用
var registry = new ReportGeneratorRegistry();
registry.Register(new HtmlReportGenerator());
registry.Register(new PdfReportGenerator());
registry.Register(new ExcelReportGenerator());

// 根据输出文件扩展名自动选择
var generator = registry.Get(Path.GetExtension(outputPath));
generator.Generate(analysisResult, outputPath, config);
```

---

## 六、实施路径

### Phase 1: 数据契约定义（预计1-2天）

**目标：定义清晰的数据交换格式**

- [ ] 设计trades.json JSON Schema
- [ ] 设计analysis.json JSON Schema
- [ ] 编写Schema验证工具
- [ ] 创建示例数据文件（用于测试）
- [ ] 文档：数据格式规范

**验收标准：**
- JSON Schema验证器可正确识别格式错误
- 示例数据通过所有三层验证

### Phase 2: Core库提取（预计2-3天）

**目标：将分析逻辑提取为独立库**

- [ ] 创建TradingAnalyzer.Core项目（.NET Standard 2.1）
- [ ] 迁移TradingAnalyzer类（移除ATAS依赖）
- [ ] 实现TradeRecordSerializer（JSON序列化）
- [ ] 实现TradeRecordValidator（三层验证）
- [ ] 创建TradingReportGenerator.Core项目
- [ ] 迁移报告生成逻辑（独立模块）
- [ ] 单元测试覆盖率 > 80%

**验收标准：**
- Core库零外部依赖（仅依赖.NET标准库）
- 单元测试全部通过
- 可在Linux上编译运行

### Phase 3: CLI工具构建（预计2-3天）

**目标：创建独立的命令行分析工具**

- [ ] 创建TradingAnalyzer.CLI项目
- [ ] 实现analyze命令（trades.json → analysis.json）
- [ ] 实现report命令（analysis.json → report.html）
- [ ] 实现batch命令（批量处理）
- [ ] 实现validate命令（数据验证）
- [ ] 集成测试（端到端）
- [ ] 打包为单文件可执行程序（self-contained）

**验收标准：**
- CLI可在无.NET运行时环境下运行
- 所有命令功能正常
- 友好的错误提示和帮助文档

### Phase 4: ATAS集成（预计1-2天）

**目标：保持ATAS一键生成功能，同时支持JSON导出**

- [ ] PaperTradingHelperV2添加JSON导出功能
- [ ] 实现两种模式切换（Embedded/CLI）
- [ ] 测试Embedded模式（内存调用Core库）
- [ ] 测试CLI模式（导出JSON + 调用CLI工具）
- [ ] 向后兼容性测试

**验收标准：**
- 现有用户体验不变（默认Embedded模式）
- JSON导出功能正常工作
- CLI模式生成的报告与Embedded模式一致

### Phase 5: 文档和发布（预计1天）

**目标：完善文档，发布工具**

- [ ] 编写CLI使用文档（README.md）
- [ ] 更新架构文档
- [ ] 创建示例数据和配置文件
- [ ] 发布NuGet包（TradingAnalyzer.Core）
- [ ] 发布GitHub Release（CLI工具二进制）
- [ ] 编写迁移指南（从旧版升级）

**验收标准：**
- 文档完整、示例可运行
- NuGet包可正常引用
- CLI工具可直接下载使用

### Phase 6: 未来扩展（可选）

**桌面GUI（WPF/Avalonia）**
- 可视化数据导入、分析、报告生成
- 图表交互（缩放、筛选、导出）

**Web服务（ASP.NET Core）**
- RESTful API接口
- 团队协作功能
- 云端存储

**多格式导出**
- PDF报告（使用QuestPDF或iTextSharp）
- Excel报告（使用EPPlus或ClosedXML）
- Markdown报告（用于文档系统）

---

## 七、技术栈总结

| 组件 | 技术选型 | 理由 |
|------|---------|------|
| **Core库** | .NET Standard 2.1 | 跨平台兼容 |
| **序列化** | System.Text.Json | 高性能、零依赖 |
| **CLI框架** | System.CommandLine | 官方库、功能完善 |
| **HTML图表** | Chart.js + Plotly.js | 轻量、交互性好 |
| **单元测试** | xUnit + FluentAssertions | .NET社区标准 |
| **打包** | dotnet publish (self-contained) | 单文件分发 |

---

## 八、关键决策记录

### 决策1：为什么选择JSON而不是CSV？

**考虑因素：**
- CSV：简单、Excel友好，但结构扁平、无类型信息
- JSON：结构化、类型明确、易于扩展

**决策：JSON作为主要格式，同时支持CSV导入**

**理由：**
- JSON可表达嵌套结构（如metadata、config、execution）
- 类型安全（decimal vs string）
- 易于版本管理（version字段）
- CSV作为辅助输入格式（通过转换工具）

### 决策2：为什么需要analysis.json中间格式？

**考虑因素：**
- 直接从trades.json生成报告更简洁
- 中间格式增加复杂性

**决策：保留analysis.json中间格式**

**理由：**
1. **可调试性** - 分析结果可独立检查
2. **性能** - 大数据集可缓存分析结果，避免重复计算
3. **灵活性** - 同一分析结果可生成多种格式报告
4. **协作** - 分析结果可分享给团队成员

### 决策3：为什么不使用数据库？

**考虑因素：**
- 数据库可持久化、支持复杂查询
- JSON文件简单、易于分享

**决策：当前阶段使用JSON文件，未来可扩展数据库支持**

**理由：**
- 日内交易数据量不大（通常<1000笔/月）
- 文件模式更灵活（版本控制、云存储、邮件分享）
- 零运维成本
- 未来可添加数据库导入功能（不影响现有架构）

---

**文档维护者**: Claude (Anthropic)
**最后更新**: 2025-10-30
