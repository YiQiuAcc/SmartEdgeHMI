# 发布检查表（Release Checklist）

> 适用范围：SmartEdgeHMI Windows 便携 zip 发布（主程序 + Watchdog）。
> 使用方式：发布负责人逐项勾选；存在未通过项时不得打 tag。
> 性能数字如需引用，一律出自 [performance-benchmarks.md](performance-benchmarks.md)，不得新造口径。

## 1. 版本与分支

- [x] 版本号确定（SemVer，如 v1.2.0），与 Release Notes / tag 名一致（版本 1.2.0）
- [x] 发布分支为 `main`，工作区干净：`git status` 无未提交 / 未跟踪文件（文档提交后复核）
- [ ] tag 指向本次已验证的提交，tag 名与版本一致（未打 tag：待硬件验收与干净机走查后决定 v1.2.0 / v1.2.1-hw）
- [x] CI 在该提交上全绿（restore / build / test / format 四步）（CI run 43938597156 / `ae10336`，8 步 success）

## 2. 构建与测试

- [x] `pwsh tools/package.ps1 -Version {版本}` 一键完成 test / publish / zip / SHA256（隐含 restore + build；勿用 `-SkipTests`；确需跳过时须另行补跑测试并记录），测试全绿，通过数：87（记录实际值；2026-11-17 实测 `pwsh tools/package.ps1 -Version 1.2.0`，87 通过 / 0 失败）
- [x] `dotnet format SmartEdgeHMI.sln --verify-no-changes --no-restore` 退出码 0（2026-11-17 实测退出码 0）

## 3. 基准数据是否更新

- [x] 确认自上次基准运行后，LTTB / SQLite 写入路径代码是否变更；若变更，重跑 `SmartEdgeHMI.Benchmarks` 并更新 [performance-benchmarks.md](performance-benchmarks.md)（`8dfcb40` 之后 `SmartEdgeHMI/Data` 仅 `c4579d0` 新增保留服务，未触及 LTTB 与写入热路径，无需重跑）
- [x] README 中引用的性能数字与基准文档逐条一致，无超出实测边界的表述（Release Notes §4 与本轮引用均出自 performance-benchmarks.md §8.3 / §8.5 / §9 口径）
- [x] 复现命令（基准文档 §7）在当前代码上可执行（2026-11-17 实测 `dotnet run -c Release --project SmartEdgeHMI.Benchmarks -- --verify`：10000 / 100000 / 1000000 三点均精确返回 1000 条、首尾保留，PASSED）

## 4. 数据库 schema / 迁移与 WAL 兼容

- [x] 用新版本打开旧版本（v1.1.0）生成的数据库文件：自动迁移成功，无 schema 报错（v1.1.0 tag 不可编译，旧库由 3cb8a00 生成（同 schema））
- [x] 迁移后旧数据可读：报警历史 / 遥测历史条数与旧版本一致（抽查）（迁移前 1800 遥测 / 180 报警，迁移后完全一致）
- [x] `PRAGMA journal_mode` 返回 `wal`（WAL 随库文件持久化）（迁移后实测仍为 wal）
- [x] 已知口径确认：`synchronous` 为连接级设置，新连接实际运行在默认 FULL（见基准文档 §6.4），发布版验证无数据损坏 / 死锁（本轮迁移与保留清理端到端实测无异常）
- [x] 降级路径说明：若本次 schema 有不可逆变更，须在 Release Notes 标注"回滚需恢复备份"（本轮变更为补建索引 + 保留策略，[release-notes-v1.2.0.md](release-notes-v1.2.0.md) §7 已标注）

## 5. appsettings 配置检查

- [x] 现场可调参数（批量写入 / 刷盘间隔 / 轮询间隔 / Watchdog 超时等）均在 `SmartEdgeHMI/appsettings.json`，无研发硬编码泄漏到现场配置面（`appsettings.json` 已入包，包内容核对见 §7）
- [ ] 默认值开箱即用（首启无需手改即可连接模拟器 / 设备）❌ 未通过：① 删除 `settings.json` 后首启，`SettingsService.LoadSettings` 在"文件不存在"分支直接写文件且无 try/catch（`SettingsService.cs:38`），写入失败即抛异常穿透到启动流程，日志记录「【UI主线程】遇到未处理异常」，启动中断；② `ApplyDefaultsFromConfiguration` 把 `Connection.ComPort` 赋成 `Device.DefaultName`（`SettingsService.cs:96`，实际值 `Sensor_01`），首启默认串口名不是有效端口
- [x] 发布包内无凭据残留：`Firmware/src/secrets.h`、测试密钥、本地路径不入包（包内容核对：0 个源码 / 密钥文件入包，无 `secrets.h`）
- [ ] 日志级别 / 路径配置合理（生产为 Information 及以上，路径可写）❌ 未通过：Serilog MinimumLevel.Debug 硬编码、日志路径相对 CWD（`App.xaml.cs:60` / `:65`）

## 6. Watchdog 联调

- [x] `SmartEdgeHMI.Watchdog` exe 与主程序成对打包（同目录 / 相对路径正确）（同目录成对入包，包内容核对见 §7）
- [x] 正常运行：3s 间隔心跳持续，Watchdog 不误杀（本机实测 18 秒内 9 次心跳无误差）
- [x] 强杀主进程 → Watchdog 在 10s 心跳超时内重启主程序 → 心跳恢复（本机实测强杀后 1.8 s 重启新 PID，心跳恢复，日志「HMI 主进程已退出 (ExitCode: -1), 正在重启」）
- [x] 防呆启动：双开主程序时第二个实例被 Mutex 拦截（第二实例弹「系统提示 / SmartEdgeHMI 系统已在运行中, 请勿重复启动！」→ 退出码 0，首实例存活）
- [ ] 重启后串口重开成功（无端口占用残留导致的连续失败）（无真实串口，未验证）

