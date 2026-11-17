# v1.2 里程碑 · 工程可信度与真实设备验收

> 创建：2026-11-17（TASK-004）｜ 上一发布版本：v1.1.0
> 本文所有性能数字一律引用 [performance-benchmarks.md](performance-benchmarks.md)，不另造数字；
> 测试数量以最近一次 `dotnet test` 实测为准（2026-11-17，Release，48 通过 / 0 失败，TASK-005 实测）。

## 1. 目标与非目标

### 目标

1. 补齐"外部可验证的工程信号"：CI 门禁、真实徽章、LICENSE、集中包版本、性能实测、故障复盘（大部分已就绪，待提交推送）。
2. 完成 ESP8266 真实硬件验收：按可重复模板执行测试矩阵并签字归档。
3. 走通一次正式发布流程：打包、校验和、安装、发布后 smoke、回滚预案（v1.1.0 已发布过 zip，但未按检查表走查）。

### 非目标（明确不做）

- **不做新协议**：不新增 Modbus TCP / CAN / MQTT 等协议栈，JSON-Lines 与 Modbus RTU 双协议保持现状。
- **不做云平台**：不上云、不加远程遥测 / OTA / 设备管理后台。
- **不重构架构**：分层、DI 注册、协议管道、存储模型保持现状，不为"更优雅"动承重结构。
- **不为覆盖率硬补 WPF UI 测试**：现有协议层 / 状态机测试的叙事已足够，不凑数字。

## 2. 已完成清单（只列有证据的事项）

| # | 事项 | 证据链接 |
| --- | --- | --- |
| 1 | CI workflow：restore → build(Release) → test → format 四步门禁（windows-latest） | [.github/workflows/ci.yml](../.github/workflows/ci.yml) |
| 2 | LICENSE（MIT 全文），与 README 徽章声明一致 | [LICENSE](../LICENSE) |
| 3 | format 门禁：`dotnet format --verify-no-changes` 退出码 0（2026-11-17 实测） | TASK-003 报告 §4；CI 第 4 步同口径 |
| 4 | 单元测试 48 通过 / 0 失败（2026-11-17，`dotnet test -c Release` 实测；测试方法 48 个：状态机 17 + JSON 8 + Modbus 11 + CRC 3 + 串口传输 1 + LTTB 5 + Task 工具 1 + SQLite 数据层 2） | TASK-003 报告 §4 · TASK-005 报告 |
| 5 | LTTB 降采样单元测试（5 个，覆盖规模 / 首尾保留 / 边界） | [SmartEdgeHMI.Tests/Utils/LttbDownsamplerTests.cs](../SmartEdgeHMI.Tests/Utils/LttbDownsamplerTests.cs) |
| 6 | 串口打开失败 / 关闭竞态修复：Token 缓存 + fire-and-forget 异常观察器（提交 `30390aa`） | [postmortem-serial-dispose.md](postmortem-serial-dispose.md) |
| 7 | 性能基准（BenchmarkDotNet 实测：LTTB 100 万点→1000 点 13.8 ms / 7.87 KB；SQLite WAL+NORMAL 批量事务 50/批 ≈19.6 万行/秒） | [performance-benchmarks.md](performance-benchmarks.md) |
| 8 | 串口故障复盘（现象 → 根因 → 修复 → 防复发 → 残余风险） | [postmortem-serial-dispose.md](postmortem-serial-dispose.md) |
| 9 | GIF 归档：4 个演示动图移入 `docs/assets/`，英文 kebab-case 命名，README 引用同步修正 | [assets/](assets/) |
| 10 | 包版本集中管理：`Directory.Build.props`（公共属性 + CI 标记）+ `Directory.Packages.props`（集中包版本） | [Directory.Build.props](../Directory.Build.props) · [Directory.Packages.props](../Directory.Packages.props) |
| 11 | README 收敛：伪造徽章删除、真实 CI 徽章、60FPS / 零分配表述收敛、测试数改 46、新增"工程证据"小节（TASK-004） | [README.md](../README.md) |

> 注意：第 1–11 项当前**均在工作区、尚未提交**（见 §3 第 3 条）。

## 3. 未完成清单（诚实口径）

