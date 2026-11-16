# 性能基准实测报告 (BenchmarkDotNet)

> 生成日期: 2026-11-17 · 任务来源: `.workbuddy/inbox/TASK-003.md`
> 本文所有数字均直接取自 `BenchmarkDotNet.Artifacts/results/` 下的原始产物，未做任何手工修改；
> 标注"换算"的列由原始数字除法计算得出（公式随列注明）。

## 1. 目的与结论摘要

把 README 中的性能表述变成可复现的实测数据。本报告覆盖两组基准：**LTTB 降采样**（`LttbDownsampler.Downsample`）与 **SQLite 遥测写入**（单条提交 vs 批量事务）。

1. **LTTB 降采样耗时与输入规模近似线性**：1 万点约 0.18 ms，100 万点约 13.8 ms（约 14–18 ns/点），100 万点压缩到 1000 点的单次耗时在此机器上为 **13.8 ms**。它适合"加载历史趋势时执行一次"，不适合逐帧执行——60 FPS 的单帧预算（16.7 ms）会被百万点降采样本身占满。
2. **LTTB 每次调用仅分配 7.87 KB，且与输入规模无关**（即输出列表自身）。"近零分配"成立，但不是字面意义的"零分配"。
3. **SQLite 批量事务收益显著**：WAL + synchronous=NORMAL 下，逐条提交约 **1.8 万行/秒**；每事务 50 行提升至约 **19.6 万行/秒（10.9×）**；每事务 500 行约 **33.0 万行/秒（18.3×）**。50 → 500 仍有提升但边际递减（1.7×）。
4. **按应用当前批量参数（50 条/批），写入吞吐余量巨大**：约 19.6 万行/秒 的实测吞吐，相对单设备 1 Hz 遥测（1 行/秒）有约 5 个数量级的余量。
5. **`--job short` 仅 3 次测量迭代，置信区间偏宽**（如 batch=500 的 Error ±166 ms 已大于 Mean），因此结论以数量级和倍数关系为准；追加的 15 次迭代复测与 3 次迭代结果趋势一致（见 §5.3）。

## 2. 测试环境

| 项目 | 值 |
| --- | --- |
| 机型 | TIMI Redmi G 2022（笔记本） |
| CPU | 12th Gen Intel(R) Core(TM) i7-12650H，10 核 / 16 逻辑处理器（基频 2.3 GHz，BDN 报告运行频率 2.69 GHz） |
| 内存 | 16 GB（BDN 报告 15.78 GB Total） |
| 操作系统 | Windows 11 Pro 23H2，Build 22631（BDN 报告 10.0.22631.6199） |
| 电源方案 | 高性能（GUID 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c） |
| .NET SDK | 11.0.100 |
| 执行运行时 | **.NET 11.0.0**（X64 RyuJIT x86-64-v3，GC = Concurrent Workstation）¹ |
| BenchmarkDotNet | v0.16.0-preview.2² |
| 测试日期 | 2026-11-17 |

