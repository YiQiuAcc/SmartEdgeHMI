# SmartEdgeHMI v1.2.0 发布说明

> 版本 1.2.0 ｜ 日期 2026-11-17 ｜ 目标框架 .NET 8 (win-x64, self-contained)
> 状态：**候选包已构建，未发布**（阻塞项见「已知问题」）

## 1. 摘要

本版本把数据层三类高频查询（报警历史 / 报警上下文 / 趋势）从"整表读进内存"改为 SQL 查询下推与分桶取样，并补 `(DeviceId, Timestamp)` 复合索引，长期运行的大库不再拖垮内存与 GC；新增遥测数据保留策略（默认 30 天，报警不删），库文件不再无限膨胀。发布工程化侧补齐 `tools/package.ps1` 一键打包（publish / zip / SHA256）、CI 覆盖率产物与漏洞扫描；应用侧新增启动配置校验（9 参数，坏配置拒绝启动）与日志批量裁剪。候选包已在本机完成桌面走查（启动冒烟 / 双开拦截 / Watchdog 强杀重启 / 旧库迁移 / 保留清理 / 包内容与校验和），**未打 tag、未发布**，阻塞项见 §5。

## 2. 新增

均以用户可见效果 + 证据链接方式列出：

| 特性 | 用户可见效果 | 证据 |
| --- | --- | --- |
| 报警历史查询下推 | 报警历史列表改为 SQL 内按设备 / 时间 / 编码过滤并限量，20 万行遥测 + 5000 行报警规模下查询耗时 8 589 µs → 118 µs、内存分配 1 930 KB → 25.89 KB，大库下打开报警页不再卡顿 | [performance-benchmarks.md §8.3](performance-benchmarks.md) |
| 报警上下文查询重写 | 打开某条报警的上下文不再整表 GroupJoin，411,851.8 µs / 86,713 KB → 1,111.7 µs / 270.22 KB（约 370×），内存分配 -99.7% | [performance-benchmarks.md §8.3](performance-benchmarks.md) |
| 趋势查询 SQL 分桶 | 加载 30 天趋势图的内存分配 80,672 KB → 765.13 KB（-99.1%），耗时持平（见 §5 已知问题 4）；数据量再涨十倍也不会一次性对象化全部原始行 | [performance-benchmarks.md §8.3](performance-benchmarks.md) |
| 遥测复合索引 `(DeviceId, Timestamp)` | 按设备窗口查询遥测不再扫描放大（591 ms → 1.1 ms，见 §3 修复 3） | [performance-benchmarks.md §8.4](performance-benchmarks.md) |
| 数据保留策略 | 可配置遥测保留天数（默认 30 天）与批数上限，报警默认不删；启动后自动清理过期遥测，库文件不再无限膨胀。本机端到端实测：45 天种子数据 + 30 天保留，清理后遥测 1800 → 1200 行、报警 180 行未动 | [data-retention.md](data-retention.md) · [release-checklist.md §12](release-checklist.md) |
| `tools/package.ps1` 一键打包 | 一条命令完成 test / publish / zip / SHA256，产物输出至 `dist/`，三次重跑幂等；发布不再依赖手工多步操作 | [release-checklist.md §7](release-checklist.md) · [next-steps-v1.2.md §8](next-steps-v1.2.md) |
| CI 覆盖率产物与漏洞扫描 | 每次 CI 运行上传测试覆盖率 artifact，并对依赖做漏洞扫描（informational），供应链问题可提前发现 | [next-steps-v1.2.md §8](next-steps-v1.2.md)（V12-14） |
| 启动配置校验（9 参数） | 配置非法（如 BaudRate=0 / PollingIntervalMs=5 / MaxLogEntries=10）时启动即弹窗逐条列出非法参数并指向 `settings.json` 路径，退出码 1，不再带病运行；配置合法时日志输出「设置校验通过」 | [release-checklist.md §12](release-checklist.md) |
| 日志批量裁剪 | 内存日志按 `MaxLogEntries` 上限批量裁剪，长时间运行不再无界累积 | [next-steps-v1.2.md §8](next-steps-v1.2.md)（V12-15） |

## 3. 修复

