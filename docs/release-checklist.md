# 发布检查表（Release Checklist）

> 适用范围：SmartEdgeHMI Windows 便携 zip 发布（主程序 + Watchdog）。
> 使用方式：发布负责人逐项勾选；存在未通过项时不得打 tag。
> 性能数字如需引用，一律出自 [performance-benchmarks.md](performance-benchmarks.md)，不得新造口径。

## 1. 版本与分支

- [x] 版本号确定（SemVer，如 v1.2.0），与 Release Notes / tag 名一致（版本 1.2.0）
- [x] 发布分支为 `main`，工作区干净：`git status` 无未提交 / 未跟踪文件（文档提交后复核）
- [ ] tag 指向本次已验证的提交，tag 名与版本一致（未打 tag：24h 观察与回滚演练已于 2026-11-22 闭环，仍待干净机与硬件验收后决定）
- [x] CI 在该提交上全绿（restore / build / test / format 四步）（CI run `44981622078` / `8952e46`，success，**与候选包构建提交一致**。说明：上一候选包构建于 `f8b3997`，但该提交没有以自己的提交号为 key 的 CI 运行——上一轮 11 个提交一次性 push，GitHub 只为推送 head `bb64359` 起跑，这是本轮修掉的证据链断点；本轮改为每个提交单独推送，候选包提交与 CI 运行已一一对应）

## 2. 构建与测试

- [x] `pwsh tools/package.ps1 -Version {版本}` 一键完成 test / publish / zip / SHA256（隐含 restore + build；勿用 `-SkipTests`；确需跳过时须另行补跑测试并记录），测试全绿，通过数：108（记录实际值；2026-11-22 `main` @ `8952e46` 实测 `dotnet test -c Release`：108 通过 / 0 失败 / 0 跳过，92 → 99 → 103 → 108：报警写入侧 +7、Watchdog 心跳 +4、管道名派生 +5；同日 11:39 新候选包由 `pwsh tools/package.ps1 -Version 1.2.0` 在 `8952e46` 上产出，见 §7）
- [x] `dotnet format SmartEdgeHMI.sln --verify-no-changes --no-restore` 退出码 0（2026-11-21 实测退出码 0）

## 3. 基准数据是否更新

- [x] 确认自上次基准运行后，LTTB / SQLite 写入路径代码是否变更；若变更，重跑 `SmartEdgeHMI.Benchmarks` 并更新 [performance-benchmarks.md](performance-benchmarks.md)（`8dfcb40` 之后 `SmartEdgeHMI/Data` 仅 `c4579d0` 新增保留服务；`c4579d0` 之后本轮改动集中在设置服务、启动日志、报警状态机、模拟器与 ViewModel，**未触及 LTTB 与 SQLite 写入热路径**，无需重跑）
- [x] README 中引用的性能数字与基准文档逐条一致，无超出实测边界的表述（Release Notes §4 与本轮引用均出自 performance-benchmarks.md §8.3 / §8.5 / §9 口径）
- [x] 复现命令（基准文档 §7）在当前代码上可执行（沿用 2026-11-17 实测记录：`dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --verify`：10000 / 100000 / 1000000 三点均精确返回 1000 条、首尾保留，PASSED）

## 4. 数据库 schema / 迁移与 WAL 兼容

> 本轮未变更数据库 schema，迁移证据沿用 2026-11-17 实测。

- [x] 用新版本打开旧版本（v1.1.0）生成的数据库文件：自动迁移成功，无 schema 报错（v1.1.0 tag 不可编译，旧库由 3cb8a00 生成（同 schema））
- [x] 迁移后旧数据可读：报警历史 / 遥测历史条数与旧版本一致（抽查）（迁移前 1800 遥测 / 180 报警，迁移后完全一致）
- [x] `PRAGMA journal_mode` 返回 `wal`（WAL 随库文件持久化）（迁移后实测仍为 wal）
- [x] 已知口径确认：`synchronous` 为连接级设置，新连接实际运行在默认 FULL（见基准文档 §6.4），发布版验证无数据损坏 / 死锁（本轮迁移与保留清理端到端实测无异常）
- [x] 降级路径说明：若本次 schema 有不可逆变更，须在 Release Notes 标注"回滚需恢复备份"（本轮变更为补建索引 + 保留策略，[release-notes-v1.2.0.md](release-notes-v1.2.0.md) §7 已标注）