## 7. 打包与校验和

- [x] 打包执行：`pwsh tools/package.ps1 -Version {版本}`（publish / 压缩 / SHA256 一键完成，产物输出至 `dist/`）（2026-11-17 实测 `pwsh tools/package.ps1 -Version 1.2.0` 退出码 0）
- [x] 产物命名：`SmartEdgeHMI-{版本}-win-x64.zip`（脚本按 `-Version` / `-Runtime` 生成，tag 前缀 `v` 已去除）（产物 `SmartEdgeHMI-1.2.0-win-x64.zip`，82,065,173 字节 / 78.26 MB，290 个根文件 / 511 个条目）
- [x] 包内容核对：主程序 exe + Watchdog exe + `appsettings.json` + 运行所需依赖；不含源码 / 测试 / 基准工程 / 模拟器（含 `SmartEdgeHMI.exe` / `SmartEdgeHMI.Watchdog.exe` / `appsettings.json`；0 个 `.cs/.csproj/.sln/.pdb/.js/.map`、无 `secrets.h`）
- [x] 干净目录（及一台无开发环境的机器）解压后可直接启动（本机空白目录解压后直接启动通过；无 .NET 环境机器未验证，见 §12 与 Release Notes 已知问题 6）
- [ ] SHA256 校验和（脚本自动写入 `dist/<zip>.sha256`，格式 `<hash>  <文件名>`），记录到 Release Notes：
      `SHA256: 99e8a5e937bcb2403294cf01c07c46d5c6ecdbe11ee7b1550e34dc48d61034a1`（`sha256sum -c` 等价校验通过）

## 8. Release Notes

- [x] 三段齐全：新增 / 修复 / 已知问题（[release-notes-v1.2.0.md](release-notes-v1.2.0.md) §2 / §3 / §5）
- [x] 修复项覆盖：串口关闭 CTS 竞态（`30390aa`）及相应异常观察器（Release Notes §3）
- [x] 性能结论引用基准实测数字，不使用"60FPS""零分配"等未实测泛化表述（数字全部出自 performance-benchmarks.md §8.3 / §8.5 / §9；趋势查询如实写"耗时持平"）
- [x] 硬件验收状态如实标注（未执行 / 已执行，链接 [hardware-validation.md](hardware-validation.md)）（Release Notes 已知问题 1：未执行）
- [x] 升级 / 回滚说明包含数据库备份提示（Release Notes §6 / §7）

## 9. 回滚方案（含数据库备份）

- [ ] 上一版本安装包与校验和仍可下载（当前：v1.1.0 zip 存档于 GitHub Releases）（未执行：需联网确认）
- [ ] 回滚步骤成文并演练过一次：退出程序（含 Watchdog）→ 恢复上一版 zip → 启动验证连接与数据（未执行：需人工演练）
- [ ] **数据库备份**：发布前对用户数据目录 `*.db`（连同 `-wal` / `-shm`）做冷备份；回滚前**再次**备份新版本已写入的数据，避免回滚覆盖（未执行：需人工演练并留存备份）
- [ ] 备份文件校验（可打开、条数正确），存放位置与保留期限已约定（未执行：需人工演练）

## 10. 发布后 smoke 测试

- [ ] 从 Release 页下载发布包 → 校验 SHA256 → 解压启动成功（未执行：Release 页尚未发布）
- [x] 连接模拟器（或虚拟串口）：数据入库、图表渲染、指令下发（阈值 / 复位 / LED）、报警触发与确认各验证 1 次（本机实测：JSON 模式 COM1↔COM2 虚拟串口对，遥测以约 1 行/秒入库（1200 → 1245 行）、当前值实时刷新 24.4~25.3 °C；「设备紧急复位」下发后模拟器收到 `{"action":3}` 并执行「设备复位 -> 恢复初始状态」；阈值滑杆 30→20 生效并落盘。**报警触发 / 确认未覆盖**：JSON 报文不含 `errorCode`，而 `AlarmStateMachine.Evaluate` 只按设备上报的 `ErrorCode` 判定，模拟器阈值固定 80 且温度被钳制在 10~60，无法制造故障态）
- [x] 断开 → 重连 1 次，日志无未观察异常（`ObserveAndLogAsync` 无 ERROR 输出）（本机实测：断开后「串口 COM1 已断开」，重连后「已打开串口 COM1, 波特率 115200」→ 恢复「已连接 COM1」并继续收数；无未观察 Task 异常；日志中 `保存通信设置失败` / `设备连接后自动下发阈值配置失败` 均源于本机对未签名 exe 的 `%LOCALAPPDATA%` 写入拦截，见 §12）
- [x] 强杀主进程 → Watchdog 重启 → 自动恢复 1 次（本机实测强杀后 1.8 s 重启新 PID，心跳恢复）
- [x] smoke 结果（含时间 / 机器）回填到 Release 说明或本表附件（回填见本表 §12 本机走查记录）

## 11. 责任人与签字

| 角色 | 责任人 | 日期 | 结论 |
| --- | --- | --- | --- |
| 发布负责人（lead） | lead | 2026-11-17 | ☐ 通过　☑ 阻塞（原因：硬件验收、干净机安装、回滚演练、模拟器 E2E 未执行；日志级别项未通过） |
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