| 问题 | 修复与用户可见效果 | 证据 |
| --- | --- | --- |
| 串口关闭后访问已释放 CTS 导致的未观察异常 | 关闭串口后不再访问已释放的 CTS 对象（提交 `30390aa`），并新增 fire-and-forget 异常观察器，断开 / 重连不再出现未观察 Task 异常刷屏 | [postmortem-serial-dispose.md](postmortem-serial-dispose.md) |
| 应用工程误引 `coverlet.collector`（测试工具包） | 已移除（提交 `cdc0461`），发布包减少 19 个测试工具文件，包更纯、体积更小 | [release-checklist.md §7](release-checklist.md) |
| 报警上下文查询缺复合索引时的 591 ms 扫描放大 | 窗口查询曾走单列索引并扫约 10 万行；补复合索引后计划变为索引直接 `SEARCH`，591 ms → 1.1 ms。该方法论记录保留：**查询下推的收益依赖索引是否匹配查询形态，不做前后基准无法发现"优化变慢"** | [performance-benchmarks.md §8.4](performance-benchmarks.md) |

## 4. 性能

数字一律出自 [performance-benchmarks.md](performance-benchmarks.md)（§8.3 查询下推、§8.5 .NET 8.0.34 复测、§9 写放大），不另造口径：

| 场景 | 改造前 | 改造后 | 说明 |
| --- | ---: | ---: | --- |
| 报警历史查询 | 8 589 µs / 1,930 KB | **118 µs / 25.89 KB** | 约 73× 快，分配 -98.7% |
| 报警上下文查询 | 411,851.8 µs / 86,713 KB | **1,111.7 µs / 270.22 KB** | 约 370× 快，分配 -99.7% |
| 趋势查询（20 万行） | 80,672 KB | **765.13 KB** | **耗时持平**（CI 内噪声），收益仅在内存与长期稳定性 |
| 写入吞吐（50 条/批） | 19.6 万行/秒 | **14.8 万行/秒** | 复合索引代价，1.21×–1.33× 写放大；相对单设备 1 Hz 遥测仍有四个数量级余量 |

## 5. 已知问题（含未执行项与环境限制）

1. **真实硬件验收未执行**：无 ESP8266 / USB 串口芯片，[hardware-validation.md](hardware-validation.md) 测试矩阵六项仍为「未执行」，电气噪声 / 断帧 / 拔插等真实设备路径完全未验证。
2. **`v1.1.0` tag 不可编译**（`WatchdogHeartbeatClient.cs(41,64)` CS0103：`_settingsService` 不存在）：本次候选包由 `main` 的 `ae10336` 构建；v1.1.0 发布包来源与该 tag 不一致，需追溯。
3. **生产日志级别为 `Debug` 且日志路径相对 CWD**（`App.xaml.cs:60` / `:65`）：未按现场 Information 口径收敛，长期运行日志量偏大。
4. **趋势查询耗时与旧实现持平**：收益仅在内存分配（-99.1%）与长期稳定性，不存在"提速"。
5. **本机安全软件拦截未签名 exe 写 `%LOCALAPPDATA%`**：同一时刻已签名的 `pwsh` 可写，而 .NET 探针程序同样被拒（`UnauthorizedAccessException`），故 `settings.json` 保存路径在本机无法验证（日志出现 `[ERR] 保存通信设置失败` / `[ERR] 设备连接后自动下发阈值配置失败`，二者同源）。这是环境限制，不是产品缺陷，需在干净机复验。
6. **首启（无 `settings.json`）健壮性不足**：`SettingsService.LoadSettings` 在文件不存在时直接写盘且无 try/catch（`SettingsService.cs:38`），写入失败会中断启动；同时 `ApplyDefaultsFromConfiguration` 把 `Connection.ComPort` 赋成 `Device.DefaultName`（`SettingsService.cs:96`，实际值 `Sensor_01`），首启默认串口名不是有效端口。两项均使「开箱即用」不成立，已在新版本走查中判定为未通过项。
7. **模拟器 E2E 只覆盖到一半**：数据入库（约 1 行/秒）、实时值刷新、复位指令下发、断开重连已验证；**报警触发与确认未覆盖**——JSON 报文不含 `errorCode`，而 `AlarmStateMachine.Evaluate` 仅按设备上报的 `ErrorCode` 判定，模拟器阈值固定 80 且温度被钳制在 10~60，无法制造故障态。
8. **未在无 .NET 环境机器、回滚路径上验证**：解压启动仅在本机空白目录验证；回滚演练与数据库冷备份均未执行。