## 5. appsettings 配置检查

- [x] 现场可调参数（批量写入 / 刷盘间隔 / 轮询间隔 / Watchdog 超时等）均在 `SmartEdgeHMI/appsettings.json`，无研发硬编码泄漏到现场配置面（`appsettings.json` 已入包，包内容核对见 §7）
- [x] 默认值开箱即用（首启无需手改即可连接模拟器 / 设备）✅ 首启不再因非法默认端口失败（`ComPort` 默认空＝未选择），首启 1.24 s 进入主界面、设置文件自动生成；需在界面选择实际端口（未实现自动选择）。本机发布包写 `%LOCALAPPDATA%` 被安全软件拦截，属环境限制，见 §12。
- [x] 发布包内无凭据残留：`Firmware/src/secrets.h`、测试密钥、本地路径不入包（包内容核对：0 个源码 / 密钥文件入包，无 `secrets.h`）
- [x] 日志级别 / 路径配置合理（生产为 Information 及以上，路径可写）✅ 默认 Information（`appsettings.json` 可调），路径 `%LOCALAPPDATA%\SmartEdgeHMI\logs`，不可写时回退程序目录（本机实测走到回退分支并正常写盘）。

## 6. Watchdog 联调

- [x] `SmartEdgeHMI.Watchdog` exe 与主程序成对打包（同目录 / 相对路径正确）（同目录成对入包，包内容核对见 §7）
- [x] 正常运行：3s 间隔心跳持续，Watchdog 不误杀（2026-11-17 实测 18 秒内 9 次心跳无误差；本轮未复测）
- [x] 强杀主进程 → Watchdog 在 10s 心跳超时内重启主程序 → 心跳恢复（2026-11-17 实测强杀后 1.8 s 重启新 PID，心跳恢复，日志「HMI 主进程已退出 (ExitCode: -1), 正在重启」；本轮未复测）
- [x] 防呆启动：双开主程序时第二个实例被 Mutex 拦截（2026-11-17 实测第二实例弹「系统提示 / SmartEdgeHMI 系统已在运行中, 请勿重复启动！」→ 退出码 0，首实例存活；本轮未复测）
- [x] 心跳管道被其他进程占用时不得反复强杀健康主进程（2026-11-22 四场景回归：A 正常场景 1 个 PID 不被杀；B 冒充者占用**派生名**仍不被杀（失效安全生效）；C 冒充者占用**旧写死名**完全无影响；D 强杀主程序后 2 s 内拉起新 PID（保护未退化）。修复前同一场景 75 秒内强杀 6 次，见 [postmortem-watchdog-pipe.md](postmortem-watchdog-pipe.md)）
- [ ] 重启后串口重开成功（无端口占用残留导致的连续失败）（无真实串口，未验证）

## 7. 打包与校验和