| # | 事项 | 状态 | 去向 |
| --- | --- | --- | --- |
| 1 | 真实硬件验收 | **未执行**。本机仅存在 ELTIMA Virtual Serial Port（COM1↔COM2）虚拟串口对，未检测到 CH340/CP210x 等 USB 串口芯片或 ESP8266 设备 | [hardware-validation.md](hardware-validation.md) |
| 2 | 发布包与安装验证 | 未执行。v1.1.0 zip 已发布，但打包 / 校验和 / 干净机安装 / 发布后 smoke / 回滚未按检查表走查 | [release-checklist.md](release-checklist.md) |
| 3 | 工作区遗留改动提交 | **未提交**。CI、LICENSE、GIF 归档、基准工程、文档、`coverlet.collector` 8.0.1 + `SQLitePCLRaw.lib.e_sqlite3` 原生库等改动全部待提交；提交前须 `dotnet test` 全绿 | git 工作区（`git status`） |

## 4. 任务表

| 编号 | 优先级 | 任务 | 负责人 | 验收标准 | 证据链接 | 状态 |
| --- | --- | --- | --- | --- | --- | --- |
| V12-01 | P0 | 提交工作区全部遗留改动 | lead | `dotnet test` 全绿后提交，`git status` 干净，无未跟踪文件 | — | 未完成 |
| V12-02 | P0 | 推送并确认 CI 首绿、徽章生效 | lead | GitHub Actions 四步全绿；README CI 徽章显示绿色 | [.github/workflows/ci.yml](../.github/workflows/ci.yml) | 未完成 |
| V12-03 | P1 | 真实硬件验收执行与签字 | ZCode 执行 / lead 签字 | 测试矩阵 6 项判定均 ≥"通过（带备注）"，签字栏签署 | [hardware-validation.md](hardware-validation.md) | 未执行 |
| V12-04 | P1 | v1.2 发布流程走查（打包→校验和→安装→smoke→回滚预案） | lead | release-checklist.md 逐项通过并签字 | [release-checklist.md](release-checklist.md) | 未执行 |
| V12-05 | P2 | README 表述收敛（徽章 / 性能数字 / 工程证据） | ZCode | 无伪造徽章；每个性能数字有实测出处；测试数为实际值 | [README.md](../README.md) | ✅ 已完成 |
| V12-06 | P2 | BenchmarkDotNet 性能基准与文档 | ZCode | 原始数据 + 复现命令 + 未测量边界成文 | [performance-benchmarks.md](performance-benchmarks.md) | ✅ 已完成 |
| V12-07 | P2 | 串口 CTS 竞态故障复盘 | ZCode | 现象→根因→修复→防复发闭环，含残余风险 | [postmortem-serial-dispose.md](postmortem-serial-dispose.md) | ✅ 已完成 |

## 5. 发布条件（Definition of Done）

v1.2 打 tag 前必须**全部**满足：

1. `dotnet build -c Release`、`dotnet test -c Release`、`dotnet format --verify-no-changes` 本机与 CI 双口径全绿。
2. [performance-benchmarks.md](performance-benchmarks.md) 含原始数据与复现命令；README 引用的每个性能数字都能在该文档找到出处。
3. [hardware-validation.md](hardware-validation.md) 测试矩阵执行完毕、判定为"通过"，签字栏已签署。
4. [release-checklist.md](release-checklist.md) 逐项通过，责任人 / 日期签字。
5. `git status` 干净，tag 指向已通过上述验证的提交。

## 6. 风险与依赖

| 风险 / 依赖 | 影响 | 缓解 |
| --- | --- | --- |
| **无真实硬件**（本机仅 ELTIMA 虚拟串口对 COM1↔COM2） | 电气噪声、断帧、拔插、断电等真实设备路径完全未验证，V12-03 无法闭环 | 验收模板已备（hardware-validation.md）；短期以虚拟串口 + Node 模拟器覆盖功能面；待采购 / 借用 NodeMCU v2（ESP8266）+ 3.3V USB-TTL 适配器后执行 |
| **UI 帧率无法自动化测量** | "60FPS" 只能作为未实测上限表述，不能进 DoD，也无法进 CI | README 已收敛为"端到端渲染帧率未测量"；如需实测，另行安排人工录屏 / 帧计时，不进本里程碑 |
| **Windows 专用**（WPF + COM 口） | CI 只能跑 windows-latest，无跨平台验证面；Linux/macOS 用户不可用 | CI 已固定 windows-latest；明确不在本里程碑投入跨平台 |
| fire-and-forget 异常仅记日志、无告警 | 异常"可观测地吞掉"，依赖人工巡检日志 | postmortem 残余风险已记录；本里程碑不加告警系统，仅靠日志巡检 |
| 工作区大量改动未提交 | CI / 徽章 / 基准在远端不可见，任何本地验证结论都尚未进入仓库历史 | V12-01 / V12-02 列为 P0，先于其他事项执行 |

