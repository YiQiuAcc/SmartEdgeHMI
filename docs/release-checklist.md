# 发布检查表（Release Checklist）

> 适用范围：SmartEdgeHMI Windows 便携 zip 发布（主程序 + Watchdog）。
> 使用方式：发布负责人逐项勾选；发布前存在未通过项时不得打 tag（本表 §10 首项属发布后动作，不计入发布前门禁，但也不得提前勾选）。
> 性能数字如需引用，一律出自 [performance-benchmarks.md](performance-benchmarks.md)，不得新造口径。
> 本表为 v1.2.0 发布实例；历史走查记录见 §12 归档链接。

## 1. 版本与分支

- [x] 版本号确定（SemVer，如 v1.2.0），与 Release Notes / tag 名一致（版本 1.2.0）
- [x] 发布分支为 `main`，工作区干净：`git status` 无未提交 / 未跟踪文件（文档提交后复核）
- [x] tag 指向本次已验证的提交，tag 名与版本一致（`v1.2.0` 为注解标签，指向发布提交（本文档随该提交发布；后续文档修订不改变 tag）。**说明**：发布包构建于 `1744cd2`，tag 提交相对它只含文档，源码与二进制零差异——`git diff 1744cd2 v1.2.0 -- SmartEdgeHMI/ SmartEdgeHMI.Watchdog/ Firmware/ Simulator/ tools/ .github/` 输出为空即可验证；包内 `ProductVersion = 1.2.0+1744cd2…` 因此必然与 tag 提交号不同，这是确定性构建（版本号内嵌 `SourceRevisionId`）与「哈希必须先写进文档」两个约束叠加的结果，已在 §7 与 Release Notes §8 记录）
- [x] CI 在该提交上全绿（restore / build / test / format 四步）（CI 运行号用 `gh run list --commit $(git rev-parse v1.2.0) --limit 1` 查询）

## 2. 构建与测试

- [x] `pwsh tools/package.ps1 -Version {版本}` 一键完成 test / publish / zip / SHA256（隐含 restore + build；勿用 `-SkipTests`；确需跳过时须另行补跑测试并记录），测试全绿，通过数：129（2026-11-23 清洁构建后在 `main` @ `1744cd2` 实测 `dotnet test -c Release --no-build`：**129 通过 / 0 失败 / 0 跳过**；92 → 99 → 103 → 108 → 129，末段 +21 为真机验收暴露的三条 P1（协议持久化 / 启动自动连接 / 重连竞态）的回归测试；发布包随后由 `pwsh tools/package.ps1 -Version 1.2.0` 在同一提交上产出，见 §7）
- [x] `dotnet format SmartEdgeHMI.sln --verify-no-changes --no-restore` 退出码 0（2026-11-23 清洁构建后实测退出码 0）

## 3. 基准数据是否更新

- [x] 确认自上次基准运行后，LTTB / SQLite 写入路径代码是否变更；若变更，重跑 `SmartEdgeHMI.Benchmarks` 并更新 [performance-benchmarks.md](performance-benchmarks.md)（`287c357` 之后 `SmartEdgeHMI/Data` 仅 `a214390` 新增保留服务；`a214390` 之后本轮改动集中在设置服务、启动日志、报警状态机、模拟器与 ViewModel，**未触及 LTTB 与 SQLite 写入热路径**，无需重跑）
- [x] README 中引用的性能数字与基准文档逐条一致，无超出实测边界的表述（Release Notes §4 与本轮引用均出自 performance-benchmarks.md §8.3 / §8.5 / §9 口径）
- [x] 复现命令（基准文档 §7）在当前代码上可执行（沿用 2026-11-17 实测记录：`dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --verify`：10000 / 100000 / 1000000 三点均精确返回 1000 条、首尾保留，PASSED）

## 4. 数据库 schema / 迁移与 WAL 兼容

> 本轮未变更数据库 schema，迁移证据沿用 2026-11-17 实测。

- [x] 用新版本打开旧版本（v1.1.0）生成的数据库文件：自动迁移成功，无 schema 报错（v1.1.0 tag 不可编译，旧库由 f556af6 生成（同 schema））
- [x] 迁移后旧数据可读：报警历史 / 遥测历史条数与旧版本一致（抽查）（迁移前 1800 遥测 / 180 报警，迁移后完全一致）
- [x] `PRAGMA journal_mode` 返回 `wal`（WAL 随库文件持久化）（迁移后实测仍为 wal）
- [x] 已知口径确认：`synchronous` 为连接级设置，新连接实际运行在默认 FULL（见基准文档 §6.4），发布版验证无数据损坏 / 死锁（本轮迁移与保留清理端到端实测无异常）
- [x] 降级路径说明：若本次 schema 有不可逆变更，须在 Release Notes 标注"回滚需恢复备份"（本轮变更为补建索引 + 保留策略，[release-notes-v1.2.0.md](release-notes-v1.2.0.md) §7 已标注）