- [x] 打包执行：`pwsh tools/package.ps1 -Version {版本}`（publish / 压缩 / SHA256 一键完成，产物输出至 `dist/`）（2026-11-22 11:39 实测 `pwsh tools/package.ps1 -Version 1.2.0`，构建来源 `main` @ `8952e46`，产出新候选包；旧候选包 `f8b3997` 作废，见下方 SHA256 项）
- [x] 产物命名：`SmartEdgeHMI-{版本}-win-x64.zip`（脚本按 `-Version` / `-Runtime` 生成，tag 前缀 `v` 已去除）（产物 `SmartEdgeHMI-1.2.0-win-x64.zip`，82,066,546 字节 / 78.26 MB；新包根文件 / 条目数未逐项复测）
- [x] 包内容核对：主程序 exe + Watchdog exe + `appsettings.json` + 运行所需依赖；不含源码 / 测试 / 基准工程 / 模拟器（含 `SmartEdgeHMI.exe` / `SmartEdgeHMI.Watchdog.exe` / `appsettings.json`；0 个 `.cs/.csproj/.sln/.pdb/.js/.map`、无 `secrets.h`）
- [x] 干净目录（及一台无开发环境的机器）解压后可直接启动（本机空白目录解压后直接启动通过；**干净机已于 2026-11-22 验收通过**：Windows 10 Pro 22H2 / `干净机`，无 .NET SDK、无 .NET 8 运行时，解压 511 文件后启动 1.364 s 出现主窗口，见 §12 与 Release Notes 已知问题 2）
- [x] SHA256 校验和（脚本自动写入 `dist/<zip>.sha256`，格式 `<hash>  <文件名>`），记录到 Release Notes：
      `SHA256: 76e9641ab07478a3633b850ee66052db5d8bf9e825ede159519bd6b96607f681`（旧候选包 `c02c87aa5e6fcfbb04c81e62cb3a8685ce86c2bdcefc6072f06d5962ce6e230b` / `f8b3997` 作废，`24fa400406a1b456fef5d6513fe87edc3409ad6ae09368841b7d545b3b29b168` / `fb69bbe` 作废；更早 `99e8a5e9…34a1` / `ae10336` 已作废），2026-11-22 11:39 重建时由脚本记录；`pwsh tools/verify-release.ps1 -Version 1.2.0` 退出码 0 可复验

## 8. Release Notes

- [x] 三段齐全：新增 / 修复 / 已知问题（[release-notes-v1.2.0.md](release-notes-v1.2.0.md) §2 / §3 / §5）
- [x] 修复项覆盖：串口关闭 CTS 竞态（`30390aa`）及相应异常观察器（Release Notes §3）
- [x] 性能结论引用基准实测数字，不使用"60FPS""零分配"等未实测泛化表述（数字全部出自 performance-benchmarks.md §8.3 / §8.5 / §9；趋势查询如实写"耗时持平"）
- [x] 硬件验收状态如实标注（未执行 / 已执行，链接 [hardware-validation.md](hardware-validation.md)）（Release Notes 已知问题 1：未执行）
- [x] 升级 / 回滚说明包含数据库备份提示（Release Notes §6 / §7）

## 9. 回滚方案（含数据库备份）