## 6. 升级说明

- **升级方式**：便携 zip，无需安装。退出旧版本（含 Watchdog 进程）后，用新 zip 解压覆盖原目录即可。
- **数据库自动迁移**：首次启动自动补建 `idx_alarm_timestamp`、`idx_telemetry_device_timestamp` 两个索引，旧数据不动。本机实测：迁移前后遥测 1800 行 / 报警 180 行完全一致，`PRAGMA journal_mode` 保持 `wal`。
- **⚠️ 升级前先备份数据库**：对用户数据目录 `*.db`（连同 `-wal` / `-shm`）做冷备份；若升级后需回滚，回滚前**再次**备份新版本已写入的数据，避免回滚覆盖。
- **保留策略默认生效**：升级后超过保留期（默认 30 天）的旧遥测会被自动删除，报警不删。如需保留更久，请在升级前修改 `appsettings.json` 中的保留配置。
- **启动配置校验**：若 `settings.json` 中存在非法参数（共 9 项校验），启动会弹窗列出非法项并退出（退出码 1），按提示修正后再启动。

## 7. 回滚步骤

1. 退出主程序与 Watchdog（确认两个进程都已退出）。
2. 对当前（新版本）已写入的数据目录 `*.db`（连同 `-wal` / `-shm`）做备份。
3. 恢复上一版 zip（v1.1.0 存档于 GitHub Releases）解压覆盖。
4. 启动并验证连接与数据；必要时恢复步骤 2 之前的备份库。

> 注：本回滚路径**尚未演练**（[release-checklist.md §9](release-checklist.md) 未勾），首次回滚须人工执行并核对数据条数。

## 8. 校验和与产物

| 项 | 值 |
| --- | --- |
| 产物 | `dist/SmartEdgeHMI-1.2.0-win-x64.zip` |
| 大小 | 82,065,173 字节 / 78.26 MB（290 个根文件 / 511 个条目） |
| SHA256 | `99e8a5e937bcb2403294cf01c07c46d5c6ecdbe11ee7b1550e34dc48d61034a1` |
| 校验方式 | `sha256sum -c` 等价校验通过；脚本同时写入 `dist/<zip>.sha256` |
| 构建来源 | `main` @ `ae10336`（CI run `43938597156`，8 步 success） |
| 包内容 | 含 `SmartEdgeHMI.exe`、`SmartEdgeHMI.Watchdog.exe`、`appsettings.json`；0 个 `.cs/.csproj/.sln/.pdb/.js/.map`、无 `secrets.h` |

## 9. 证据索引

| 证据 | 链接 |
| --- | --- |
| 性能数字唯一口径（查询下推 / .NET 8 复测 / 写放大） | [performance-benchmarks.md](performance-benchmarks.md)（§8.3 / §8.5 / §9） |
| 本机桌面走查记录（2026-11-17 23:19–23:31，启动 / 双开 / Watchdog / 迁移 / 保留清理） | [release-checklist.md §12](release-checklist.md) |
| 发布走查闭环项与阻塞项汇总 | [next-steps-v1.2.md §9](next-steps-v1.2.md) |
| 硬件验收状态（六项均未执行） | [hardware-validation.md](hardware-validation.md) |
| 数据保留策略说明 | [data-retention.md](data-retention.md) |
| 串口 CTS 竞态复盘 | [postmortem-serial-dispose.md](postmortem-serial-dispose.md) |
| 发布检查表（勾选状态与签字） | [release-checklist.md](release-checklist.md) |
| 构建提交 | `ae10336`（main HEAD，候选包构建来源） |
| 基准时效依据 | `8dfcb40`（最后一次基准相关提交）之后 `SmartEdgeHMI/Data` 仅 `c4579d0` 新增保留服务，未触及 LTTB 与写入热路径，无需重跑基准 |
| 旧库生成提交（v1.1.0 tag 不可编译，改用同 schema 旧库） | `3cb8a00` |
| 串口修复提交 | `30390aa` |
| CI run | `43938597156`（8 步 success） |
| 打包脚本 | `tools/package.ps1` |
