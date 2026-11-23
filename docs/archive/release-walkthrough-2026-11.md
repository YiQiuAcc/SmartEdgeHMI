# v1.2.0 发布前历史走查记录（2026-11-17 ～ 2026-11-23）

> 归档：本文件原为 `docs/release-checklist.md` §12，包含 v1.2.0 发布前的本机走查、24h 观察、回滚演练、干净机、真机验收与打包记录。
> 当前发布检查表见 [../release-checklist.md](../release-checklist.md)。

## 2026-11-17 首轮本机走查（23:19–23:56）

| 项 | 口径 |
| --- | --- |
| 机器 | Windows 11（开发机）· .NET 8.0.34 运行时 |
| 包 | `SmartEdgeHMI-1.2.0-win-x64.zip`（78.26 MB，290 文件，SHA256 见 §7） |
| 解压目录 | 空白目录解压后直接启动，无需安装 |
| 串口环境 | ELTIMA 虚拟串口对 COM1↔COM2；模拟器 `node virtual-device.js json COM2 115200` |
| 结论 | 启动 / 双开 / Watchdog / 迁移 / 保留清理 / 模拟器连接与指令下发 / 断开重连通过；硬件、干净机、回滚、报警触发与确认未执行 |
| 环境限制 | 未签名 exe 写入 `%LOCALAPPDATA%\SmartEdgeHMI` 被拒（事后定位为低完整性目录；同一时刻已签名 `pwsh` 可写，10 行 .NET 探针同样被拒），`settings.json` 保存路径无法验证 |
| 首启发现 | 删除 `settings.json` 后首启启动中断（`SettingsService.cs:38` 写文件无兜底）；默认 `ComPort` 被赋成 `Sensor_01`（`SettingsService.cs:96`）→ §5「默认值开箱即用」判未通过 |
| 模拟器 E2E | 遥测约 1 行/秒入库（1200 → 1245）、实时值刷新、复位指令下发并被模拟器执行、断开重连正常；报警触发与确认未覆盖（JSON 无 `errorCode`，模拟器无法制造故障态） |

### 2026-11-20/21 第二轮本机走查（2026-11-20 23:32 – 2026-11-21 00:26）

| 项 | 口径 |
| --- | --- |
| 机器 | Windows 11（开发机） |
| 新候选包 | `SmartEdgeHMI-1.2.0-win-x64.zip`，82,065,781 字节 / 78.26 MB，SHA256 `24fa400406a1b456fef5d6513fe87edc3409ad6ae09368841b7d545b3b29b168`，构建来源 `5a7c383`（2026-11-21 00:20）；旧候选包（`99e8a5e9…34a1` / `6250434`）作废 |
| A · 首启（从 `bin\Release` 运行，删除 `settings.json`） | 启动后 1.24 s 出现主窗口，无异常弹窗；`settings.json` 重新生成，内容 `"ComPort": ""`（未选择），其余默认值正确；日志落 `%LOCALAPPDATA%\SmartEdgeHMI\logs\log-20261120.txt`（首行「日志文件目录: …」），全程 0 条 `[DBG]`，日志含「设置校验通过」，正常关闭「退出码: 0」；未选端口点「连接」，状态栏显示「请先选择串口」（不再静默）；选 COM1 后 `settings.json` 成功改写（`"ComPort": "COM1"`，468 字节），日志无「保存通信设置失败」 |
| B · 发布包解压运行 | 日志目录探测到 `%LOCALAPPDATA%` 不可写，回退到程序目录 `logs\log-20261121.txt`（首行写明落点）；`保存通信设置失败` / `设备连接后自动下发阈值配置失败` 各出现，异常均为 `UnauthorizedAccessException`（事后定位为低完整性目录导致，见本文）；启动、串口连接（`已打开串口 COM1, 波特率 115200`）、数据入库均不受影响 |
| 环境差异（A vs B） | 同一台机器：从 `bin\Release` 运行可写，从解压目录运行被拒；事后定位为低完整性目录导致（见本文） |
| C · 报警端到端（3 轮，最终 2 轮为修复后） | 模拟器 `node virtual-device.js json COM2 115200 --fault-window 20 15`：`[FAULT]` 计划 / 进入(t=20s) / 退出(t=35s) 三行齐全，窗口内 TX 含 `"status":4,"err_code":302`；报警 `Id=3`：`UNACK 00:17:31.61` → 操作员确认 `ACK 00:17:32.59` → `NORMAL 00:17:48.62`，`ClearedAt` 非空，`State=3` 已落库；应用日志三行齐全：`报警触发 UNACK` / `操作员确认 UNACK→ACK` / `完全恢复 ACK→NORMAL`；「一键确认全部」报警期间 `enabled=True`，恢复后 `enabled=False` |
| C · 修复前对照（同机 `Id=1`） | 日志已打印「完全恢复 ACK→NORMAL」，数据库却停在 `ACK`、`ClearedAt=NULL` —— 正是 `ee2758f` / `5a7c383` 修掉的缺陷 |
| D · 24 小时观察（当轮快照，最终结论见本文） | 2026-11-21 00:18:51 起，发布包（`soak-app`）+ 模拟器 1 Hz 连续上报；采样器每 300 s 写本机 `soak-data\soak.csv`；截至 00:26:38（2 次采样）：工作集 255.1 / 255.2 MB、私有 166 MB、句柄 735、`SmartEdgeHMI.db` 69,632 B、`-wal` 4,144,752 B（4.13 MB 自限）、遥测 421 → 457 行、报警 0 行、`journal_mode=wal`；日志 `[ERR]` 3 条（均为 B 段写盘拦截）、`[WRN]` 0 条；预计 2026-11-22 00:26 结束 |
| 结论 | 首启 / 默认配置 / 生产日志 / 报警触发→确认→恢复 E2E 通过；发布包写盘受限（事后定位为低完整性目录）；当轮 24h 观察进行中，硬件、干净机、回滚演练未执行（后续均已完成，见本文） |