¹ 目标框架为 `net8.0-windows`，但本机环境变量 `DOTNET_ROLL_FORWARD=LatestMajor` 使进程前滚到 .NET 11.0.0。复现时若移除该变量，进程将运行在 .NET 8 上，数字可能有差异。
² 选择 preview 版的原因：稳定版 0.15.8 在装有 .NET 11 SDK 的机器上触发 `GetRuntimeVersion not implemented for NotRecognized` 崩溃（上游问题 [dotnet/BenchmarkDotNet#3172](https://github.com/dotnet/BenchmarkDotNet/issues/3172)），0.16.0-preview.1 起修复。

## 3. 方法

### 3.1 BDN 配置

- 全部基准类标注 `[MemoryDiagnoser]`；不带 Job attribute，运行时通过 CLI 传入 `--job short`。
- `--job short` 实际生效配置（BDN 输出原文）：`ShortRun(InvocationCount=1, IterationCount=3, LaunchCount=1, UnrollFactor=1, WarmupCount=3)`，RunStrategy=Throughput。
- **IterationCount = 3**（short job 的固有设置，这是误差偏大的直接原因）。

### 3.2 LTTB 降采样基准

- 输入点数 `[Params(10_000, 100_000, 1_000_000)]`，目标点数固定 1000。
- 数据在 `[GlobalSetup]` 中生成，**不计入被测耗时**：时间戳每点 +100 ms 递增；温度为固定种子（42）的确定性伪随机游走（15–35 °C）；湿度同理（20–80 %）。
- 被测方法：`LttbDownsampler.Downsample(data, 1000)`。
- 正确性校验：`--verify` 模式实测三种规模均**精确返回 1000 条**，且首尾点保留（输出：`PASSED`）。

### 3.3 SQLite 写入基准

- 每轮（每个 operation）写入固定 **10,000 行**；`[Params(1, 50, 500)]` 为每事务批量行数（1 = 逐条提交）。
- 临时库：`%TEMP%\SmartEdgeHMI_Benchmarks\telemetry_bench.db`；`PRAGMA journal_mode=WAL`、`PRAGMA synchronous=NORMAL`、`PRAGMA temp_store=MEMORY`（每次打开新连接后重新声明——`synchronous` 是连接级设置，不随库文件持久化）。
- 表结构与索引（`TelemetryHistory` + `idx_telemetry_timestamp` + `idx_telemetry_device`）与 `SqliteConnectionFactory.InitializeDatabaseAsync` 保持一致。
- `[IterationSetup]` 在每轮前执行 `DELETE FROM TelemetryHistory` 清表，保证三种批量参数可比（short job 下每轮只含 1 次调用，`UnrollFactor=1`，故每万行写入面对的都是空表）。
- **被测范围** = 打开连接 → PRAGMA → 按批次开启事务 → 参数化插入 → 提交。时间戳字符串化、行数据准备均在 `[GlobalSetup]` 预完成，不计入被测耗时。

### 3.4 执行时间

- 全量 short job（6 个基准）：**54.26 s**。
- SQLite 15 次迭代复测（3 个基准，见 §5.3）：36.3 s。

## 4. 原始数据

### 4.1 LTTB 降采样（原文复制自 `results/SmartEdgeHMI.Benchmarks.LttbDownsampleBenchmarks-report-github.md`）

| Method           | N       | Mean        | Error       | StdDev    | Gen0   | Allocated |
|----------------- |-------- |------------:|------------:|---------:|-------:|----------:|
| **DownsampleTo1000** | **10000**   |    **179.2 μs** |    **34.48 μs** |   **1.89 μs** | **0.4883** |   **7.87 KB** |
| **DownsampleTo1000** | **100000**  |  **1,615.9 μs** |   **153.68 μs** |   **8.42 μs** |      **-** |   **7.87 KB** |
| **DownsampleTo1000** | **1000000** | **13,814.6 μs** | **2,482.01 μs** | **136.05 μs** |      **-** |   **7.87 KB** |

换算（Mean ÷ 输入点数）：

| N | 平均每点耗时 |
| --- | --- |
| 10,000 | 17.9 ns/点 |
| 100,000 | 16.2 ns/点 |
| 1,000,000 | 13.8 ns/点 |

### 4.2 SQLite 写入 10,000 行（原文复制自 `results/SmartEdgeHMI.Benchmarks.SqliteInsertBenchmarks-report-github.md`）

| Method                          | BatchSize | Mean      | Error     | StdDev    | Gen0      | Allocated |
|-------------------------------- |---------- |----------:|----------:|----------:|----------:|----------:|
| **WriteTelemetryHistory_10000Rows** | **1**         | **554.97 ms** |  **60.50 ms** | **3.316 ms** | **2000.0000** |  **31.36 MB** |
| **WriteTelemetryHistory_10000Rows** | **50**        |  **51.06 ms** | **117.17 ms** | **6.423 ms** |         **-** |   **9.45 MB** |
| **WriteTelemetryHistory_10000Rows** | **500**       |  **30.30 ms** | **166.09 ms** | **9.104 ms** |         **-** |   **9.05 MB** |

换算行/秒（10,000 ÷ Mean）与加速比（相对 batch=1）：

| 每事务批量行数 | 平均耗时 | 行/秒（换算） | 相对逐条提交 |
| ---: | ---: | ---: | ---: |
| 1 | 554.97 ms | 18,019 | 1× |
| 50 | 51.06 ms | 195,848 | 10.9× |
| 500 | 30.30 ms | 330,033 | 18.3× |

### 4.3 补充：SQLite 15 次迭代复测（非任务要求，用于评估 short job 稳定性）

配置：`--filter "*SqliteInsertBenchmarks*" --iterationCount 15 --warmupCount 3`（其余同上），
原文见 `BenchmarkDotNet.Artifacts/benchmark-run-sqlite-i15.log`：

| BatchSize | Mean | Error | StdDev | 行/秒（换算） |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 552.24 ms | 4.947 ms | 4.131 ms | 18,109 |
| 50 | 50.93 ms | 3.804 ms | 3.558 ms | 196,348 |
| 500 | 28.70 ms | 4.064 ms | 3.801 ms | 348,432 |

与 3 次迭代的 short job 相比趋势一致，且置信区间收窄一个数量级——两组数据互为印证。

## 5. 结论边界（明确未测量的内容）

以下内容本次**均未测量**，任何相关表述都应视为推算或上限，不得写成实测：

- **ScottPlot 实际渲染帧率**：本报告只测了 LTTB 降采样的 CPU 耗时，未测 ScottPlot 5.x 的 UI/GPU 渲染路径。README 的 "60FPS 极速渲染" **只能作为推算/上限**，不是实测值。补充推算：1M 点降采样耗时 13.8 ms，已接近 60 FPS 单帧预算 16.7 ms，说明"逐帧对百万点做降采样"不可行；应用实际只在加载历史时执行一次降采样，故该推算不构成对 60 FPS 的否定。
- **UI 线程调度**：Dispatcher 优先级、ObservableCollection 批量添加（`Utils/UI`）的耗时均未测。
- **真实串口吞吐**：115200 波特率串口（约 11.5 KB/s 物理上限）的实际读写路径未测。
- **磁盘耐久性**：WAL checkpoint 频率、SSD 写放大与寿命影响未测。
- **`synchronous=FULL` 下的写入吞吐**：见 §6 第 4 条——已由 TASK-005 修复：连接工厂对每个新连接设置 `synchronous=NORMAL`，修复后应用口径与本基准一致；当前代码已是 NORMAL。修复前应用新连接运行在默认 FULL 模式，该模式吞吐未测（保留作为历史口径）。
- **协议解析吞吐**（JSON-Lines / Modbus RTU）、**报警直写路径**、**Watchdog 心跳延迟**均未测。
- **.NET 8 运行时数字**：查询三组已在 .NET 8.0.34 复测（见 §8.5），结论与 .NET 11 口径一致；LTTB 与 SQLite 写入两组仍只有 .NET 11 口径（见 §2 注 1）。
- **查询基准的规模上界**：§8 的对照数据为 20 万行遥测、5000 行报警。30 天满负荷（1 Hz，约 260 万行）下三种查询的耗时未实测；已知趋势查询耗时随行数线性增长、内存保持 KB 级，报警上下文受"报警条数 × 窗口行数"约束。
- **数据保留策略的长期收敛**：TASK-104 的清理逻辑只做了单轮单元测试（含 1.2 万行分批），"连续运行 24 小时后库文件是否稳定"未实测。

## 6. 与 README 现有表述的差异（建议收敛点）

1. **"保障 ScottPlot 图表 60FPS 极速渲染"**（README 核心特性表）：渲染帧率未实测。建议改为"LTTB 使百万级点降采样到千点级（实测 100 万点约 13.8 ms），图表刷新帧率未做基准测试"之类收敛表述。
2. **"零分配数据通道 / 零分配协议解析"**：README 的"零分配"指协议缓冲复用（`ArrayPool`），本次未测；LTTB 降采样实测每次调用分配 7.87 KB（输出列表），**不是零分配**。建议把"零分配"的适用范围明确限定为协议缓冲复用。
3. **"SQLite WAL 双缓冲……50 条 / 1s 窗口"**：TASK-003 撰写本文时代码实际是 `BatchSize = 50`、`FlushIntervalMs = 10_000`（10 秒，见 `SqliteTelemetryRepository.cs` 第 18–19 行），README 当时的 "1s 窗口" 与代码不符。已由 TASK-005 将 `FlushIntervalMs` 修正为 `1_000`，代码与 README/ROADMAP 声明的 "50 条或 1 秒窗口" 一致。
4. **连接级 PRAGMA 偏差（已由 TASK-005 修复）**：`PRAGMA synchronous` 是连接级设置。修复前应用只在 `SqliteConnectionFactory.InitializeDatabaseAsync` 中设置过一次 `NORMAL`，仓储后续经 `CreateConnectionAsync` 新建的连接实际以 SQLite 默认的 `synchronous=FULL` 运行。已由 TASK-005 修复：连接工厂对每个新连接设置 `synchronous=NORMAL`，修复后应用口径与本基准一致；修复前 FULL 模式吞吐未测（历史口径），当前代码已是 NORMAL。`journal_mode=WAL` 随库文件持久化、不受影响。本基准在每次连接打开后重设 `NORMAL`，实测吞吐代表 WAL+NORMAL 口径。
5. **"tests-39 passed" 徽章**（历史口径）：TASK-004 已删除伪造徽章、改为真实 CI workflow badge；当前测试数量以最近一次 `dotnet test` 实测为准，见 README「工程证据」小节。

## 7. 复现命令与产物路径

```bash
# 全量基准（short job）
dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --job short --filter "*"

# 仅 SQLite 组（提高迭代次数的复测口径）
dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --filter "*SqliteInsertBenchmarks*" --iterationCount 15 --warmupCount 3

# 查询下推三组对照（§8，约 8~10 分钟）
dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --filter "*QueryBenchmarks*"

# LTTB 返回条数校验（三种规模均应精确返回 1000 条）
dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --verify

# 验证三连
dotnet build SmartEdgeHMI.sln -c Release
dotnet test SmartEdgeHMI.sln -c Release --no-build
dotnet format SmartEdgeHMI.sln --verify-no-changes --no-restore
```

## 8. 查询下推基准（TASK-103 / TASK-107 实测）

### 8.1 背景

改动前三个查询接口都是"把表读进内存再处理"：报警历史全表 `SELECT *` + LINQ 过滤；报警上下文把**整张报警表和整张遥测表**读进内存后 `GroupJoin`；趋势查询把时间范围内**全部原始行**读进内存再跑 LTTB。遥测按 1 Hz 采样，30 天约 260 万行，长期运行后这三次调用会直接导致内存与 GC 压力。

TASK-101/102/103 把三者改为 SQL 过滤、按报警窗口逐条查询、SQL 分桶取样；TASK-107 补 `(DeviceId, Timestamp)` 复合索引修掉窗口查询的扫描放大。

### 8.2 基准设计（`SmartEdgeHMI.Benchmarks/QueryBenchmarks.cs`）

| 项 | 值 |
| --- | --- |
| 数据规模 | `TelemetryHistory` 20 万行（最近 30 天均匀分布，2 设备交替）+ `AlarmHistory` 5000 行（2 设备 × 2 编码） |
| 对照方式 | 每组一个 `Legacy_*`（内联改前实现，仅作基线）+ 一个 `New_*`（调用当前仓储方法） |
| 参数 | 报警历史：最近 1 天 / Limit=50；报警上下文：Limit=20 / 窗口 5 分钟；趋势：30 天 / targetPoints=1000 |
| BDN 配置 | `[MemoryDiagnoser]` + `[SimpleJob(warmupCount:1, iterationCount:3)]`，`Job-WWFMMQ` |
| 运行环境 | i7-12650H（10C/16T）、16 GB、Windows 11 23H2、BDN 0.16.0-preview.2；托管运行时 .NET 11.0.0（见 §2 注 1） |

```bash
dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --filter "*QueryBenchmarks*"
```

### 8.3 结果（TASK-107 复测，含复合索引）

| 组 | 方法 | Mean | Allocated | 相对旧实现 |
| --- | --- | ---: | ---: | --- |
| 报警历史 | `Legacy_AlarmHistory` | 8,589.0 us | 1,930.39 KB | 基线 |
| | `New_AlarmHistory` | **118.3 us** | **25.89 KB** | **约 73× 快，分配 -98.7%** |
| 报警上下文 | `Legacy_AlarmContext` | 411,851.8 us | 86,713.07 KB | 基线 |
| | `New_AlarmContext` | **1,111.7 us** | **270.22 KB** | **约 370× 快，分配 -99.7%** |
| 趋势 | `Legacy_Trend` | 382,656.6 us | 80,672.52 KB | 基线 |
| | `New_Trend` | 443,711.9 us | **765.13 KB** | **时间持平（CI ±190 ms，差异在噪声内），分配 -99.1%** |

GC 侧：两个 Legacy 大组每 1000 次操作触发 6000/2000/1000 次 Gen0/Gen1/Gen2；`New_*` 三组均为 0。

**趋势查询没有可测的时间提升**，这点必须如实说明：它的剩余耗时由 SQLite 对 20 万行做 `julianday` 解析与分桶排序决定，与旧实现同量级；改造收益在内存（80 MB → 0.75 MB）与"数据量再涨十倍也不会一次性对象化全部行"。README 中不得写成"趋势查询提速 N 倍"。

### 8.4 报警上下文：1.48× 变慢 → 370× 变快的修正过程
TASK-103 首次基准暴露出新实现反而慢 1.48×：`EXPLAIN QUERY PLAN` 显示窗口查询走的是单列 `idx_telemetry_device`，库里只有 2 台设备，每条查询最多扫约 10 万行，还要为 `ORDER BY Timestamp` 建临时 B 树。TASK-107 追加复合索引 `idx_telemetry_device_timestamp (DeviceId, Timestamp)` 后，计划变为复合索引直接 `SEARCH`、无临时 B 树，单次调用从 591 ms 降到 1.1 ms。

这条记录保留下来，是因为它说明了一个方法论：**查询下推的收益依赖于索引是否匹配查询形态，不做前后基准就无法发现"优化变慢"**。

### 8.5 .NET 8 运行时复测（目标运行时口径）

§8.3 的数字跑在 .NET 11 上（本机 `DOTNET_ROLL_FORWARD=LatestMajor`）。为确认结论不是运行时特性，按项目目标运行时再跑一遍：

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestPatch'   # 锁到 .NET 8 补丁版本, 不跨大版本前滚
dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --filter "*QueryBenchmarks*"
```

Host 与 Job 均为 `.NET 8.0.34 (8.0.34, 8.0.3426.52217), X64 RyuJIT x86-64-v3`：

| 组 | 方法 | Mean（.NET 8） | Allocated（.NET 8） | 相对旧实现 |
| --- | --- | ---: | ---: | --- |
| 报警历史 | `Legacy_AlarmHistory` | 8,483.5 us | 1,933.24 KB | 基线 |
| | `New_AlarmHistory` | **118.4 us** | **25.92 KB** | **约 72× 快，分配 -98.7%** |
| 报警上下文 | `Legacy_AlarmContext` | 487,724.3 us | 86,720.42 KB | 基线 |
| | `New_AlarmContext` | **1,156.2 us** | **270.41 KB** | **约 422× 快，分配 -99.7%** |
| 趋势 | `Legacy_Trend` | 395,922.0 us | 80,673.16 KB | 基线 |
| | `New_Trend` | 426,486.3 us | **765.15 KB** | 时间持平（噪声内），分配 -99.1% |

与 .NET 11 口径（§8.3）逐行比较：绝对耗时差异都在运行间噪声内，收益量级与结论完全一致——**数字可跨运行时复现**，不是某版运行时的偶然结果。

> 注意：`DOTNET_ROLL_FORWARD=Disable` 会因本机只装了 8.0.34（不是 8.0.0）而启动失败，必须用 `LatestPatch`。

## 9. 复合索引的写放大（TASK-107 实测）

新增 `idx_telemetry_device_timestamp` 后 `TelemetryHistory` 有 4 个索引（PK + timestamp + device + 复合），写入需要多维护一棵 B 树：

| BatchSize | 加索引前（§4.2 short job） | 加索引后（15 次迭代） | 变化 | 换算吞吐（加索引后） |
| ---: | ---: | ---: | --- | ---: |
| 1 | 554.97 ms | 673.92 ms | +21.4% | ≈ 14,838 行/秒 |
| 50 | 51.06 ms | 67.58 ms | +32.4% | ≈ **147,973 行/秒** |
| 500 | 30.30 ms | 37.67 ms | +24.3% | ≈ 265,462 行/秒 |

托管分配三档均与加索引前完全一致（31.36 / 9.45 / 9.05 MB），写放大纯粹是 SQLite 端维护索引的成本。

结论：按应用当前 50 条/批的批量参数，写入吞吐仍有约 14.8 万行/秒，相对单设备 1 Hz 遥测（86,400 行/天）余量四个数量级以上；代价可接受。若未来出现高频写入场景，应重新评估该索引是否值得。

```bash
dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --filter "*SqliteInsertBenchmarks*"
```

产物（`BenchmarkDotNet.Artifacts/` 已被 `.gitignore` 忽略，本地留存；关键表格已复制进本文）：

| 产物 | 路径 |
| --- | --- |
| LTTB 结果表 / CSV / HTML | `BenchmarkDotNet.Artifacts/results/SmartEdgeHMI.Benchmarks.LttbDownsampleBenchmarks-report-github.md`（同目录 `-report.csv` / `-report.html`） |
| SQLite 结果表 / CSV / HTML | `BenchmarkDotNet.Artifacts/results/SmartEdgeHMI.Benchmarks.SqliteInsertBenchmarks-report-github.md`（同目录 `-report.csv` / `-report.html`） |
| 查询下推结果表（§8） | `BenchmarkDotNet.Artifacts/results/SmartEdgeHMI.Benchmarks.QueryBenchmarks-report-github.md` |
| 查询/插入基准完整日志（TASK-107） | `BenchmarkDotNet.Artifacts/bench-TASK107.log` |
| 全量运行完整控制台日志 | `BenchmarkDotNet.Artifacts/benchmark-run-short-final.log` |
| SQLite 15 次迭代复测日志 | `BenchmarkDotNet.Artifacts/benchmark-run-sqlite-i15.log` |

基准项目源码：`SmartEdgeHMI.Benchmarks/`（`LttbDownsampleBenchmarks.cs`、`SqliteInsertBenchmarks.cs`、`Program.cs`）。