- [x] 上一版本安装包与校验和仍可下载（当前：v1.1.0 zip 存档于 GitHub Releases）（2026-11-22 演练取用 GitHub Release `v1.1.0` 资产真包：104,128,612 字节，digest `9dcdfa43…503078`，核对一致）
- [x] 回滚步骤成文并演练过一次：退出程序（含 Watchdog）→ 恢复上一版 zip → 启动验证连接与数据（2026-11-22 00:30–00:40 实机演练通过：v1.1.0 真包启动 → 升级前冷备份 → 1.2.0 候选包覆盖升级 → 升级后启动 / 连接验证 → 恢复 v1.1.0 二进制 + `restore-data.ps1` 恢复备份库 → 回滚后启动验证；详见 §12「2026-11-21/22 24 小时观察与回滚演练」表）
- [x] **数据库备份**：发布前对用户数据目录 `*.db`（连同 `-wal` / `-shm`）做冷备份；回滚前**再次**备份新版本已写入的数据，避免回滚覆盖（已执行：升级前冷备份 `20261122-003239`（3 个文件 57,344 B，manifest 自校验通过，`telemetryRows 60 / alarmRows 1`）；升级后冷备份 `20261122-003427`（1 个文件 32,768 B，`telemetryRows 35 / alarmRows 1`）；备份存于本机演练目录，不入库）
- [x] 备份文件校验（可打开、条数正确），存放位置与保留期限已约定（两份备份 manifest 自校验通过、条数正确；回滚恢复备份 #1 后库回到 `60 / 1`、索引回到 2 个，条数核对 `60/1 → 走完 → 60/1` 成立；存放位置为本机演练目录（不入库））
      **保留策略（2026-11-22 约定）**：每个 `<备份根>\<yyyyMMdd-HHmmss>\` 为一份完整备份；保留**最近 10 份且不超过 90 天**（先到为准）——按 30 天库增长约 456 MB 估算，10 份上限约束了备份盘的长期占用。`backup-data.ps1` **不自动删除任何备份**（备份工具自动清理的误删风险高于磁盘收益），超出策略的由运维手工清理；`restore-data.ps1` 生成的 `data-before-restore-*` 目录在恢复确认后至少保留 30 天再删

## 10. 发布后 smoke 测试

- [ ] 从 Release 页下载发布包 → 校验 SHA256 → 解压启动成功（未执行：Release 页尚未发布）
- [x] 连接模拟器（或虚拟串口）：数据入库、图表渲染、指令下发（阈值 / 复位 / LED）、报警触发与确认各验证 1 次（本机实测：JSON 模式 COM1↔COM2 虚拟串口对，遥测以约 1 行/秒入库（1200 → 1245 行）、当前值实时刷新 24.4~25.3 °C；「设备紧急复位」下发后模拟器收到 `{"action":3}` 并执行「设备复位 -> 恢复初始状态」；阈值滑杆 30→20 生效并落盘。**报警触发 / 确认已完成**：修复后 3 轮 E2E，报警 `Id=3` 走完 `UNACK → ACK → NORMAL` 且 `ClearedAt` 落库，应用日志三行齐全，按钮「一键确认全部」恢复后自动禁用（模拟器故障窗口注入，见 §12 第二轮记录））
- [x] 断开 → 重连 1 次，日志无未观察异常（`ObserveAndLogAsync` 无 ERROR 输出）（本机实测：断开后「串口 COM1 已断开」，重连后「已打开串口 COM1, 波特率 115200」→ 恢复「已连接 COM1」并继续收数；无未观察 Task 异常；日志中 `保存通信设置失败` / `设备连接后自动下发阈值配置失败` 均源于本机对未签名 exe 的 `%LOCALAPPDATA%` 写入拦截，见 §12）
- [x] 强杀主进程 → Watchdog 重启 → 自动恢复 1 次（本机实测强杀后 1.8 s 重启新 PID，心跳恢复）
- [x] smoke 结果（含时间 / 机器）回填到 Release 说明或本表附件（回填见本表 §12 本机走查记录）

## 11. 责任人与签字

| 角色 | 责任人 | 日期 | 结论 |
| --- | --- | --- | --- |
| 发布负责人（lead） | lead | 2026-11-22 | ☐ 通过　☑ 阻塞（原因：真实硬件验收（P0-06）未完成；干净机 P0-05 已完成除串口相关两项外的全部验收，`T4-COM1` 与 `T9 数据链路` 因该机无 COM 口未执行） |
| 质量复核（ZCode） | ZCode | 2026-11-17 | ☑ 通过（备注：文档口径与证据逐条核对）　☐ 阻塞 |

> 全部通过后方可发布 Release 并打 tag；任何一项阻塞即冻结发布，修复后重新走查本表。

## 12. 本机走查记录（2026-11-17 23:19–23:56）

| 项 | 口径 |
| --- | --- |
| 机器 | Windows 11 · i7-12650H · .NET 8.0.34 运行时 |
| 包 | `SmartEdgeHMI-1.2.0-win-x64.zip`（78.26 MB，290 文件，SHA256 见 §7） |
| 解压目录 | 空白目录解压后直接启动，无需安装 |
| 串口环境 | ELTIMA 虚拟串口对 COM1↔COM2；模拟器 `node virtual-device.js json COM2 115200` |
| 结论 | 启动 / 双开 / Watchdog / 迁移 / 保留清理 / 模拟器连接与指令下发 / 断开重连通过；硬件、干净机、回滚、报警触发与确认未执行 |
| 环境限制 | 未签名 exe 写入 `%LOCALAPPDATA%\SmartEdgeHMI` 被安全软件拦截（同一时刻已签名 `pwsh` 可写，10 行 .NET 探针同样被拒），`settings.json` 保存路径无法验证 |
| 首启发现 | 删除 `settings.json` 后首启启动中断（`SettingsService.cs:38` 写文件无兜底）；默认 `ComPort` 被赋成 `Sensor_01`（`SettingsService.cs:96`）→ §5「默认值开箱即用」判未通过 |
| 模拟器 E2E | 遥测约 1 行/秒入库（1200 → 1245）、实时值刷新、复位指令下发并被模拟器执行、断开重连正常；报警触发与确认未覆盖（JSON 无 `errorCode`，模拟器无法制造故障态） |

### 2026-11-20/21 第二轮本机走查（2026-11-20 23:32 – 2026-11-21 00:26）

| 项 | 口径 |
| --- | --- |
| 机器 | Windows 11 · i7-12650H |
| 新候选包 | `SmartEdgeHMI-1.2.0-win-x64.zip`，82,065,781 字节 / 78.26 MB，SHA256 `24fa400406a1b456fef5d6513fe87edc3409ad6ae09368841b7d545b3b29b168`，构建来源 `fb69bbe`（2026-11-21 00:20）；旧候选包（`99e8a5e9…34a1` / `ae10336`）作废 |
| A · 首启（从 `bin\Release` 运行，删除 `settings.json`） | 启动后 1.24 s 出现主窗口，无异常弹窗；`settings.json` 重新生成，内容 `"ComPort": ""`（未选择），其余默认值正确；日志落 `%LOCALAPPDATA%\SmartEdgeHMI\logs\log-20261120.txt`（首行「日志文件目录: …」），全程 0 条 `[DBG]`，日志含「设置校验通过」，正常关闭「退出码: 0」；未选端口点「连接」，状态栏显示「请先选择串口」（不再静默）；选 COM1 后 `settings.json` 成功改写（`"ComPort": "COM1"`，468 字节），日志无「保存通信设置失败」 |
| B · 发布包解压运行（`…\soak-app`） | 日志目录探测到 `%LOCALAPPDATA%` 不可写，回退到程序目录 `logs\log-20261121.txt`（首行写明落点）；`保存通信设置失败` / `设备连接后自动下发阈值配置失败` 各出现，异常均为 `UnauthorizedAccessException: Access to the path '…\SmartEdgeHMI\settings.json.tmp' is denied.`（未签名 exe 写 `%LOCALAPPDATA%` 被本机安全软件拦截，与上一轮结论一致）；启动、串口连接（`已打开串口 COM1, 波特率 115200`）、数据入库均不受影响 |
| 环境差异（A vs B） | 同一台机器：从 `bin\Release` 运行可写，从解压到 `Agent\projects` 的发布包运行被拒；原因未查明，需在干净机复验 |
| C · 报警端到端（3 轮，最终 2 轮为修复后） | 模拟器 `node virtual-device.js json COM2 115200 --fault-window 20 15`：`[FAULT]` 计划 / 进入(t=20s) / 退出(t=35s) 三行齐全，窗口内 TX 含 `"status":4,"err_code":302`；报警 `Id=3`：`UNACK 00:17:31.61` → 操作员确认 `ACK 00:17:32.59` → `NORMAL 00:17:48.62`，`ClearedAt` 非空，`State=3` 已落库；应用日志三行齐全：`报警触发 UNACK` / `操作员确认 UNACK→ACK` / `完全恢复 ACK→NORMAL`；「一键确认全部」报警期间 `enabled=True`，恢复后 `enabled=False` |
| C · 修复前对照（同机 `Id=1`） | 日志已打印「完全恢复 ACK→NORMAL」，数据库却停在 `ACK`、`ClearedAt=NULL` —— 正是 `3ab7619` / `fb69bbe` 修掉的缺陷 |
| D · 24 小时观察（**进行中，未完成，未出结论**） | 2026-11-21 00:18:51 起，发布包（`soak-app`）+ 模拟器 1 Hz 连续上报；采样器每 300 s 写本机 `soak-data\soak.csv`；截至 00:26:38（2 次采样）：工作集 255.1 / 255.2 MB、私有 166 MB、句柄 735、`SmartEdgeHMI.db` 69,632 B、`-wal` 4,144,752 B（4.13 MB 自限）、遥测 421 → 457 行、报警 0 行、`journal_mode=wal`；日志 `[ERR]` 3 条（均为 B 段写盘拦截）、`[WRN]` 0 条；预计 2026-11-22 00:26 结束 |
| 结论 | 首启 / 默认配置 / 生产日志 / 报警触发→确认→恢复 E2E 通过；发布包写盘受本机环境限制（`bin\Release` 可写、解压包被拒，差异原因未查明）；24h 观察进行中；硬件、干净机、回滚演练未执行 |

### 2026-11-21/22 24 小时观察与回滚演练（2026-11-21 00:26:02 – 2026-11-22 00:40）

| 项 | 口径 |
| --- | --- |
| 机器与证据 | 本机（同前两轮）；24h 采样与判定证据在本机 `soak-data\`（不入库） |
| A · 观察窗口与采样 | 2026-11-21 00:26:02 → 2026-11-22 00:22:11；289 行、间隔 300 s，最大间隔 310 s（08:26:44→08:31:54），>405 s 缺口 0 次，时间戳非单调 0 次；`AppAlive=False` 0 条，PID 唯一 24308，无重启 |
| A · 资源 | 工作集首 255.1 MB → 末 97.7 MB（峰值 273.2 MB @03:46:40、最低 38.9 MB @21:47:08，后 1/3 斜率 −6.85 MB/h，全程 −61.7%，检测到 11 处 >15% 的 GC 回落）；句柄 735 → 730（峰值 769 @14:11:59，后 1/3 斜率 +1.86 个/h，全程 −5）；线程 47 → 25；CPU 累计 305.8 CPU·秒 / 86,169 秒 = 0.35% 单核 |
| A · 数据库 / WAL | `SmartEdgeHMI.db` 69,632 B → 15,454,208 B（+14.67 MB，0.613 MB/h，按此外推 30 天约 456 MB）；WAL 峰值 4,161,232 B（3.97 MB @20:47:07），末 1/10 采样极差 0；`journal_mode=wal` ×289 |
| A · 遥测 / 报警 / 日志 | 遥测 421 → 86,528 行（+86,107，0.999 行/秒，对 86,400 行期望的达成率 99.7%）；报警 0 行；`LinkEvents` 1 → 1（无重连）；日志 3,839 B → 13,739 B，`[ERR]` 3 → 3（无新增）、`[WRN]` 0 → 10 |
| A · 判定与退出 | `analyze-soak.ps1` 总判定 **PASS-WITH-NOTES**（唯一「需人工判断」项 = 那 10 条 WRN）；采样器 00:27:11 自行退出（`soak 结束`），主程序 00:29:02 正常退出（日志 `[INF] SmartEdgeHMI 应用程序正在退出。退出码: 0`） |
| A · 10 条 `[WRN]` 定性（2026-11-22 更正） | 全部为 `心跳发送异常, 3000ms 后重试`（2026-11-21 16:51:28–16:58:44）。触发源确实是测试活动（`WatchdogHeartbeatClientTests` 在全局管道名 `SmartEdgeHMI_Watchdog_Pipe` 上起了假服务端），但当时「非产品缺陷」的定性**是错的**：2026-11-22 的实验证明，同一管道名被占用会让 Watchdog 反复强杀健康主进程。这 10 条 WRN 是**真实缺陷的症状**，已作为 v1.2.0 阻塞项修复，见 [postmortem-watchdog-pipe.md](postmortem-watchdog-pipe.md) |
| A · 证据 | `soak.csv` SHA256 `f92c6999771113c846f5c7b06c67fac55a57553016c2058ff5fa3b6493b3d67e`（本机 `soak-data\`，不入库） |
| B · 回滚演练（P0-07，2026-11-22 00:30–00:40） | v1.1.0 真包（GitHub Release 资产 104,128,612 B，digest `9dcdfa43…503078` 核对一致）启动：主窗口出现，库 `TelemetryHistory 60 / AlarmHistory 1`，索引 2 个（`idx_telemetry_device`、`idx_telemetry_timestamp`）；冷备份 #1 `20261122-003239`（3 个文件 57,344 B，manifest 自校验通过）；新包覆盖解压升级（17 → 295 个文件，v1.1.0 的两个 `.pdb` 残留）；升级后启动：索引 4 个（新增 `idx_telemetry_device_timestamp`、`idx_alarm_timestamp`），**遥测 60 → 0**、报警保留 1 条；界面选 COM1 →「已连接 COM1」→ 遥测 0 → 25 → 35 行；正常退出（`退出码: 0`）；冷备份 #2 `20261122-003427`（1 个文件 32,768 B，`telemetryRows 35 / alarmRows 1`）；回滚 = 恢复 v1.1.0 二进制 + `restore-data.ps1` 恢复备份 #1 → 库回到 `60 / 1`、索引回到 2 个（升级后数据移至 `data-before-restore-20261122-003437`，未删除）；回滚后启动：主窗口出现，界面报警列表可见 `ThresholdExceeded` 行 |
| B · 升级清理旧遥测（注意事项） | 升级后首次启动，1.2.0 的数据保留任务把 v1.1.0 库里 60 行 2026-06-29 的遥测判为超过 30 天而清理；报警按 `AlarmRetentionDays=0` 保留。**这是策略行为，不是数据丢失**，但升级说明必须提醒用户：升级会按保留策略清理旧遥测（[release-notes-v1.2.0.md](release-notes-v1.2.0.md) 已知问题与升级说明已标注） |
| C · 重建与门禁（2026-11-22，`f8b3997`） | `dotnet build -c Release` 0 错误（2 条既有 NU1701）；`dotnet test -c Release` 103 通过 / 0 失败 / 0 跳过（92 → 99 → 103：报警写入侧 +7、Watchdog 心跳 +4）；`dotnet format --verify-no-changes` 退出码 0；`git diff --stat fb69bbe..f8b3997 -- SmartEdgeHMI/ SmartEdgeHMI.Watchdog/ Firmware/ Simulator/` 输出为空 → 24 小时观察跑的就是这份产品代码（新增仅测试 / 工具 / 文档）；00:36 重建候选包（SHA256 见 §7，旧候选包 `fb69bbe` 作废） |
| 环境限制 | v1.1.0 与 v1.2.0 均在写 `%LOCALAPPDATA%\SmartEdgeHMI\settings.json.tmp` 时被拒（`UnauthorizedAccessException`），与前两轮结论一致；不影响启动 / 连接 / 入库 |
| 结论 | 24h 观察 **PASS-WITH-NOTES**（唯一备注 = 10 条 WRN，后确认为管道缺陷症状，已修复）；P0-07 升级 / 回滚演练通过（条数核对 `60/1 → 走完 → 60/1`）；真实硬件验收、无开发环境干净机仍未执行，发布继续阻塞 |

### 2026-11-22 心跳管道同名串台缺陷：定性、修复与回归（11:06–11:45）

| 项 | 口径 |
| --- | --- |
| A · 修复前复现 | 先由脚本以 `maxInstances=1` 抢占 `SmartEdgeHMI_Watchdog_Pipe`，再启动候选包的 `SmartEdgeHMI.Watchdog.exe`：75 秒内主程序出现 **7 个不同 PID、6 次强杀**，周期约 11 秒；Watchdog 每 2 秒一条 `[ERR] 管道服务器循环异常` + `System.IO.IOException: 所有的管道范例都在使用中。`；主程序 7 次启动、6 次 `退出码: 0` 正常退出、**0 条心跳告警**（连上冒充者后 `hb` 照发） |
| B · 根因 | 管道名是编译期常量；Watchdog 绑定失败只重试不退出，`_lastHeartbeat` 停在启动时刻，10 秒后即杀自己刚拉起的健康进程；`_lastHeartbeat != DateTime.MinValue` 的兜底被 `StartHmiProcess()` 覆盖成当前时间而失效；客户端不校验对端身份 |
| C · 修复 | 新增 `Shared/WatchdogPipeName.cs`（管道名 = 前缀 + SHA256(程序目录小写全路径) 前 16 位，两工程链接同一源文件）；Watchdog 仅在管道服务端已绑定时才可按心跳超时杀进程（进程退出仍无条件重启）；绑定失败 ERR 降为 60 秒一条；测试改用测试目录派生的管道名，不再占用生产管道名 |
| D · 门禁（`8952e46`） | `dotnet build -c Release` **0 错误**（2 条既有 NU1701）；`dotnet test -c Release` **108 通过 / 0 失败 / 0 跳过**（103 → 108：管道名派生 +5）；`dotnet format --verify-no-changes` 退出码 **0**；CI run `44981622078` **success**，与候选包构建提交一致 |
| E · 四场景回归（修复后，同一套发布二进制） | A 正常（无冒充者）70 s：**1 个 PID**，不被杀；B 冒充者占用**派生名**：仍 **1 个 PID**（失效安全生效，未杀健康进程）；C 冒充者占用**旧写死名**：**1 个 PID**（名字已不同，无影响）；D 强杀主程序：**2 个 PID**，2 s 内拉起新 PID（Watchdog 保护未退化） |
| F · 新候选包 | `SmartEdgeHMI-1.2.0-win-x64.zip`，82,066,546 字节 / 78.26 MB，SHA256 `76e9641a…07f681`，构建自 `8952e46`；包内 `SmartEdgeHMI.exe` 与 `SmartEdgeHMI.Watchdog.exe` 的 `ProductVersion` 均为 `1.2.0+8952e46…`（SHA256 见 §7） |
| G · 遗留风险 | 心跳报文仍无鉴权；知道派生规则且与被监视程序同目录的进程理论上仍可冒充或吸收心跳（已写入 Release Notes 已知问题 8） |

### 2026-11-22 干净机验收（P0-05，`干净机`）

| 项 | 口径 |
| --- | --- |
| 机器 | Windows 10 Pro 22H2（普通用户，非管理员）；**无 .NET SDK、无 .NET 8 运行时**（仅有 .NET 10 desktop 运行时）；**无任何 COM 口**；装有**第三方安全软件 且全程运行、未加白** |
| T1 包完整性 | **通过**：82,066,546 B；SHA256 `76E9641A…07F681` 与 `.sha256`、交接信息三方一致；解压 511 文件 / 13 目录 |
| T2 无 .NET 前提 | **通过（限定）**：无 SDK、无 .NET 8；候选包 `includedFrameworks` 为 8.0.34 self-contained，启动成功（该机并非"绝对没有任何 .NET"，已如实注明） |
| T3 首启 | **通过**：进程创建到主窗口 **1.364 s**；无应用错误框；`settings.json` 与日志自动生成 |
| T4 设置落盘 | **通过（替代口径）**：波特率 115200→9600、阈值 60→65（默认 60，65 必来自落盘）；退出码 0；重启后保留；全量日志 0 命中 `保存通信设置失败` / `settings.json.tmp` / `UnauthorizedAccessException` / `Access is denied`。**T4-COM1 未执行**（该机端口下拉为空且不可编辑） |
| T5 日志 | **通过**：`%LOCALAPPDATA%\SmartEdgeHMI\logs\log-20261122.txt`；Information；0 ERR / 0 WRN / 0 DBG |
| T6 双开 | **通过**：弹窗标题「系统提示」、正文「SmartEdgeHMI 系统已在运行中, 请勿重复启动！」；第二实例退出、第一实例存活 |
| T7 Watchdog | **通过**：管道名 `SmartEdgeHMI_Watchdog_Pipe_0abb82f50d5a9a77`（**我方按工作目录独立复算，逐字符一致**）；强杀主程序后 **0.26 s** 拉起新 PID 并重新连接管道 |
| T8 包审计 | **通过**：511 文件（503 `.dll` / 5 `.json` / 3 `.exe`），0 个 `.cs/.csproj/.sln/.pdb/.js/.map/secrets.h`；两 EXE 产品版本 `1.2.0+8952e46…`；`NotSigned` |
| T9 数据链路 | **未执行**（无串口设备；按要求未安装驱动或虚拟串口） |
| 关键结论 | 写盘失败的根因是**运行目录的完整性级别**而非杀软：从低完整性目录启动时进程为 Low，无法写中完整性的 `%LOCALAPPDATA%`；同一台机器换到桌面普通目录后设置写入、重启保留、日志全部正常，第三方安全软件全程运行未拦截 |
| 回传缺件 | 报告引用的 `SmartEdgeHMI-acceptance-return\`（含 `localappdata-SmartEdgeHMI\`、`package-audit.txt`、`low-integrity-diagnosis.txt`）未随报告回传；**可独立复核的项（包哈希、包内容 511/0、管道名复算）已由本机重算并一致**，日志级证据仍缺原件 |