## 5. appsettings 配置检查

- [x] 现场可调参数（批量写入 / 刷盘间隔 / 轮询间隔 / Watchdog 超时等）均在 `SmartEdgeHMI/appsettings.json`，无研发硬编码泄漏到现场配置面（`appsettings.json` 已入包，包内容核对见 §7）
- [x] 默认值开箱即用（首启无需手改即可连接模拟器 / 设备）✅ 首启不再因非法默认端口失败（`ComPort` 默认空＝未选择），首启 1.24 s 进入主界面、设置文件自动生成；需在界面选择实际端口（未实现自动选择）。本机发布包写 `%LOCALAPPDATA%` 失败，根因是低完整性目录，属环境限制，见 [归档走查记录](archive/release-walkthrough-2026-11.md)。
- [x] 发布包内无凭据残留：`Firmware/src/secrets.h`、测试密钥、本地路径不入包（包内容核对：0 个源码 / 密钥文件入包，无 `secrets.h`）
- [x] 日志级别 / 路径配置合理（生产为 Information 及以上，路径可写）✅ 默认 Information（`appsettings.json` 可调），路径 `%LOCALAPPDATA%\SmartEdgeHMI\logs`，不可写时回退程序目录（本机实测走到回退分支并正常写盘）。

## 6. Watchdog 联调

- [x] `SmartEdgeHMI.Watchdog` exe 与主程序成对打包（同目录 / 相对路径正确）（同目录成对入包，包内容核对见 §7）
- [x] 正常运行：3s 间隔心跳持续，Watchdog 不误杀（2026-11-17 实测 18 秒内 9 次心跳无误差；2026-11-22 在干净机 T7 与真机 HW-01 / HW-06 期间均无误杀）
- [x] 强杀主进程 → Watchdog 在 10s 心跳超时内重启主程序 → 心跳恢复（2026-11-17 实测强杀后 1.8 s 重启新 PID，心跳恢复；2026-11-22 复测：干净机 0.26 s 拉起新 PID 并重连管道，真机 HW-06 强杀后 1.9 s 拉起、**7.8 s 内自动重连并恢复 Modbus 轮询**，日志可见「HMI 主进程已退出 (ExitCode: -1), 正在重启」）
- [x] 防呆启动：双开主程序时第二个实例被 Mutex 拦截（2026-11-17 实测弹「系统提示 / SmartEdgeHMI 系统已在运行中, 请勿重复启动！」→ 退出码 0，首实例存活；2026-11-22 干净机 T6 复验通过）
- [x] 心跳管道被其他进程占用时不得反复强杀健康主进程（2026-11-22 四场景回归：A 正常场景 1 个 PID 不被杀；B 冒充者占用**派生名**仍不被杀（失效安全生效）；C 冒充者占用**旧写死名**完全无影响；D 强杀主程序后 2 s 内拉起新 PID（保护未退化）。修复前同一场景 75 秒内强杀 6 次，见 [故障复盘合集](postmortems.md)）
- [x] 重启后串口重开成功（无端口占用残留导致的连续失败）（**2026-11-22 真机验证**：HW-06 强杀主进程后 Watchdog 拉起新进程，自动重开 COM4 并在 7.8 s 内恢复 Modbus 轮询、零人工操作；HW-02 拔插后 10–20 s 自行恢复。修复前同一场景会永久停住，正是 `275a6d6` 修掉的竞态，见 [真机验收记录（归档）](archive/hardware-validation-2026-11-22.md)）

## 7. 打包与校验和