## 7. 下一迭代建议（不展开功能规划）

1. 硬件到位即按 [hardware-validation.md](hardware-validation.md) 执行并归档证据（照片 / 串口日志 / 录屏），据此收口 v1.2；若短期无硬件，则将 v1.2 冻结为"工程可信度"口径发布，硬件验收顺延为独立标签（如 v1.2.1-hw）。
2. 连接级 PRAGMA 已修复（TASK-005）：`SqliteConnectionFactory.CreateConnectionAsync` 对每个新连接设置 `synchronous=NORMAL`，应用写入口径与 [performance-benchmarks.md](performance-benchmarks.md) 基准一致；后续可在同等环境复测确认。

## 8. v1.2 夜间补强批次（2026-11-17，已提交并推送）

> 本文 §3 的"未提交"状态已被本批次覆盖：工作区全部提交，CI 首轮全绿（run 43915278633，8 步 success，2m10s）。
> 本节只记录有实测数字的完成项，数字出处统一指向 [performance-benchmarks.md](performance-benchmarks.md)。

| 编号 | 事项 | 结果与证据 |
| --- | --- | --- |
| V12-08 | 报警历史查询下推（`WHERE` + `LIMIT` + `AlarmHistory(Timestamp)` 索引） | 8 589 µs → 118 µs，分配 -98.7%；7 个仓储测试 |
| V12-09 | 报警上下文查询重写（按报警窗口逐条查询） | 412 ms / 86.7 MB → 1.1 ms / 0.27 MB；7 个测试（设备隔离/窗口边界/防退化） |
| V12-10 | 趋势查询 SQL 分桶（每桶温度 min/max 真实采样点） | 20 万行分配 80 MB → 0.75 MB；**时间持平**，不宣称提速；5 个测试 |
| V12-11 | 遥测复合索引 `(DeviceId, Timestamp)` | 窗口查询 591 ms → 1.1 ms；写放大 1.21×–1.33×（[§9](performance-benchmarks.md)） |
| V12-12 | 数据保留策略 | 30 天/5000 行一批/200 批上限，报警默认不删；8 个测试；[data-retention.md](data-retention.md) |
| V12-13 | 一键打包脚本 | `tools/package.ps1` 产出 self-contained zip + SHA256，`sha256sum -c` 通过，三次重跑幂等 |
| V12-14 | CI 增强 | 覆盖率采集 + artifact 上传 + 漏洞扫描（informational） |
| V12-15 | 启动配置校验 + 日志批量裁剪 | 9 个参数范围校验；11 个测试 |

### 仍未完成

| 事项 | 状态 | 说明 |
| --- | --- | --- |
| 真实硬件验收（V12-03） | **仍未执行** | 本机仍只有 ELTIMA 虚拟串口对；模板见 [hardware-validation.md](hardware-validation.md) |
| 发布流程走查（V12-04） | 脚本就绪，未逐项签字 | [release-checklist.md](release-checklist.md) 已改为脚本口径，干净机安装/回滚仍需人工执行 |

### 测试与门禁（本批次结束时的实测口径）

- `dotnet test -c Release`：**87 通过 / 0 失败**（约 6 秒）
- `dotnet build -c Release`：0 错误，2 条既有 NU1701（SkiaSharp.Views.WPF）警告
- `dotnet format --verify-no-changes`：退出码 0

## 9. v1.2.0 发布走查（2026-11-17，本机）

> 门禁口径：`dotnet build -c Release` 0 错误（2 条既有 NU1701 警告）、`dotnet test -c Release` 87 通过 / 0 失败、format 退出码 0、CI run `43938597156`（`ae10336`）8 步 success；走查时间 2026-11-17 23:19–23:56（Windows 11 · i7-12650H，ELTIMA 虚拟串口对 COM1↔COM2）。

### 9.1 已闭环

