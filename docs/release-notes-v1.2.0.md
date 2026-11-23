# SmartEdgeHMI v1.2.0 发布说明

> 版本 1.2.0 ｜ 日期 2026-11-22 ｜ 目标框架 .NET 8 (win-x64, self-contained)
> 状态：**已打 tag `v1.2.0`，GitHub Release 页与资产已建立**（发布包在 `26f6cef` 上清洁构建，tag 提交相对它只含文档）：[Releases/tag/v1.2.0](https://github.com/YiQiuAcc/SmartEdgeHMI/releases/tag/v1.2.0)。资产已从 Release 页回下载复核 SHA256，检查表 §10 的「解压启动」仍待补做。
> 旧候选包（`e1a3240` / `6fc86aa3…768077`；`8952e46` / `76e9641a…7681`；更早 `f8b3997` / `c02c87aa…e230b`；`fb69bbe` / `24fa4004…b168`、`ae10336` / `99e8a5e9…`）与其结论作废。
> 2026-11-22 真机硬件验收（NodeMCU v2 + CH340, COM4）第一轮在 HW-06 判失败，暴露三条 P1（协议选择不持久化 / 重启后不自动连串口 / 手动重连与在途重试竞态）；修复后第二轮 HW-02/04/05/06 全部通过，验收记录见 [hardware-validation-2026-11-22.md](hardware-validation-2026-11-22.md)。

## 1. 摘要

本版本把数据层三类高频查询（报警历史 / 报警上下文 / 趋势）从"整表读进内存"改为 SQL 查询下推与分桶取样，并补 `(DeviceId, Timestamp)` 复合索引，长期运行的大库不再拖垮内存与 GC；新增遥测数据保留策略（默认 30 天，报警不删），库文件不再无限膨胀。发布工程化侧补齐 `tools/package.ps1` 一键打包（publish / zip / SHA256）、CI 覆盖率产物与漏洞扫描；应用侧新增启动配置校验（9 参数，坏配置拒绝启动）与日志批量裁剪；并修复了一处 Watchdog 心跳管道同名串台、导致健康主程序被反复强杀的可用性缺陷（见 §3 与 [postmortem-watchdog-pipe.md](postmortem-watchdog-pipe.md)）。发布包已在本机完成桌面走查（启动冒烟 / 双开拦截 / Watchdog 强杀重启 / 旧库迁移 / 保留清理 / 包内容与校验和）；其后完成无开发环境干净机验收与真机硬件验收（六项全过，见 §5 第 11 条），门禁全绿后打成 tag `v1.2.0`。**GitHub Release 页与资产已发布**，发布后 smoke 待补（检查表 §10）；遗留限制见 §5。

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
| Watchdog 心跳管道同名串台，健康主进程被反复强杀 | 管道名改为按被测程序所在目录派生（`SmartEdgeHMI_Watchdog_Pipe_<目录哈希>`），不同安装实例与开发构建互不串台；Watchdog 在心跳通道未建立时**不再**按「心跳超时」杀进程，改为降级告警。实测：修复前占用管道名时 75 秒内强杀 6 次；修复后同一场景 0 次，且强杀主程序仍能在 2 秒内被重新拉起 | [postmortem-watchdog-pipe.md](postmortem-watchdog-pipe.md) |
| 通信协议选择不持久化（2026-11-22 真机验收 HW-06 连带暴露） | `settings.json` 新增 `Connection.Protocol` 键：界面上切换 JSON/Modbus 后立即落盘，重启/看门狗拉起后按原协议恢复，不再回落 JSON。旧版本 settings.json 缺该键时自动按 JSON 处理，无需迁移；非法值（含数字串）同样回退 JSON，不抛异常 | 单测 `SettingsServiceTests` / `ConnectionSettingsTests` / `ConnectionViewModelAutoConnectTests.ParseProtocol_*` |
| 重启/看门狗拉起后不会自动连接串口（HW-06 直接判失败点） | 启动时若上次使用的端口非空、仍在系统中枚举到且未连接，则后台自动走既有「连接」路径重连（不阻塞 UI 启动）；端口不在（设备未插/换口）时只记 INFO 保持未连接，不弹窗、不报错。首次运行（无保存端口）行为不变：不自动连接 | 单测 `ConnectionViewModelAutoConnectTests.ShouldAutoConnect_*` |
| 手动重连与在途重试竞态，旧重连任务把刚打开的新连接拆掉 | 现场表现：「打开成功」与「放弃重连 + 链路故障」同毫秒出现、遥测永久停住。修复：重试循环遇到上下文取消立即退出（不再毫秒级烧完 5 次重试、不再对被取代/被断开的会话报 Error）；重连收尾只清理属于自己会话的注册项（按引用判定），字典里已是新连接时不动 | 单测 `SerialPortServiceTests.IsOwnedEntry_*`；根因与现场日志见任务 TASK-129 |

## 4. 性能

数字一律出自 [performance-benchmarks.md](performance-benchmarks.md)（§8.3 查询下推、§8.5 .NET 8.0.34 复测、§9 写放大），不另造口径：

| 场景 | 改造前 | 改造后 | 说明 |
| --- | ---: | ---: | --- |
| 报警历史查询 | 8 589 µs / 1,930 KB | **118 µs / 25.89 KB** | 约 73× 快，分配 -98.7% |
| 报警上下文查询 | 411,851.8 µs / 86,713 KB | **1,111.7 µs / 270.22 KB** | 约 370× 快，分配 -99.7% |
| 趋势查询（20 万行） | 80,672 KB | **765.13 KB** | **耗时持平**（CI 内噪声），收益仅在内存与长期稳定性 |
| 写入吞吐（50 条/批） | 19.6 万行/秒 | **14.8 万行/秒** | 复合索引代价，1.21×–1.33× 写放大；相对单设备 1 Hz 遥测仍有四个数量级余量 |

## 5. 已知问题（含未执行项与环境限制）

1. **真机验收的未覆盖边界**（六项结论见第 11 条）：2026-11-22 用 NodeMCU v2 + CH340 / COM4 走完 [hardware-validation.md](hardware-validation.md) 六项矩阵，但以下边界**仍未验证、不可外推**：多节点 / 多设备并联、强电磁干扰与长帧错误注入、单节点真机连续运行超过 **125 分钟**（24 小时观察用的是虚拟串口 + 模拟器）、与第三方 Modbus 主站互操作。
2. **干净机验收已完成（串口两项由本机替代覆盖）**：2026-11-22 在 Windows 10 Pro 22H2 干净机（`干净机`，无 .NET SDK、无 .NET 8 运行时、无任何 COM 口）完成验收——解压启动 **1.364 s**、设置落盘与重启保留、双开拦截、Watchdog 强杀后重启、包内容审计（511 文件、0 个禁止项）全部通过。该机无串口设备，`T4-COM1`（选 COM1 并重启保留）与 `T9`（数据链路）于同日改由**本机虚拟串口对 COM1↔COM2 + 模拟器**覆盖通过（选择 COM1 后立即落盘、重启仍为 COM1；连接后遥测 **0 → 37 行**、界面实时曲线、日志 0 ERR/0 WRN）。虚拟串口**不能**覆盖电气特性，那属于 P0-06 真实硬件验收。详见 [release-checklist.md §12](release-checklist.md)。
3. **`v1.1.0` tag 不可编译**（`WatchdogHeartbeatClient.cs(41,64)` CS0103：`_settingsService` 不存在）：本发布包由 `main` 的 `26f6cef` 构建（相对上一候选 `e1a3240` 仅文档变化）；v1.1.0 发布包来源已追溯（[traceability-v1.1.0.md](traceability-v1.1.0.md)：由带未提交改动的标签工作区构建，实际构建树无法复原）。
4. **从"受限完整性目录"运行时会写盘失败（已定位，非产品缺陷）**：程序若从带 **Low 完整性标签**的目录启动（例如被沙箱标记的工作区目录），进程继承低完整性令牌，按 Windows 强制完整性策略无法写入中完整性的 `%LOCALAPPDATA%`，日志出现 `[WRN] 创建设置目录失败` 与 `[ERR] 保存通信设置失败`。2026-11-22 干净机实测（同机装有**第三方安全软件 并全程运行、未加白**）：同一份包从低完整性目录启动失败、从普通用户可写目录启动**完全正常**（设置写入、重启保留、日志 0 条 ERR/WRN）；进程完整性实测 Low 与 Medium 完整性级别。**结论：既不是杀软拦截，也不是产品缺陷——把程序解压到普通用户可写目录运行即可。**
5. **趋势查询耗时与旧实现持平**：收益仅在内存分配（-99.1%）与长期稳定性，不存在"提速"（沿用 2026-11-17 基准结论）。
6. **升级会按 30 天保留策略清理旧遥测**：升级后首次启动，数据保留任务会删除超过保留期（默认 30 天）的旧遥测。升级 / 回滚演练实测：v1.1.0 库中 60 行 2026-06-29 的遥测在升级到 1.2.0 后首次启动即被判为超期清理（报警按 `AlarmRetentionDays=0` 保留）。这是**策略行为，不是数据丢失**；如需保留旧遥测，请在升级前备份或调整保留配置（见 §6）。
7. **`WatchdogHeartbeatClient.Dispose()` 不幂等**：重复调用会抛 `ObjectDisposedException`；当前仅在退出时调用一次，正常运行不受影响，计划 v1.2.1 修复。
8. **心跳协议仍无鉴权（本版已消除同名串台，遗留风险如实披露）**：原固定全局管道名 `SmartEdgeHMI_Watchdog_Pipe` 在被他进程占用时，会导致 Watchdog 反复强杀健康主程序（实测 75 秒 6 次）；本版已改为按安装目录派生管道名，并让 Watchdog 在通道建立不起来时不再杀进程（见 §3 与 [postmortem-watchdog-pipe.md](postmortem-watchdog-pipe.md)）。**遗留风险**：心跳报文仍无鉴权，知道派生规则、且与被监视程序同目录的进程理论上仍可冒充或吸收心跳；当前判为可接受，因为冒充者必须与安装目录同路径。
9. **双开时会多出一个日志文件（轻微）**：第二个实例在单实例检查之前已初始化日志，因此被拦截退出时会留下 `log-YYYYMMDD_001.txt`（内容正常、无 ERR/WRN）。不影响功能，计划 v1.2.1 把单实例检查提前到日志初始化之前。
10. **包未签名**：两个 EXE 均为 `NotSigned`，首次从"下载来的"zip 解压后双击会触发 Windows「无法验证发布者」系统提示（不是应用错误）。正式分发前建议做代码签名。
11. **2026-11-22 硬件验收边界（已复验，遗留两条已知项）**：第一轮真机验收在 HW-06 判失败并暴露三条 P1，修复后（`e1a3240`）第二轮复验通过——强杀主程序后 **7.8 秒**内自动恢复 Modbus 轮询、零人工操作、协议不再回落。完整过程与证据见 [hardware-validation-2026-11-22.md](hardware-validation-2026-11-22.md)。**遗留两条未修**：F-4 阈值只写不可回读（FC06 addr2 写阈值 / FC03 addr2 读状态，验收只能用行为验证；固件 WiFi 接口 `/api/data` 的 `thrh` 可读但依赖联网）；F-6 上位机无 LED 控制指令（FC06 addr4），验收须用串口工具直接发帧。两项均计划 v1.2.1 处理。

## 6. 升级说明

- **升级方式**：便携 zip，无需安装。退出旧版本（含 Watchdog 进程）后，用新 zip 解压覆盖原目录即可。
- **数据库自动迁移**：首次启动自动补建 `idx_alarm_timestamp`、`idx_telemetry_device_timestamp` 两个索引，旧数据不动。本机实测：迁移前后遥测 1800 行 / 报警 180 行完全一致，`PRAGMA journal_mode` 保持 `wal`。
- **⚠️ 升级前先备份数据库**：对用户数据目录 `*.db`（连同 `-wal` / `-shm`）做冷备份；若升级后需回滚，回滚前**再次**备份新版本已写入的数据，避免回滚覆盖。
- **保留策略默认生效**：升级后超过保留期（默认 30 天）的旧遥测会被自动删除，报警不删。如需保留更久，请在升级前修改 `appsettings.json` 中的保留配置。
- **启动配置校验**：若 `settings.json` 中存在非法参数（共 9 项校验），启动会弹窗列出非法项并退出（退出码 1），按提示修正后再启动。
- **设置持久化边界**：本版首次生成 `settings.json` 时若目录不可写，设置不会持久化（日志有 Warning），不影响启动与采集。
- **`settings.json` 新增 `Connection.Protocol` 键**：旧版本设置文件缺该键时自动按 JSON 协议处理，无需手工迁移；升级后首次在界面切换协议即会写出该键。
- **启动自动连接**：启动时若上次保存的端口仍在系统中，会自动连接该端口一次（不弹窗）；设备未插或换口时保持未连接，照常手动选择端口连接即可。

## 7. 回滚步骤

1. 退出主程序与 Watchdog（确认两个进程都已退出）。
2. 对当前（新版本）已写入的数据目录 `*.db`（连同 `-wal` / `-shm`）做备份。
3. 恢复上一版 zip（v1.1.0 存档于 GitHub Releases）解压覆盖。
4. 启动并验证连接与数据；必要时恢复步骤 2 之前的备份库。

> 注：本回滚路径已于 2026-11-22 在本机演练通过（v1.1.0 真包 ↔ 1.2.0 候选包升级 / 回滚，冷备份与条数核对 `60/1 → 走完 → 60/1`，见 [release-checklist.md §9](release-checklist.md) 及其 §12「2026-11-21/22 24 小时观察与回滚演练」表）。

## 8. 校验和与产物

| 项 | 值 |
| --- | --- |
| 产物 | `dist/SmartEdgeHMI-1.2.0-win-x64.zip` |
| 大小 | 82,066,868 字节 / 78.27 MB |
| SHA256 | `4a2d49200cbb0073d00ae494f616ffc39bd2f79bbdfaa42b473da206a1a440fd` |
| 校验方式 | 脚本同时写入 `dist/<zip>.sha256`；`pwsh tools/verify-release.ps1 -Version 1.2.0` 退出码 0 可复验。旧候选包均已作废：`6fc86aa3…768077`（`e1a3240`）、`76e9641a…7681`（`8952e46`）、`c02c87aa…e230b`（`f8b3997`）、`24fa4004…b168`（`fb69bbe`）、`99e8a5e9…34a1`（`ae10336`） |
| 构建来源 | `main` @ `26f6cef`，清洁构建（先删除全部 `bin/obj/.vs/.pio/node_modules/dist` 构建产物再 `pwsh tools/package.ps1 -Version 1.2.0`，2026-11-22 17:47）；CI run `45008115174` 在该提交上 success；包内 `SmartEdgeHMI.exe` 与 `SmartEdgeHMI.Watchdog.exe` 的 `ProductVersion` 均为 `1.2.0+26f6cef3b6fa50e20a677ebb9218bd326bb569b9` |
| 包内容 | 511 条目 = 503 `.dll` / 5 `.json` / 3 `.exe`；0 个 `.cs/.csproj/.sln/.pdb/.js/.map/.h`、无 `secrets.h`（本轮实测口径） |
| 门禁 | `dotnet build -c Release` 0 错误（2 条既有 NU1701）；`dotnet test -c Release --no-build` **129 通过 / 0 失败 / 0 跳过**；`dotnet format --verify-no-changes` 退出码 0（2026-11-22 17:44，先删净 `bin` / `obj` / `.vs` / `.pio` / `node_modules` / `dist` 再还原） |
| tag | `v1.2.0`（注解标签）指向本文件所在提交；该提交相对包构建提交 `26f6cef` 只含文档，`git diff 26f6cef v1.2.0 -- SmartEdgeHMI/ SmartEdgeHMI.Watchdog/ Firmware/ Simulator/ tools/ .github/` 输出为空即证明源码与二进制零差异 |

## 9. 证据索引

| 证据 | 链接 |
| --- | --- |
| 性能数字唯一口径（查询下推 / .NET 8 复测 / 写放大） | [performance-benchmarks.md](performance-benchmarks.md)（§8.3 / §8.5 / §9） |
| 本机桌面走查记录（第一轮 2026-11-17，启动 / 双开 / Watchdog / 迁移 / 保留清理；第二轮 2026-11-20/21，首启 / 日志 / 报警 E2E / 24h 观察起点；第三轮 2026-11-21/22，24 小时观察与升级 / 回滚演练） | [release-checklist.md §12](release-checklist.md) |
| 24 小时连续观察 | **已完成，总判定 PASS-WITH-NOTES**（2026-11-22 `analyze-soak.ps1` 判定；唯一「需人工判断」项 = 10 条心跳 WRN —— 后经实验确认它们是**同名管道串台缺陷的症状**，本版已修复，见 [postmortem-watchdog-pipe.md](postmortem-watchdog-pipe.md)；`soak.csv` 在本机 `soak-data\`，不入库） |
| 项目现状、发布阻塞项与下一步计划 | [next-steps-v1.2.md §2 / §4](next-steps-v1.2.md) |
| 真实硬件验收（两轮，含第一轮失败原因与修复后复验） | [hardware-validation-2026-11-22.md](hardware-validation-2026-11-22.md) |
| 硬件验收流程模板（六项矩阵与判定标准） | [hardware-validation.md](hardware-validation.md) |
| 数据保留策略说明 | [data-retention.md](data-retention.md) |
| 串口 CTS 竞态复盘 | [postmortem-serial-dispose.md](postmortem-serial-dispose.md) |
| Watchdog 心跳管道串台复盘 | [postmortem-watchdog-pipe.md](postmortem-watchdog-pipe.md) |
| 发布检查表（勾选状态与签字） | [release-checklist.md](release-checklist.md) |
| 构建提交 | `26f6cef`（包构建时的 main HEAD；相对上一候选 `e1a3240` 产品代码零差异——`git diff --stat e1a3240 26f6cef` 只改 4 个文档文件） |
| 基准时效依据 | `8dfcb40`（最后一次基准相关提交）之后 `SmartEdgeHMI/Data` 仅 `c4579d0` 新增保留服务，未触及 LTTB 与写入热路径，无需重跑基准 |
| 旧库生成提交（v1.1.0 tag 不可编译，改用同 schema 旧库） | `3cb8a00` |
| 串口修复提交 | `30390aa` |
| CI run | `45008115174`（`26f6cef`，包构建提交，success）· `45009427633`（`847c065`，success）；tag 提交的运行为该提交在 `main` 上触发的 CI（`gh run list --commit $(git rev-parse v1.2.0)` 可查） |
| 打包脚本 | `tools/package.ps1` |
