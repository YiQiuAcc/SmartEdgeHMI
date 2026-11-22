# SmartEdgeHMI 现场操作手册

> 适用版本：v1.2.0（发布包 `SmartEdgeHMI-1.2.0-win-x64.zip`，tag `v1.2.0`，构建自 `main` @ `26f6cef`，见 release-notes-v1.2.0.md §8）。
> 本手册只写有依据的事实，关键结论的出处见文末「依据表」；看不到依据的写「未验证」并在 §9 登记。无截图可配。

## 1. 这是什么 / 适用版本

Windows 工业边缘上位机：串口采集 + 实时曲线 + ISA-18.2 报警 + SQLite 存储。主程序 `SmartEdgeHMI.exe`，可选守护进程 `SmartEdgeHMI.Watchdog.exe`（同目录成对打包）。

## 2. 安装与首次启动

1. 便携 zip，解压即用，无需安装；发布为 self-contained（`--self-contained true`），目标机器不需要预装 .NET。空白目录解压直接启动已在本机验证。
2. 首次启动自动生成用户设置文件 `%LOCALAPPDATA%\SmartEdgeHMI\settings.json`（默认值取自包内 `appsettings.json`；`ComPort` 为空 = 未选择）。
3. 主界面出现即就绪（启动完成时日志记「SmartEdgeHMI 应用程序已成功启动。」）。双开被拦截：第二个实例弹「SmartEdgeHMI 系统已在运行中, 请勿重复启动！」后退出。
4. 可选：运行同目录 `SmartEdgeHMI.Watchdog.exe`（默认监控同目录 `SmartEdgeHMI.exe`）。主进程被强杀后由它重启：心跳超时阈值 10 秒，本机实测 1.8 秒重启。
5. 日志落点按顺序探测，取第一个可写目录：`%LOCALAPPDATA%\SmartEdgeHMI\logs` → 程序目录 `logs\` → `%TEMP%\SmartEdgeHMI\logs`。**每个日志文件首行是「日志文件目录: …」，以它为准确认实际落点。** 文件按天滚动（如 `log-20261121.txt`），单文件上限 10 MB，保留 31 个。
6. 启动配置校验：`settings.json` 中 9 个关键参数非法（如波特率越界）时，启动弹窗逐条列出非法项并提示「请检查 %LOCALAPPDATA%\SmartEdgeHMI\settings.json, 修正或删除该文件后重启。」，退出码 1。

## 3. 连接设备

- 「端口」下拉列表在程序启动时枚举一次系统串口名，之后新插入的串口**不会自动出现**，重启程序刷新。没有自动选串口。
- 「波特率」选项固定：4800 / 9600 / 19200 / 38400 / 57600 / 115200，须与设备一致（模拟器与 ESP8266 固件均为 115200 8N1）。
- 「协议」选 JSON 或 Modbus；选 Modbus 时出现 `Slave ID:` 输入框（默认 1）。注意：JSON-Lines 串口模式只有 Node.js 模拟器提供，真实 ESP8266 硬件串口只走 Modbus RTU。
- 未选端口就点「连接」→ 状态栏显示「请先选择串口」，不会静默。
- 点「连接」后看状态栏文字与左侧圆点：文字依次「未连接」→「已连接 <COM口>」（多口时「已连接 N 个端口」）；圆点红=未连接、绿=已连接。
- 打开失败 → 状态栏「打开串口失败」，日志有「打开串口失败: <口>」和异常详情；链路异常 →「链路故障 [<口>]」。
- 点「连接」后绿灯不亮先别急：串口打开后有 5 秒稳定期，之后才转入「已连接」。
- 断开：就是同一个按钮，连接后文字变「断开」，再点一次即断开，状态回「未连接」。
- 意外断线自动重连：最多 5 次，间隔 5s/10s/20s/40s/80s 递增；全部失败后状态显示「链路故障 [<口>]」。打开串口时 DTR/RTS 置为关闭。

## 4. 实时监控与阈值下发

- 主界面中部是实时曲线；连接栏右侧按钮在「历史模式 / 实时模式」间切换。历史模式可选起始/结束时间与点数（500/1000/2000/5000，默认 1000，默认区间最近 1 小时），点「加载」出曲线。
- 左下监控面板标题「温度报警阈值设置」：「设置值:」是滑块值，「当前值:」是最新温度（保留 1 位小数）。
- 阈值滑块范围 0–100、步进 1；写入 `settings.json` 的值允许 -40~125（启动校验范围）。
- 生效方式：滑动停止 3 秒后防抖保存进 `settings.json` 并下发到设备；每次串口连接成功后会自动再下发一次当前阈值。JSON 模式下发 Configure 命令，Modbus 模式写保持寄存器（值 = 阈值×10）。
- 报警判定在设备侧：设备上报错误码（如 302=阈值超限）才触发上位机报警，上位机不用阈值做本地比较；数据质量为 Bad 的帧不参与报警判定。
- 「⚠️ 设备紧急复位 (Reset)」按钮下发复位指令（JSON Action=Reset；Modbus FC06 写复位寄存器）。复位指令在模拟器上已实测执行；真实硬件行为未验收（§9）。

## 5. 报警处理

主窗口底部「报警视图」页签，状态列四种值（ISA-18.2）：

| 状态 | 现场含义 | 该做什么 |
| --- | --- | --- |
| UNACK | 已触发、未确认 | 现场核实后点该行「确认」；行底色深红加粗 |
| ACK | 已确认、尚未恢复 | 继续观察，等设备连续恢复正常帧 |
| RTN_UNACK | 已恢复、但未确认 | 点「确认」归档；行底色深绿 |
| NORMAL | 已确认且已恢复 | 无需操作 |

- 恢复不是立即的：错误码消失后还要连续 `Alarm.RecoveryDebounceCount` 帧（默认 3）正常帧才判恢复（防阈值边界抖动）；期间再报错则 RTN_UNACK 回退 UNACK。
- 「一键确认全部」只在存在非 NORMAL 状态报警时可点；报警全部恢复/确认后自动变灰（禁用）——这是设计行为，不是故障。
- 单行「确认」按钮只在 UNACK / RTN_UNACK 行可见。
- 报警列表超过 `Logging.MaxLogEntries`（默认 500）条时裁掉最旧的；「系统日志」页签中 ERROR 级红色显示。

## 6. 数据与文件位置

| 文件 | 位置 | 说明 |
| --- | --- | --- |
| 数据库 | 程序目录下 `SmartEdgeHMI.db`（相对路径按程序目录解析） | 表 `AlarmHistory` / `TelemetryHistory`；启动自动补索引、自动迁移旧库 |
| WAL | 同目录 `SmartEdgeHMI.db-wal` / `-shm` | WAL=SQLite 写前日志，`journal_mode=WAL` 随库文件持久化，checkpoint 由 SQLite 自行调度；删除行不会让库文件变小 |
| 用户设置 | `%LOCALAPPDATA%\SmartEdgeHMI\settings.json` | 界面上的串口/波特率/Slave ID/阈值改动自动写回（原子写：先 `.tmp` 再改名） |
| 包内配置 | 程序目录 `appsettings.json` | 随包发布，默认值来源 |
| 日志 | 见 §2 第 5 条 | |

`appsettings.json` 与用户 `settings.json` 的区别：

- `appsettings.json` 随包发布，管「默认值 + 进程级配置」：文件日志级别（`Logging:LogLevel:Default`）、数据库连接串与保留策略（`DatabaseSettings`：`RetentionDays` 默认 30 天 / `AlarmRetentionDays` 默认 0=报警不删 / `RetentionCheckIntervalHours` 默认 24）只在这里改，改后需重启。
- `settings.json` 是用户设置，含 9 个受启动校验的参数：`Connection.BaudRate`、`Modbus.SlaveAddress`、`Modbus.PollingIntervalMs`、`Watchdog.HeartbeatIntervalMs` / `ConnectTimeoutMs`、`UI.ChartRefreshRateMs`、`Hardware.DefaultThreshold`、`Alarm.RecoveryDebounceCount`、`Logging.MaxLogEntries`（另有 `Connection.ComPort` 与 `Watchdog.ReconnectDelayMs`，不在校验之列）。
- 遥测超期自动清理（每 24 小时一轮、分批删除）；目标是库不再增长，不会自动缩小。

## 7. 备份 / 升级 / 回滚

**必须先在界面正常退出程序与 Watchdog**——脚本检测到目标目录里有 SmartEdgeHMI 进程在跑就拒绝执行（`-DryRun` 也不放行，退出码 2）。

```bash
# 第 1 步：冷备份（升级前、回滚前都必须先备份）
pwsh ./tools/backup-data.ps1 -AppDir <发布目录> -OutDir <备份根目录> [-DryRun]