| 项 | 结果与证据 |
| --- | --- |
| 启动冒烟 | 坏配置（BaudRate=0 / PollingIntervalMs=5 / MaxLogEntries=10）启动弹窗「配置错误」，逐条列出 3 个非法参数并指向 `%LOCALAPPDATA%\SmartEdgeHMI\settings.json`，退出码 1，日志 3 条 `[FTL] 设置校验失败`；正常启动主窗口 1s 内出现，日志「设置校验通过」「数据保留策略已启动」「Watchdog 心跳发送已启动」，正常关闭退出码 0、无 `[FTL]` |
| 双开 | 第二实例弹「系统提示 / SmartEdgeHMI 系统已在运行中, 请勿重复启动！」→ 退出码 0，首实例存活 |
| Watchdog | Watchdog 自行拉起主程序，18 秒内 9 次心跳无误差；强杀后 **1.8 s** 重启新 PID、心跳恢复，日志「HMI 主进程已退出 (ExitCode: -1), 正在重启」 |
| 迁移 | `v1.1.0` tag 不可编译（CS0103），旧库由 `3cb8a00` 生成（同 schema）；新版本打开自动补建 `idx_alarm_timestamp`、`idx_telemetry_device_timestamp`，1800 遥测 / 180 报警迁移前后完全一致，`PRAGMA journal_mode` 保持 wal |
| 保留清理（端到端，非单测） | 45 天种子数据 + 30 天保留：日志「数据保留清理完成: 遥测删除 600 行(保留 30 天), 报警删除 0 行(保留 0 天), 批数上限截断: false」，遥测 1800 → **1200**（15 天 × 40 行）、报警保持 180 未动（策略说明见 [data-retention.md](data-retention.md)） |
| 包内容 | 含 `SmartEdgeHMI.exe`、`SmartEdgeHMI.Watchdog.exe`、`appsettings.json`；0 个 `.cs/.csproj/.sln/.pdb/.js/.map`、无 `secrets.h` |
| 校验和 | `dist/SmartEdgeHMI-1.2.0-win-x64.zip` 82,065,173 字节 / 78.26 MB（290 个根文件 / 511 个条目），SHA256 `99e8a5e937bcb2403294cf01c07c46d5c6ecdbe11ee7b1550e34dc48d61034a1`，`sha256sum -c` 等价校验通过 |
| LTTB 复现命令 | `dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --verify` → 10000 / 100000 / 1000000 三点均精确返回 1000 条、首尾保留，PASSED（基准时效：`8dfcb40` 之后 `SmartEdgeHMI/Data` 仅 `c4579d0` 新增保留服务，未触及 LTTB 与写入热路径，无需重跑基准） |
| 模拟器 E2E（数据链路） | JSON 模式 COM1↔COM2 虚拟串口对：遥测约 1 行/秒入库（1200 → 1245 行），当前值实时刷新 24.4~25.3 °C；「设备紧急复位」下发后模拟器收到 `{"action":3}` 并执行「设备复位 -> 恢复初始状态」；断开 →「串口 COM1 已断开」→ 重连 →「已打开串口 COM1, 波特率 115200」→ 恢复「已连接 COM1」，无未观察 Task 异常 |

### 9.2 仍阻塞发布

| 项 | 原因 |
| --- | --- |
| 真实硬件验收 | 无 ESP8266 / USB 串口芯片 |
| 干净机安装 | 无第二台机器 |
| 回滚演练 + 数据库备份 | 需人工执行并留存备份 |
| 报警触发与确认（E2E） | JSON 报文不含 `errorCode`，`AlarmStateMachine.Evaluate` 只按设备上报 `ErrorCode` 判定，模拟器阈值固定 80、温度钳制 10~60，无法制造故障态；需设备侧故障注入或 Modbus 故障帧构造 |
| 首启默认值 | 无 `settings.json` 时 `SettingsService.LoadSettings` 写盘无兜底会中断启动（`:38`）；默认 `ComPort` 被赋成 `Sensor_01`（`:96`），不满足开箱即用 |
| 日志级别 | `Debug` 硬编码，未按现场口径收敛 |
| 保留策略长期收敛 | 本轮只做了"45 天种子数据 + 30 天保留"的单次端到端清理（1800 → 1200 行），连续运行 ≥24 小时后的库大小 / `-wal` 收敛仍未观察（[data-retention.md §5](data-retention.md) 已标注） |
| v1.1.0 tag | 不可编译（CS0103），发布包来源待追溯 |

### 9.3 结论

候选包已就绪（`dist/SmartEdgeHMI-1.2.0-win-x64.zip`，SHA256 `99e8a5e9…34a1`），但**未打 tag、未发布**：数据链路与发布脚本已闭环，仍缺硬件验收、干净机安装、回滚演练三项"必须换环境"的证据，且首启默认值、报警 E2E、日志级别三项属产品侧待修项。

环境限制如实记录：本机安全软件拦截未签名 exe 写 `%LOCALAPPDATA%\SmartEdgeHMI`（已签名 pwsh 可写而 .NET 探针同样被拒），`settings.json` 保存路径在本机无法验证，需干净机复验；日志中的 `保存通信设置失败` / `设备连接后自动下发阈值配置失败` 均源于此，不是产品缺陷。