### 2026-11-21/22 24 小时观察与回滚演练（2026-11-21 00:26:02 – 2026-11-22 00:40）

| 项 | 口径 |
| --- | --- |
| 机器与证据 | 本机（同前两轮）；24h 采样与判定证据在本机 `soak-data\` 存档（不入库） |
| A · 观察窗口与采样 | 2026-11-21 00:26:02 → 2026-11-22 00:22:11；289 行、间隔 300 s，最大间隔 310 s（08:26:44→08:31:54），>405 s 缺口 0 次，时间戳非单调 0 次；`AppAlive=False` 0 条，PID 唯一 24308，无重启 |
| A · 资源 | 工作集首 255.1 MB → 末 97.7 MB（峰值 273.2 MB @03:46:40、最低 38.9 MB @21:47:08，后 1/3 斜率 −6.85 MB/h，全程 −61.7%，检测到 11 处 >15% 的 GC 回落）；句柄 735 → 730（峰值 769 @14:11:59，后 1/3 斜率 +1.86 个/h，全程 −5）；线程 47 → 25；CPU 累计 305.8 CPU·秒 / 86,169 秒 = 0.35% 单核 |
| A · 数据库 / WAL | `SmartEdgeHMI.db` 69,632 B → 15,454,208 B（+14.67 MB，0.613 MB/h，按此外推 30 天约 456 MB）；WAL 峰值 4,161,232 B（3.97 MB @20:47:07），末 1/10 采样极差 0；`journal_mode=wal` ×289 |
| A · 遥测 / 报警 / 日志 | 遥测 421 → 86,528 行（+86,107，0.999 行/秒，对 86,400 行期望的达成率 99.7%）；报警 0 行；`LinkEvents` 1 → 1（无重连）；日志 3,839 B → 13,739 B，`[ERR]` 3 → 3（无新增）、`[WRN]` 0 → 10 |
| A · 判定与退出 | `analyze-soak.ps1` 总判定 **PASS-WITH-NOTES**（唯一「需人工判断」项 = 那 10 条 WRN）；采样器 00:27:11 自行退出（`soak 结束`），主程序 00:29:02 正常退出（日志 `[INF] SmartEdgeHMI 应用程序正在退出。退出码: 0`） |
| A · 10 条 `[WRN]` 定性（2026-11-22 更正） | 全部为 `心跳发送异常, 3000ms 后重试`（2026-11-21 16:51:28–16:58:44）。触发源确实是测试活动（`WatchdogHeartbeatClientTests` 在全局管道名 `SmartEdgeHMI_Watchdog_Pipe` 上起了假服务端），但当时「非产品缺陷」的定性**是错的**：2026-11-22 的实验证明，同一管道名被占用会让 Watchdog 反复强杀健康主进程。这 10 条 WRN 是**真实缺陷的症状**，已作为 v1.2.0 阻塞项修复，见 [故障复盘合集](../postmortems.md) |
| A · 证据 | `soak.csv` SHA256 `f92c6999771113c846f5c7b06c67fac55a57553016c2058ff5fa3b6493b3d67e`（本机 `soak-data\` 存档，不入库） |
| B · 回滚演练（P0-07，2026-11-22 00:30–00:40） | v1.1.0 真包（GitHub Release 资产 104,128,612 B，digest `9dcdfa43…503078` 核对一致）启动：主窗口出现，库 `TelemetryHistory 60 / AlarmHistory 1`，索引 2 个（`idx_telemetry_device`、`idx_telemetry_timestamp`）；冷备份 #1 `20261122-003239`（3 个文件 57,344 B，manifest 自校验通过）；新包覆盖解压升级（17 → 295 个文件，v1.1.0 的两个 `.pdb` 残留）；升级后启动：索引 4 个（新增 `idx_telemetry_device_timestamp`、`idx_alarm_timestamp`），**遥测 60 → 0**、报警保留 1 条；界面选 COM1 →「已连接 COM1」→ 遥测 0 → 25 → 35 行；正常退出（`退出码: 0`）；冷备份 #2 `20261122-003427`（1 个文件 32,768 B，`telemetryRows 35 / alarmRows 1`）；回滚 = 恢复 v1.1.0 二进制 + `restore-data.ps1` 恢复备份 #1 → 库回到 `60 / 1`、索引回到 2 个（升级后数据移至 `data-before-restore-20261122-003437`，未删除）；回滚后启动：主窗口出现，界面报警列表可见 `ThresholdExceeded` 行 |
| B · 升级清理旧遥测（注意事项） | 升级后首次启动，1.2.0 的数据保留任务把 v1.1.0 库里 60 行 2026-06-29 的遥测判为超过 30 天而清理；报警按 `AlarmRetentionDays=0` 保留。**这是策略行为，不是数据丢失**，但升级说明必须提醒用户：升级会按保留策略清理旧遥测（[release-notes-v1.2.0.md](../release-notes-v1.2.0.md) 已知问题与升级说明已标注） |
| C · 重建与门禁（2026-11-22，`0168071`） | `dotnet build -c Release` 0 错误（2 条既有 NU1701）；`dotnet test -c Release` 103 通过 / 0 失败 / 0 跳过（92 → 99 → 103：报警写入侧 +7、Watchdog 心跳 +4）；`dotnet format --verify-no-changes` 退出码 0；`git diff --stat 5a7c383..0168071 -- SmartEdgeHMI/ SmartEdgeHMI.Watchdog/ Firmware/ Simulator/` 输出为空 → 24 小时观察跑的就是这份产品代码（新增仅测试 / 工具 / 文档）；00:36 重建候选包（SHA256 见 §7，旧候选包 `5a7c383` 作废） |
| 环境限制 | v1.1.0 与 v1.2.0 均在写 `%LOCALAPPDATA%\SmartEdgeHMI\settings.json.tmp` 时被拒（`UnauthorizedAccessException`），与前两轮结论一致；不影响启动 / 连接 / 入库 |
| 结论 | 24h 观察 **PASS-WITH-NOTES**（唯一备注 = 10 条 WRN，后确认为管道缺陷症状，已修复）；P0-07 升级 / 回滚演练通过（条数核对 `60/1 → 走完 → 60/1`）；真实硬件验收、无开发环境干净机仍未执行，发布继续阻塞 |

### 2026-11-22 心跳管道同名串台缺陷：定性、修复与回归（11:06–11:45）

| 项 | 口径 |
| --- | --- |
| A · 修复前复现 | 先由脚本以 `maxInstances=1` 抢占 `SmartEdgeHMI_Watchdog_Pipe`，再启动候选包的 `SmartEdgeHMI.Watchdog.exe`：75 秒内主程序出现 **7 个不同 PID、6 次强杀**，周期约 11 秒；Watchdog 每 2 秒一条 `[ERR] 管道服务器循环异常` + `System.IO.IOException: 所有的管道范例都在使用中。`；主程序 7 次启动、6 次 `退出码: 0` 正常退出、**0 条心跳告警**（连上冒充者后 `hb` 照发） |
| B · 根因 | 管道名是编译期常量；Watchdog 绑定失败只重试不退出，`_lastHeartbeat` 停在启动时刻，10 秒后即杀自己刚拉起的健康进程；`_lastHeartbeat != DateTime.MinValue` 的兜底被 `StartHmiProcess()` 覆盖成当前时间而失效；客户端不校验对端身份 |
| C · 修复 | 新增 `Shared/WatchdogPipeName.cs`（管道名 = 前缀 + SHA256(程序目录小写全路径) 前 16 位，两工程链接同一源文件）；Watchdog 仅在管道服务端已绑定时才可按心跳超时杀进程（进程退出仍无条件重启）；绑定失败 ERR 降为 60 秒一条；测试改用测试目录派生的管道名，不再占用生产管道名 |
| D · 门禁（`c9997a5`） | `dotnet build -c Release` **0 错误**（2 条既有 NU1701）；`dotnet test -c Release` **108 通过 / 0 失败 / 0 跳过**（103 → 108：管道名派生 +5）；`dotnet format --verify-no-changes` 退出码 **0**；CI run `44981622078` **success**，与候选包构建提交一致 |
| E · 四场景回归（修复后，同一套发布二进制） | A 正常（无冒充者）70 s：**1 个 PID**，不被杀；B 冒充者占用**派生名**：仍 **1 个 PID**（失效安全生效，未杀健康进程）；C 冒充者占用**旧写死名**：**1 个 PID**（名字已不同，无影响）；D 强杀主程序：**2 个 PID**，2 s 内拉起新 PID（Watchdog 保护未退化） |
| F · 新候选包 | `SmartEdgeHMI-1.2.0-win-x64.zip`，82,066,546 字节 / 78.26 MB，SHA256 `76e9641a…07f681`，构建自 `c9997a5`；包内 `SmartEdgeHMI.exe` 与 `SmartEdgeHMI.Watchdog.exe` 的 `ProductVersion` 均为 `1.2.0+c9997a5…`（SHA256 见 §7） |
| G · 遗留风险 | 心跳报文仍无鉴权；知道派生规则且与被监视程序同目录的进程理论上仍可冒充或吸收心跳（已写入 Release Notes 已知问题 8） |

### 2026-11-22 干净机验收（P0-05）

| 项 | 口径 |
| --- | --- |
| 机器 | Windows 10 Pro 22H2（普通用户，非管理员）；**无 .NET SDK、无 .NET 8 运行时**；**无任何 COM 口**；装有第三方安全软件且全程运行、未加白 |
| T1 包完整性 | **通过**：82,066,546 B；SHA256 `76E9641A…07F681` 与 `.sha256`、交接信息三方一致；解压 511 文件 / 13 目录 |
| T2 无 .NET 前提 | **通过（限定）**：无 SDK、无 .NET 8；候选包 `includedFrameworks` 为 8.0.34 self-contained，启动成功（该机并非"绝对没有任何 .NET"，已如实注明） |
| T3 首启 | **通过**：进程创建到主窗口 **1.364 s**；无应用错误框；`settings.json` 与日志自动生成 |
| T4 设置落盘 | **通过（替代口径）**：波特率 115200→9600、阈值 60→65（默认 60，65 必来自落盘）；退出码 0；重启后保留；全量日志 0 命中 `保存通信设置失败` / `settings.json.tmp` / `UnauthorizedAccessException` / `Access is denied`。**T4-COM1 未执行**（该机端口下拉为空且不可编辑） |
| T5 日志 | **通过**：`%LOCALAPPDATA%\SmartEdgeHMI\logs\log-20261122.txt`；Information；0 ERR / 0 WRN / 0 DBG |
| T6 双开 | **通过**：弹窗标题「系统提示」、正文「SmartEdgeHMI 系统已在运行中, 请勿重复启动！」；第二实例退出、第一实例存活 |
| T7 Watchdog | **通过**：管道名 `SmartEdgeHMI_Watchdog_Pipe_0abb82f50d5a9a77`（**我方按工作目录独立复算，逐字符一致**）；强杀主程序后 **0.26 s** 拉起新 PID 并重新连接管道 |
| T8 包审计 | **通过**：511 文件（503 `.dll` / 5 `.json` / 3 `.exe`），0 个 `.cs/.csproj/.sln/.pdb/.js/.map/secrets.h`；两 EXE 产品版本 `1.2.0+c9997a5…`；`NotSigned` |
| T9 数据链路 | **未执行**（无串口设备；按要求未安装驱动或虚拟串口） |
| 关键结论 | 写盘失败的根因是**运行目录的完整性级别**而非安全软件：从低完整性目录启动时进程为 Low，无法写中完整性的 `%LOCALAPPDATA%`；同一台机器换到桌面普通目录后设置写入、重启保留、日志全部正常，第三方安全软件全程运行未拦截 |
| 回传证据 | **齐备**：`SmartEdgeHMI-acceptance-return-20261122.zip`（45 个条目）含 `localappdata-SmartEdgeHMI\`（`settings.json` 原件 `BaudRate=9600` / `DefaultThreshold=65`、两份日志原件均为 **0 ERR / 0 WRN / 0 DBG**）、`extraction-logs\watchdog-20261122.log` 原件（含派生管道名与"退出→重启→重连"完整时间线）、`package-audit.txt`、`low-integrity-diagnosis.txt`、`invalid-lowil-runs\`（三次失败运行原件）、6 张关键截图与报告全文。本机已解包复核，与报告逐项一致 |

### 2026-11-22 串口两项 · 本机虚拟串口替代覆盖（`T4-COM1`、`T9`）

> 干净机无任何 COM 口且不可加装，这两项改在本机用 ELTIMA 虚拟串口对 `COM1↔COM2` + 项目自带 Node 模拟器覆盖。**边界：这覆盖的是串口功能，替代不了"无开发环境可运行"这一干净机结论（后者已由该干净机独立完成）；也不能替代真实设备的电气特性（属 P0-06）。**

| 项 | 口径 |
| --- | --- |
| 被测对象 | `dist/publish/1.2.0/`（与候选包同一构建，`ProductVersion = 1.2.0+c9997a5…`） |
| T4-COM1 | **通过**：移走 `settings.json` 使基线为空 → 界面下拉选 `COM1` → `settings.json` **立即**变为 `"ComPort": "COM1"`（选择即保存，不等退出）→ 正常退出 `退出码: 0`、文件仍为 COM1 → 重启后界面端口显示 **COM1** |
| T9 | **通过**：模拟器占住 `COM2`（外部试打开返回 `Access is denied`）；应用连 `COM1` 后 `[SerialPort] 已打开串口 COM1, 波特率 115200`，并 `[JSON] 下发阈值至 COM1: 60°C`；36 秒内 `TelemetryHistory` **0 → 37 行**（约 1 行/秒）；界面 **已连接 COM1**、曲线 24.0–25.4 °C、当前值 24.8 °C；退出码 0；当日日志 124 行 **0 ERR / 0 WRN**；`journal_mode=wal` |
| 证据 | 本机 `local-serial-20261122\` 存档（含 `T9-connected.png`、`T4-02-restart-persisted.png`、`app-log-after-T9.txt`、`sim-stdout.log`、`SmartEdgeHMI.db`，不入库） |

### 2026-11-22 真实硬件验收两轮（P0-06 · NodeMCU v2 + CH340 / COM4）

> 记录全文见 [真机验收记录](hardware-validation-2026-11-22.md)。这里只放结论与关键数字。

| 项 | 口径 |
| --- | --- |
| 第一轮构建 | `c9997a5` 候选包；固件由 `Firmware/` 本地编译烧录（309,984 B，Hash 校验通过） |
| 第一轮结果 | 5 项通过（带备注）、**HW-06 失败**：看门狗 1.2 s 重启成功，但协议回落 JSON 且不自动连串口，系统静默不采数；另暴露重连竞态（打开成功与"放弃重连"同毫秒、遥测永久停住）与两条协议/能力缺口 |
| 修复 | 提交 `275a6d6`（协议持久化 / 启动自动连接 / 重连竞态），新增 21 个单测，129 全绿；CI run `44997763278` success |
| 第二轮构建 | `275a6d6`，包 82,067,265 B / SHA256 `6fc86aa3…876077`，两 exe `ProductVersion = 1.2.0+275a6d6…` |
| 第二轮结果 | **六项全部通过（带备注）**：HW-06 从强杀到 Modbus 轮询恢复 **7.8 s 零人工操作**；HW-02 含竞态回归无故障误报；HW-05 断电后回到 25.1–25.4 °C / 43.1–43.3 %；HW-01 **125 分钟 / 7,695 行 / 每分钟 58–62 行无中断 / 零新增日志** |
| 遗留已知限制 | F-4 阈值只写不可回读（FC03 addr2 读的是状态位），该条**不可验证**阈值默认值；F-6 上位机无 LED 指令，验收须用探针发帧。两项转入 v1.2.1（P1-04 / P1-05），验收模板已标注规避口径 |
| 证据 | 本机 `hw-accept-20261122\` 存档（`ACCEPTANCE-RECORD.md`、`soak-fixed\soak-samples.csv` 与 `soak-summary.txt`、多张截图、固件编译与烧录日志，不入库） |

### 2026-11-22 清洁构建、打包与打 tag

| 项 | 口径 |
| --- | --- |
| 清理 | 仓库内 `bin` / `obj` / `.vs` / `BenchmarkDotNet.Artifacts` / `Firmware/.pio` / `Simulator/node_modules` / `dist` 全部删除（两轮共释放 1,338.3 MB + 900.7 MB）；工作区冗余副本（解包验收副本、重复 handoff 目录）再释放 483.2 MB，合计约 **2.72 GB**。保留：`Firmware/.vscode/`、`Firmware/src/secrets.h`（WiFi 凭据，不入库）、`dist/`（交付物） |
| 清洁构建 | `dotnet build -c Release` 从零还原：**0 错误**（2 条既有 NU1701），12.4 s |
| 门禁 | `dotnet test -c Release --no-build` **129 通过 / 0 失败 / 0 跳过**；`dotnet format --verify-no-changes` 退出码 **0** |
| 打包 | `pwsh tools/package.ps1 -Version 1.2.0` → `dist/SmartEdgeHMI-1.2.0-win-x64.zip`，**82,066,878 字节 / 78.27 MB**，511 条目（503 `.dll` / 5 `.json` / 3 `.exe`），0 个禁止扩展名、无 `secrets.h`、无 `.workbuddy` |
| 校验 | SHA256 `0c1869d9fa93a62955fbb171100ef73034208c05aef45b8b4736093a3611e949`；两个 exe 的 `ProductVersion` 均为 `1.2.0+1744cd2…`；`pwsh tools/verify-release.ps1 -Version 1.2.0` 四项检查退出码 **0** |
| 提交与 CI | `1744cd2`（包构建提交）；CI 运行号用 `gh run list --commit $(git rev-parse v1.2.0) --limit 1` 查询 |
| tag / Release | 注解标签 `v1.2.0` 已推送到 GitHub，指向回填发布状态的文档提交；**GitHub Release 页与资产已建立**（[v1.2.0](https://github.com/YiQiuAcc/SmartEdgeHMI/releases/tag/v1.2.0)），§10 已完成下载与 SHA256 复核，解压启动待补做 |
| 遗留 | `dist/` 按约定不入库；`.workbuddy/` 已移至仓库外归档；`Firmware/src/secrets.h` 保持本地；无残留 SmartEdgeHMI 进程 |

