# SmartEdgeHMI v1.2.0 发布说明

> 版本 1.2.0 ｜ 日期 2026-11-21 ｜ 目标框架 .NET 8 (win-x64, self-contained)
> 状态：**候选包已重建（`fb69bbe`），未发布**（阻塞项见「已知问题」）
> 旧候选包（`ae10336` / `99e8a5e9…`）与其结论作废。

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
| `tools/package.ps1` 一键打包 | 一条命令完成 test / publish / zip / SHA256，产物输出至 `dist/`，三次重跑幂等；发布不再依赖手工多步操作 | [release-checklist.md §7](release-checklist.md) · 提交 `aee9346` |
| CI 覆盖率产物与漏洞扫描 | 每次 CI 运行上传测试覆盖率 artifact，并对依赖做漏洞扫描（informational），供应链问题可提前发现 | [CI workflow](../.github/workflows/ci.yml) · 提交 `aee9346` |
| 启动配置校验（9 参数） | 配置非法（如 BaudRate=0 / PollingIntervalMs=5 / MaxLogEntries=10）时启动即弹窗逐条列出非法参数并指向 `settings.json` 路径，退出码 1，不再带病运行；配置合法时日志输出「设置校验通过」 | [release-checklist.md §12](release-checklist.md) |
| 日志批量裁剪 | 内存日志按 `MaxLogEntries` 上限批量裁剪，长时间运行不再无界累积 | 提交 `d20fb46` |
| 模拟器故障窗口（开发工具） | 定时注入 `status=4` + `err_code`，用于报警链路 E2E | 源码 `Simulator/virtual-device.js` |

## 3. 修复

| 问题 | 修复与用户可见效果 | 证据 |
| --- | --- | --- |
| 串口关闭后访问已释放 CTS 导致的未观察异常 | 关闭串口后不再访问已释放的 CTS 对象（提交 `30390aa`），并新增 fire-and-forget 异常观察器，断开 / 重连不再出现未观察 Task 异常刷屏 | [postmortem-serial-dispose.md](postmortem-serial-dispose.md) |
| 应用工程误引 `coverlet.collector`（测试工具包） | 已移除（提交 `cdc0461`），发布包减少 19 个测试工具文件，包更纯、体积更小 | [release-checklist.md §7](release-checklist.md) |
| 报警上下文查询缺复合索引时的 591 ms 扫描放大 | 窗口查询曾走单列索引并扫约 10 万行；补复合索引后计划变为索引直接 `SEARCH`，591 ms → 1.1 ms。该方法论记录保留：**查询下推的收益依赖索引是否匹配查询形态，不做前后基准无法发现"优化变慢"** | [performance-benchmarks.md §8.4](performance-benchmarks.md) |
| 首启写盘失败会中断启动 | 首启写盘失败不再中断启动：设置目录不可写时用内存默认值继续运行 | 提交 `60d8ebf` |
| 首启默认串口名为非法值 `Sensor_01` | 默认串口名改为"未选择"，首启不再拿非法端口去连接 | 提交 `60d8ebf` |
| 报警恢复后历史记录停留在「已确认」 | 报警恢复后历史记录不再停留在「已确认」，`ClearedAt` 正常落库 | 提交 `3ab7619` / `fb69bbe` |

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
2. **无开发环境机器未验证**：解压启动仅在本机验证；无 .NET / 开发环境的 Windows 干净机验收未执行。
3. **回滚与数据库冷备份未演练**：[release-checklist.md §9](release-checklist.md) 四项均未执行，首次回滚须人工执行并核对数据条数。
4. **24 小时连续观察进行中**：2026-11-21 00:18 起运行，尚未结束、结论未出（起点数据见 [release-checklist.md §12](release-checklist.md) 第二轮记录）。
5. **`v1.1.0` tag 不可编译**（`WatchdogHeartbeatClient.cs(41,64)` CS0103：`_settingsService` 不存在）：本候选包由 `main` 的 `fb69bbe` 构建；v1.1.0 发布包来源与该 tag 不一致，需追溯。
6. **本机未签名 exe 写 `%LOCALAPPDATA%` 被拦截**（环境限制，非产品缺陷）：未签名 exe 写 `%LOCALAPPDATA%` 被本机安全软件拦截（与上一轮结论一致）；同一台机器上从 `bin\Release` 运行可写、解压到 `Agent\projects` 的发布包运行被拒，原因未查明，需在干净机复验。日志出现 `[ERR] 保存通信设置失败` / `[ERR] 设备连接后自动下发阈值配置失败`（`settings.json.tmp` 写入被拒）；启动、串口连接与数据入库不受影响。
7. **趋势查询耗时与旧实现持平**：收益仅在内存分配（-99.1%）与长期稳定性，不存在"提速"（沿用 2026-11-17 基准结论）。