# 第 2 步：升级 = 退出旧版本（含 Watchdog）后，新 zip 解压覆盖原目录

# 第 3 步：需要时恢复（脚本先校验备份，校验不过一个字节都不写）
pwsh ./tools/restore-data.ps1 -BackupDir <某次备份目录> -AppDir <目标发布目录> [-DryRun]
```

- 备份集：`SmartEdgeHMI.db` / `-wal` / `-shm` / `settings.json`（存在才纳入），落到 `<OutDir>\<yyyyMMdd-HHmmss>\`，生成含 SHA256 的 `manifest.json` 并自校验；恢复时现有数据整体移到 `data-before-restore-<时间戳>\`（只移动、不删除）。
- 退出码：0 成功 / 1 未预期异常 / 2 进程在跑 / 3 没找到库文件（数据若在用户目录，改用 `-AppDir $env:LOCALAPPDATA\SmartEdgeHMI`）/ 4 校验失败 / 5 恢复后校验失败。
- 回滚步骤见 release-notes-v1.2.0.md §7（退出程序与 Watchdog → 备份新版本数据 → 恢复上一版 zip → 启动验证）。该路径**尚未演练**，结论以 release-checklist §9 的勾选状态为准。

## 8. 常见问题（现象 → 先看哪里 → 处理）

**8.1 连不上串口 / 端口列表里没有目标口**
先看端口下拉列表与日志文件（搜「打开串口失败」）。端口列表是启动时枚举的：后插的 USB 串口要重启程序才出现。口被占用或不存在时打开失败，状态栏「打开串口失败」。核对波特率（设备 115200）与 Modbus Slave ID（固件从站地址为 1）。上位机打开串口时 DTR/RTS 为关闭，依赖这两个信号的设备未验证（§9）。

**8.2 日志目录不可写（`%LOCALAPPDATA%` 被拦截）**
先看日志文件首行「日志文件目录: …」确认实际落点。`%LOCALAPPDATA%` 不可写时程序自动回退程序目录 `logs\`，再不行回退 `%TEMP%\SmartEdgeHMI\logs`；本机走查实测过回退分支并正常写盘。若日志同时出现「保存通信设置失败」，见 8.3——那是同一拦截的另一面，属环境限制，启动 / 串口连接 / 数据入库不受影响。

**8.3 `settings.json` 保存失败**
先看日志：出现「创建设置目录失败」「首次写入设置文件失败」「保存通信设置失败」「设备连接后自动下发阈值配置失败」即命中；典型异常形如 `UnauthorizedAccessException: Access to the path '…\settings.json.tmp' is denied.`。影响：改动只在本次运行的内存里生效、不持久化，重启后回到上次成功保存的值；首启写盘失败不中断启动（用内存默认值继续）。处理：修正目录写权限后重新改动一次即可。

**8.4 报警不消失 / 「一键确认全部」常亮或变灰**
先看「报警视图」状态列与日志（搜 `[ISA-18.2]`）。报警要走到 NORMAL 才闭环：设备恢复后还要连续 3 帧（默认）正常帧，RTN_UNACK 还需手动点「确认」。「一键确认全部」只在存在非 NORMAL 报警时可用，全部恢复后变灰是正常行为。报警长期停在 UNACK：说明设备仍在上报错误码，先查设备与链路。

**8.5 程序被强杀后残留 `-wal` 文件**
先看程序目录是否同时有 `SmartEdgeHMI.db-wal` / `-shm`。WAL 模式下这两个文件存在是正常现象（`journal_mode=wal` 随库持久化；长期观察中 WAL 稳定在约 4.13 MB 后不再增长）。**备份/恢复时必须连同 `-wal`/`-shm` 一起处理**——backup/restore 脚本已把三者列为标准备份集，不要单独只拷 `.db`。手工删除 `-wal` 的做法未验证（§9），一律通过脚本处理。

## 9. 明确不做 / 未验证

- 没有自动选串口（未实现，需在界面选择实际端口）。
- 没有 Modbus TCP / MQTT / CAN / OTA / 云平台（v1.2 非目标）。
- JSON-Lines 串口模式仅由 Node.js 模拟器提供；真实 ESP8266 固件串口只支持 Modbus RTU（115200 8N1、从站 1、FC03 读 / FC06 写）。
- 真实 ESP8266 硬件验收六项（连续运行 / 拔插 / 异常报文 / 指令下发 / 断电恢复 / Watchdog 重启后串口恢复）全部「未执行」；依赖 DTR/RTS 的设备行为未验证。
- 无开发环境的 Windows 干净机未验证（解压启动仅在本机验证；本机存在「未签名 exe 写 `%LOCALAPPDATA%` 被安全软件拦截」的环境限制，需干净机复验）。
- 回滚与数据库冷备份演练未执行：回滚路径是否可用，**见 release-checklist §9**，本文不替它下结论。
- 24 小时连续观察进行中、结论未出。
- 手工删除 `-wal` 未验证，不做此操作指引。

## 依据表

| # | 关键结论 | 出处 |
| --- | --- | --- |
| 1 | 发布包 / 版本 / 构建来源 / tag | docs/release-notes-v1.2.0.md §8 |
| 2 | 定位（串口采集 + 曲线 + ISA-18.2 + SQLite） | docs/next-steps-v1.2.md §2.1 |
| 3 | 便携 zip / self-contained / 解压即用 | release-notes §6；tools/package.ps1:76；release-checklist §7 |
| 4 | 首启自动生成 `settings.json`，默认值来自 `appsettings.json` | SmartEdgeHMI/Core/Services/SettingsService.cs:40-67,91-122 |
| 5 | `ComPort` 默认空=未选择 | release-notes §3；release-checklist §12 A |
| 6 | 主界面出现即就绪 / 启动完成日志 | SmartEdgeHMI/App.xaml.cs:197-199 |
| 7 | 双开拦截文案 | App.xaml.cs:139-145 |
| 8 | Watchdog 10s 超时重启 / 同目录定位主程序 | SmartEdgeHMI.Watchdog/Program.cs:20,42-65；release-checklist §6 |
| 9 | 日志三级回退 / 首行写落点 / 滚动与保留 | App.xaml.cs:72-77,82,95-112；release-checklist §12 A/B |
| 10 | 启动校验 9 参数 / 弹窗 / 退出码 1 | App.xaml.cs:167-180；Common/AppSettingsValidator.cs:11-36 |
| 11 | 端口列表启动时枚举一次 | ViewModels/ConnectionViewModel.cs:59-60；Protocols/Transports/Serial/SerialPortService.cs:46 |
| 12 | 波特率档位 | Common/AppConstants.cs:6-9 |
| 13 | 设备侧 115200 8N1 / 从站 1 / FC03、FC06 | docs/hardware-validation.md §2,§3,§4 |
| 14 | JSON 串口模式仅模拟器提供 | hardware-validation.md §1 |
| 15 | `Slave ID` 框默认 1 | Views/Controls/ConnectionBarView.xaml:66-100；ConnectionViewModel.cs:37 |
| 16 | 「请先选择串口」/ 状态文案与红绿灯 | ConnectionViewModel.cs:39,80-84,101-110,186-189,220-224；Resources/Strings.zh-CN.xaml:10-14；release-checklist §12 A |
| 17 | 「打开串口失败」/「链路故障 [口]」 | ConnectionViewModel.cs:200-204；SerialPortService.cs:70-75 |
| 18 | 5 秒稳定期后才转「已连接」 | SerialPortService.cs:26,112-116 |
| 19 | 连接/断开同一按钮 | ConnectionBarView.xaml:102-118；ConnectionViewModel.cs:208-229 |
| 20 | 自动重连 5 次、5s 起倍增，失败后报链路故障 | SerialPortService.cs:27-28,173-177,233-239 |
| 21 | DTR/RTS 关闭 | SerialPortService.cs:58-64 |
| 22 | 历史模式 / 点数选项 / 默认区间 | ConnectionBarView.xaml:120-174；ViewModels/TrendViewModel.cs:12-23 |
| 23 | 阈值滑块 0–100 步进 1 / 当前值显示 | Views/Controls/MonitorPanelView.xaml:16-31 |
| 24 | 校验范围 -40~125 | AppSettingsValidator.cs:29-30 |
| 25 | 防抖 3 秒保存并下发 / 连接成功自动下发 | AppConstants.cs:12；ViewModels/MonitorViewModel.cs:66-72,180-228 |
| 26 | JSON Configure / Modbus 阈值×10 下发 | Protocols/DeviceCommunicationCoordinator.cs:51-83 |
| 27 | 报警由错误码触发；Bad 帧不判 | Core/Domain/MachineState/AlarmStateMachine.cs:51-65；Common/Enums.cs:19-29 |
| 28 | 复位指令格式 / 模拟器已实测 / 真实硬件未验收 | DeviceCommunicationCoordinator.cs:86-116；release-checklist §10,§12 |
| 29 | 四状态语义与转换、恢复去抖默认 3 | AlarmStateMachine.cs:49-113,162-177；Enums.cs:47-53；appsettings.json:17-19 |
| 30 | 行配色 / 单行确认按钮可见条件 | Views/Controls/AlarmLogTabsView.xaml:21-35,73-81；Views/Converters/AlarmAckVisibilityConverter.cs:12-15 |
| 31 | 一键确认全部可用条件 / 恢复后变灰 | AlarmLogTabsView.xaml:129-138；MonitorViewModel.cs:127-146；release-checklist §12 C |
| 32 | 报警列表裁剪 / 系统日志 ERROR 红色 | ViewModels/AlarmHistoryViewModel.cs:52-53；AlarmLogTabsView.xaml:101-110 |
| 33 | 数据库文件位置与表名 / 索引与迁移 | Data/SqliteConnectionFactory.cs:36-39,59-131 |
| 34 | WAL 持久化 / checkpoint 自调度 / 库不缩小 | SqliteConnectionFactory.cs:20-25；docs/data-retention.md §4 |
| 35 | 配置文件分工（appsettings vs settings.json） | SettingsService.cs:91-122；appsettings.json:20-37；App.xaml.cs:86-92 |
| 36 | 遥测保留默认 30 天 / 报警不删 / 24h 一轮 | data-retention.md §2,§3 |
| 37 | 备份/恢复命令、守卫、备份集、退出码 | tools/README.md:5-17；tools/backup-data.ps1:80-83,94；tools/restore-data.ps1:48 |
| 38 | 升级方式与升级前备份 / 回滚步骤 | release-notes §6,§7 |
| 39 | 回滚/冷备份演练未执行 | release-checklist §9 |
| 40 | 无自动选串口 | release-checklist §5 |
| 41 | 无 Modbus TCP/MQTT/CAN/OTA/云平台 | docs/next-steps-v1.2.md §7 |
| 42 | 硬件验收六项未执行 / 干净机未验证 | release-notes §5；hardware-validation.md §1 |
| 43 | `%LOCALAPPDATA%` 拦截属环境限制 / 保存失败日志与影响 | release-checklist §12 B；release-notes §5.6；SettingsService.cs:53,64；ConnectionViewModel.cs:165；MonitorViewModel.cs:219 |
| 44 | WAL 约 4.13 MB 自限 | data-retention.md §4；release-checklist §12 D |