- [x] 打包执行：`pwsh tools/package.ps1 -Version {版本}`（publish / 压缩 / SHA256 一键完成，产物输出至 `dist/`）（2026-11-23 **清洁构建**实测 `pwsh tools/package.ps1 -Version 1.2.0`：先删除全部 `bin/obj/.vs/.pio/node_modules/dist` 构建产物，从零还原后打包；构建来源 `main` @ `1744cd2`，CI 运行号用 `gh run list --commit 1744cd2` 查询；此前 `275a6d6`、`c9997a5` 候选包作废，见下方 SHA256 项）
- [x] 产物命名：`SmartEdgeHMI-{版本}-win-x64.zip`（脚本按 `-Version` / `-Runtime` 生成，tag 前缀 `v` 已去除）（产物 `SmartEdgeHMI-1.2.0-win-x64.zip`，82,066,878 字节 / 78.27 MB；511 条目 = 503 `.dll` / 5 `.json` / 3 `.exe`）
- [x] 包内容核对：主程序 exe + Watchdog exe + `appsettings.json` + 运行所需依赖；不含源码 / 测试 / 基准工程 / 模拟器（含 `SmartEdgeHMI.exe` / `SmartEdgeHMI.Watchdog.exe` / `appsettings.json`；0 个 `.cs/.csproj/.sln/.pdb/.js/.map`、无 `secrets.h`）
- [x] 干净目录（及一台无开发环境的机器）解压后可直接启动（本机空白目录解压后直接启动通过；**干净机已于 2026-11-22 验收通过**：一台无开发环境的 Windows 10 机器，无 .NET SDK、无 .NET 8 运行时，解压 511 文件后启动 1.364 s 出现主窗口，见 [归档走查记录](archive/release-walkthrough-2026-11.md) 与 Release Notes 已知问题 2）
- [x] SHA256 校验和（脚本自动写入 `dist/<zip>.sha256`，格式 `<hash>  <文件名>`），记录到 Release Notes：
      `SHA256: 0c1869d9fa93a62955fbb171100ef73034208c05aef45b8b4736093a3611e949`（旧候选包 `6fc86aa33f8a52c81f0504ff44f2f9cb85276017e1e7a3c378b8c72428876077` / `275a6d6` 作废，`76e9641ab07478a3633b850ee66052db5d8bf9e825ede159519bd6b96607f681` / `c9997a5` 作废，`c02c87aa5e6fcfbb04c81e62cb3a8685ce86c2bdcefc6072f06d5962ce6e230b` / `0168071` 作废，`24fa400406a1b456fef5d6513fe87edc3409ad6ae09368841b7d545b3b29b168` / `5a7c383` 作废；更早 `99e8a5e9…34a1` / `6250434` 已作废），2026-11-23 清洁构建时由脚本记录；此前发布包 `4a2d4920…a440fd` 已作废；`pwsh tools/verify-release.ps1 -Version 1.2.0` 退出码 0 可复验

## 8. Release Notes

- [x] 三段齐全：新增 / 修复 / 已知问题（[release-notes-v1.2.0.md](release-notes-v1.2.0.md) §2 / §3 / §5）
- [x] 修复项覆盖：串口关闭 CTS 竞态（`8d89037`）及相应异常观察器（Release Notes §3）
- [x] 性能结论引用基准实测数字，不使用"60FPS""零分配"等未实测泛化表述（数字全部出自 performance-benchmarks.md §8.3 / §8.5 / §9；趋势查询如实写"耗时持平"）
- [x] 硬件验收状态如实标注（未执行 / 已执行，链接验收记录）（**已执行并复验**：2026-11-22 真机 NodeMCU v2 + CH340/COM4 两轮，6 项中 5 项通过（带备注）、HW-06 第一轮失败并暴露 3 条 P1，修复（`275a6d6`）后第二轮通过；记录见 [真机验收记录（归档）](archive/hardware-validation-2026-11-22.md)）
- [x] 升级 / 回滚说明包含数据库备份提示（Release Notes §6 / §7）

## 9. 回滚方案（含数据库备份）