## 6. 升级说明

- **升级方式**：便携 zip，无需安装。退出旧版本（含 Watchdog 进程）后，用新 zip 解压覆盖原目录即可。
- **数据库自动迁移**：首次启动自动补建 `idx_alarm_timestamp`、`idx_telemetry_device_timestamp` 两个索引，旧数据不动。本机实测：迁移前后遥测 1800 行 / 报警 180 行完全一致，`PRAGMA journal_mode` 保持 `wal`。
- **⚠️ 升级前先备份数据库**：对用户数据目录 `*.db`（连同 `-wal` / `-shm`）做冷备份；若升级后需回滚，回滚前**再次**备份新版本已写入的数据，避免回滚覆盖。
- **保留策略默认生效**：升级后超过保留期（默认 30 天）的旧遥测会被自动删除，报警不删。如需保留更久，请在升级前修改 `appsettings.json` 中的保留配置。
- **启动配置校验**：若 `settings.json` 中存在非法参数（共 9 项校验），启动会弹窗列出非法项并退出（退出码 1），按提示修正后再启动。
- **设置持久化边界**：本版首次生成 `settings.json` 时若目录不可写，设置不会持久化（日志有 Warning），不影响启动与采集。

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
| 大小 | 82,065,781 字节 / 78.26 MB（511 个条目 / 290 个根文件） |
| SHA256 | `24fa400406a1b456fef5d6513fe87edc3409ad6ae09368841b7d545b3b29b168` |
| 校验方式 | 脚本同时写入 `dist/<zip>.sha256`；旧候选包哈希 `99e8a5e9…34a1`（`ae10336`）作废 |
| 构建来源 | `main` @ `fb69bbe`，`pwsh tools/package.ps1 -Version 1.2.0`（2026-11-21 00:20） |
| 包内容 | 含 `SmartEdgeHMI.exe`、`SmartEdgeHMI.Watchdog.exe`、`appsettings.json`；0 个 `.cs/.csproj/.sln/.pdb/.js/.map`、无 `secrets.h` |

## 9. 证据索引

| 证据 | 链接 |
| --- | --- |
| 性能数字唯一口径（查询下推 / .NET 8 复测 / 写放大） | [performance-benchmarks.md](performance-benchmarks.md)（§8.3 / §8.5 / §9） |
| 本机桌面走查记录（第一轮 2026-11-17，启动 / 双开 / Watchdog / 迁移 / 保留清理；第二轮 2026-11-20/21，首启 / 日志 / 报警 E2E / 24h 观察起点） | [release-checklist.md §12](release-checklist.md) |
| 24 小时连续观察 | **进行中，未出结论**（本机采样记录，未入库） |
| 项目现状、发布阻塞项与下一步计划 | [next-steps-v1.2.md §2 / §4](next-steps-v1.2.md) |
| 硬件验收状态（六项均未执行） | [hardware-validation.md](hardware-validation.md) |
| 数据保留策略说明 | [data-retention.md](data-retention.md) |
| 串口 CTS 竞态复盘 | [postmortem-serial-dispose.md](postmortem-serial-dispose.md) |
| 发布检查表（勾选状态与签字） | [release-checklist.md](release-checklist.md) |
| 构建提交 | `fb69bbe`（候选包构建时的 main HEAD，本轮最后一个代码提交） |
| 基准时效依据 | `8dfcb40`（最后一次基准相关提交）之后 `SmartEdgeHMI/Data` 仅 `c4579d0` 新增保留服务，未触及 LTTB 与写入热路径，无需重跑基准 |
| 旧库生成提交（v1.1.0 tag 不可编译，改用同 schema 旧库） | `3cb8a00` |
| 串口修复提交 | `30390aa` |
| CI run | `44772013652`（`fb69bbe`，success） |
| 打包脚本 | `tools/package.ps1` |