- [x] 上一版本安装包与校验和仍可下载（当前：v1.1.0 zip 存档于 GitHub Releases）（2026-11-22 演练取用 GitHub Release `v1.1.0` 资产真包：104,128,612 字节，digest `9dcdfa43…503078`，核对一致）
- [x] 回滚步骤成文并演练过一次：退出程序（含 Watchdog）→ 恢复上一版 zip → 启动验证连接与数据（2026-11-22 00:30–00:40 实机演练通过：v1.1.0 真包启动 → 升级前冷备份 → 1.2.0 候选包覆盖升级 → 升级后启动 / 连接验证 → 恢复 v1.1.0 二进制 + `restore-data.ps1` 恢复备份库 → 回滚后启动验证；详见 [归档走查记录](archive/release-walkthrough-2026-11.md)「2026-11-21/22 24 小时观察与回滚演练」表）
- [x] **数据库备份**：发布前对用户数据目录 `*.db`（连同 `-wal` / `-shm`）做冷备份；回滚前**再次**备份新版本已写入的数据，避免回滚覆盖（已执行：升级前冷备份 `20261122-003239`（3 个文件 57,344 B，manifest 自校验通过，`telemetryRows 60 / alarmRows 1`）；升级后冷备份 `20261122-003427`（1 个文件 32,768 B，`telemetryRows 35 / alarmRows 1`）；备份存于本机演练目录，不入库）
- [x] 备份文件校验（可打开、条数正确），存放位置与保留期限已约定（两份备份 manifest 自校验通过、条数正确；回滚恢复备份 #1 后库回到 `60 / 1`、索引回到 2 个，条数核对 `60/1 → 走完 → 60/1` 成立；存放位置为本机演练目录（不入库））
      **保留策略（2026-11-22 约定）**：每个 `<备份根>\<yyyyMMdd-HHmmss>\` 为一份完整备份；保留**最近 10 份且不超过 90 天**（先到为准）——按 30 天库增长约 456 MB 估算，10 份上限约束了备份盘的长期占用。`backup-data.ps1` **不自动删除任何备份**（备份工具自动清理的误删风险高于磁盘收益），超出策略的由运维手工清理；`restore-data.ps1` 生成的 `data-before-restore-*` 目录在恢复确认后至少保留 30 天再删

## 10. 发布后 smoke 测试

- [ ] 从 Release 页下载发布包 → 校验 SHA256 → 解压启动成功（**部分完成**：2026-11-23 已建立 [Release 页](https://github.com/YiQiuAcc/SmartEdgeHMI/releases/tag/v1.2.0)，资产 `SmartEdgeHMI-1.2.0-win-x64.zip`（82,066,878 B）与 `.sha256` 已上传；已从 Release 页回下载，SHA256 = `0c1869d9…61e949` 与文档一致、包内 511 条目；**解压启动待补做**，完成后勾选）
- [x] 连接模拟器（或虚拟串口）：数据入库、图表渲染、指令下发（阈值 / 复位 / LED）、报警触发与确认各验证 1 次（本机实测：JSON 模式 COM1↔COM2 虚拟串口对，遥测以约 1 行/秒入库（1200 → 1245 行）、当前值实时刷新 24.4~25.3 °C；「设备紧急复位」下发后模拟器收到 `{"action":3}` 并执行「设备复位 -> 恢复初始状态」；阈值滑杆 30→20 生效并落盘。**报警触发 / 确认已完成**：修复后 3 轮 E2E，报警 `Id=3` 走完 `UNACK → ACK → NORMAL` 且 `ClearedAt` 落库，应用日志三行齐全，按钮「一键确认全部」恢复后自动禁用（模拟器故障窗口注入，见 [归档走查记录](archive/release-walkthrough-2026-11.md) 第二轮记录））
- [x] 断开 → 重连 1 次，日志无未观察异常（`ObserveAndLogAsync` 无 ERROR 输出）（本机实测：断开后「串口 COM1 已断开」，重连后「已打开串口 COM1, 波特率 115200」→ 恢复「已连接 COM1」并继续收数；无未观察 Task 异常；日志中 `保存通信设置失败` / `设备连接后自动下发阈值配置失败` 均源于本机对未签名 exe 的 `%LOCALAPPDATA%` 写入拦截，见 [归档走查记录](archive/release-walkthrough-2026-11.md)）
- [x] 强杀主进程 → Watchdog 重启 → 自动恢复 1 次（**真机实测（`275a6d6`）**：强杀 pid=21344 后看门狗 1.9 s 拉起 pid=4720，自动连 COM4 并恢复 Modbus 轮询，**从强杀到恢复采样共 7.8 s、零人工操作、协议未回落**；第一轮同一操作用了 1.2 s 重启但**不自动重连、协议回落 JSON**，即为 HW-06 失败点，见 [真机验收记录（归档）](archive/hardware-validation-2026-11-22.md) §5.3）
- [x] smoke 结果（含时间 / 机器）回填到 Release 说明或本表附件（回填见 [归档走查记录](archive/release-walkthrough-2026-11.md)）

## 11. 责任人与签字

| 角色 | 责任人 | 日期 | 结论 |
| --- | --- | --- | --- |
| 发布负责人（lead） | lead | 2026-11-22 | ☑ 通过　☐ 阻塞（P0-01～P0-08 全部完成：P0-06 于 2026-11-22 真机执行两轮，第一轮 HW-06 失败并暴露 3 条 P1，修复 `275a6d6` 后第二轮六项全过含 125 分钟浸泡；P0-05 干净机已完成，其串口两项由本机虚拟串口替代覆盖；遗留 F-4 / F-6 已知限制转入 v1.2.1 的 P1-04 / P1-05。发布包 82,066,878 B / SHA256 `0c1869d9…61e949`，tag `v1.2.0` 已推送；§10 首项〔Release 页下载验证〕属发布后动作，Release 页已于 2026-11-23 建立，下载与 SHA256 已复核，解压启动待补做，不因本签字视为已通过） |
| 质量复核（ZCode） | ZCode | 2026-11-17 | ☑ 通过（备注：文档口径与证据逐条核对）　☐ 阻塞 |

> 全部通过后方可发布 Release 并打 tag；任何一项阻塞即冻结发布，修复后重新走查本表。

## 12. 历史走查记录

v1.2.0 发布前的本机走查、24h 观察、回滚演练、干净机、真机验收与打包记录已归档：
[archive/release-walkthrough-2026-11.md](archive/release-walkthrough-2026-11.md)。

当前发布只需维护本表 §1～§11 的检查项与签字；历史记录不再随表更新。
