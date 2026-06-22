# SmartEdge-HMI：工业边缘网关监控系统

SmartEdge-HMI 是一个基于 .NET 8 WPF 构建的工业设备上位机监控系统，具备**双协议实时通信**、**边缘持久化存储**、**设备状态管理**和**双向控制**能力。

系统采用分层架构设计：物理层（串口字节流收发）→ 协议层（JSON-Lines / Modbus RTU 无锁解析）→ 数据层（SQLite 双缓冲批量写入）→ 状态层（边缘触发报警 + 设备状态容器）→ 展现层（MVVM 视图模型）。各层通过 `Channel<T>` + `WeakReferenceMessenger` 异步解耦，提供从虚拟串口模拟器到真实 PLC/边缘网关的平滑切换。

## 技术栈

| 层次     | 技术选型                                                      |
| -------- | ------------------------------------------------------------- |
| UI 框架  | WPF (.NET 8)                                                  |
| 架构模式 | MVVM（CommunityToolkit.Mvvm 源码生成器）                      |
| 物理通信 | System.IO.Ports（串口）                                       |
| 协议解析 | JSON-Lines、Modbus RTU（半字节查表 CRC16）                    |
| 数据通道 | System.Threading.Channels（批量缓冲 + 超时刷盘）              |
| 实时图表 | ScottPlot.WPF（SkiaSharp 渲染，30 FPS）                       |
| 边缘存储 | SQLite + Dapper（WAL 模式 + Dapper 类型处理器）               |
| 日志     | Serilog（异步文件 + WPF 界面 Sink）                           |
| 依赖注入 | Microsoft.Extensions.DependencyInjection（KeyedService 模式） |
| 配置     | Microsoft.Extensions.Configuration（JSON）                    |

## 架构概览

### 分层架构

```
┌──────────────────────────────────────────────────────────┐
│  ViewModels (MVVM)                                       │
│  MonitorVM / AlarmHistoryVM / ConnectionVM / TrendVM     │
│   ┌─────────────────────────────────────────────────┐    │
│   │  State Layer (领域层)                            │    │
│   │  AlarmStateMachine · DeviceStateContainer       │    │
│   │  SettingsService · DeviceStateSnapshot          │    │
│   └──────────┬──────────────────────────────────────┘    │
│              │ 事件驱动 (Messenger)                       │
│   ┌──────────▼──────────────────────────────────────┐    │
│   │  Communication Layer (通信层)                    │    │
│   │  DeviceCommunicationCoordinator                 │    │
│   │  ┌──────────────┐  ┌──────────────────────┐     │    │
│   │  │ JSON-Lines   │  │ Modbus RTU           │     │    │
│   │  │ Protocol     │  │ Pipe + CRC16         │     │    │
│   │  └──────┬───────┘  └────────┬─────────────┘     │    │
│   └─────────┼───────────────────┼───────────────────┘    │
│             │                   │                         │
│   ┌─────────▼───────────────────▼───────────────────┐    │
│   │  Physical Layer (串口物理层)                     │    │
│   │  SerialPortService · 双线程 Channel 模型         │    │
│   └─────────────────────────────────────────────────┘    │
├──────────────────────────────────────────────────────────┤
│  Data Layer (数据持久化)                                  │
│  SqliteRepository (IAlarmRepository + ITelemetryRepository)│
│  ┌──────────────────────────────────────────────────┐    │
│  │  遥测双缓冲 Channel: 入队 → 批量刷盘              │    │
│  │  报警直写: 实时落盘                               │    │
│  │  降采样: LTTB (Largest Triangle Three Buckets)    │    │
│  └──────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────┘
```

### 数据流

```
硬件/模拟器 → 串口
  ↓ BaseStream.ReadAsync
SerialPortService (物理层)
  ↓ BoundedChannel → ForwardDataLoop
  ↓ RawDataReceivedMessage (Messenger)
DeviceCommunicationCoordinator (路由分发)
  ├─→ JsonProtocolService   (JSON-Lines 报文 → DeviceTelemetryMessage)
  └─→ ModbusProtocolService (CRC16 校验 → SensorReadingMessage)
        ↓
MonitorViewModel
  ├─→ DeviceStateContainer (最新温度/状态/报警快照)
  ├─→ ITelemetryRepository (SQLite 批量异步写入)
  └─→ AlarmStateMachine (边缘触发判定 → AlarmRecordedMessage)
        ↓
AlarmHistoryViewModel ← AlarmRecordedMessage → SQLite 直写
        ↓
TrendViewModel ← ITelemetryRepository.GetTelemetryHistoryAsync() → LTTB 降采样
```

## 核心功能

### 双协议通信

- **JSON-Lines 协议**：换行符分隔的 JSON 报文，基于 `System.IO.Pipelines` 的无锁流式解析，支持交错帧
- **Modbus RTU 协议**：03 读保持寄存器、06 写单个寄存器，半字节查表 CRC16 校验，基于 Pipe 的滑动窗口缓冲区解决粘包断包；支持定时轮询（1s 间隔）

### 实时监控

- 连接状态指示灯（红/绿）、COM 口 / 波特率 / 协议选择
- ScottPlot 实时曲线图（30 FPS DataLogger），支持多端口独立着色曲线
- 温度报警阈值滑块（防抖保存）、设备紧急复位按钮

### 设备状态管理

- `DeviceStateContainer`：统一管理设备的最新遥测值、连接状态、活跃报警快照
- 属性变更通知驱动 UI 刷新，ViewModel 无需直接订阅每个协议消息的字段

### 报警与日志

- **边缘触发报警状态机**：上升沿记录、恢复迟滞计数（`AlarmRecoveryDebounceCount` 帧连续正常才判定恢复），防止阈值边界震荡
- 报警数据直写 SQLite，支持按设备/时间段/报警码过滤查询
- 报警上下文查询：`GetAlarmsWithTelemetryContextAsync` 可拉取报警前后时间窗口的遥测数据（`GroupJoin` 关联）
- 系统日志实时展示，ERROR 级别红色高亮，支持清空

### 边缘存储

- SQLite WAL 模式 + 临时内存存储（`PRAGMA temp_store = MEMORY`）
- **遥测双缓冲批量写入**：`Channel<SensorReadingEntity>` 收集遥测数据，达 50 条或 10 秒超时触发异步事务提交
- **报警直写**：每条报警实时 `INSERT`，确保不丢失
- **Dapper 类型处理器**：`Temperature`/`Humidity`/`DataQuality` 自定义类型处理器自动映射 SQLite 列
- 表结构自动迁移（`MigrateSchemaAsync` 增量添加列）
- LTTB 降采样算法确保海量历史数据的可视性能

### 值对象（Value Objects）

- `Temperature`：摄氏度温标，支持 `FromCelsius(double)` / `FromRawModbus(short)` / 隐式算数运算符重载
- `Humidity`：百分比湿度，支持 `FromPercent(double)` / `FromRawModbus(short)`
- 与 SQLite 之间通过 Dapper `TypeHandler` 自动转换序列化格式

### 虚拟设备模拟器

Node.js 编写的双模模拟器（`Simulator/virtual-device.js`），支持：

- JSON-Lines 主动上报（1s/次，温湿度随机游走 + 阈值超限自动故障注入）
- Modbus RTU 被动轮询（一问一答，CRC16 校验应答）
- 双模式通过命令参数切换：`pnpm run sim:json` / `pnpm run sim:modbus`

## 项目结构

```
SmartEdgeHMI/
├── App.xaml / App.xaml.cs              # 应用入口、DI 容器构建、全局异常处理
├── appsettings.json                     # 默认配置（串口、数据库、阈值）
├── Common/                              # 公共定义
│   ├── AppConstants.cs                  # 系统常量（波特率表、报警参数等）
│   ├── AppSettings.cs                   # 设置模型（Modbus/UI/Hardware 三级嵌套）
│   └── Enums.cs                         # DeviceStatus / DataQuality / ErrorCode / AlarmState 等
│
├── Communication/                       # 通信层
│   ├── DeviceCommunicationCoordinator   # 统一指令下发 + 数据路由 (KeyedService)
│   ├── Ports/                           # 串口物理层
│   │   ├── ISerialPortService.cs        # 串口服务接口
│   │   └── SerialPortService.cs         # 双线程 Channel 收发实现
│   └── Protocols/                       # 协议解析层
│       ├── IProtocolParser.cs           # 协议解析器接口 (KeyedService 模式)
│       ├── JsonProtocolService.cs       # JSON-Lines Pipe 流式解析
│       ├── ModbusProtocolService.cs     # Modbus RTU CRC16 + Pipe 滑动窗口
│       └── Utils/CRC16Table.cs          # 半字节查表 CRC16
│
├── Data/                                # 数据持久化层
│   ├── AlarmHistoryFilter.cs            # 报警查询过滤器（DTO）
│   ├── Entities/                        # 数据实体
│   │   ├── AlarmRecord.cs               # 报警记录实体（Id / DeviceId / Timestamp / AlarmCode / ...）
│   │   └── SensorReadingRecord.cs       # 遥测记录实体（Id / DeviceId / Temperature / Humidity / ...）
│   └── Repositories/                    # 仓储实现
│       ├── IAlarmRepository.cs          # 报警仓储接口
│       ├── ITelemetryRepository.cs      # 遥测仓储接口
│       └── SqliteRepository.cs          # SQLite 实现（双接口 + 双缓冲 Channel）
│
├── State/                               # 领域状态层
│   ├── IAlarmStateMachine.cs            # 报警状态机接口
│   ├── AlarmStateMachine.cs             # 边缘触发实现（恢复迟滞）
│   ├── IDeviceStateContainer.cs         # 设备状态容器接口
│   ├── DeviceStateContainer.cs          # 最新温度/连接/报警快照管理
│   ├── DeviceStateSnapshot.cs           # 状态快照模型（UI 绑定用）
│   ├── ISettingsService.cs              # 配置持久化接口
│   └── SettingsService.cs               # JSON 文件原子保存实现
│
├── Models/                              # 数据传输层
│   ├── Dtos/                            # 通信契约 & DTO
│   │   ├── TelemetryPayload.cs          # 硬件→上位机 JSON 报文模型
│   │   ├── CommandPayload.cs            # 上位机→硬件 JSON 控制报文
│   │   └── AlarmWithTelemetry.cs        # 报警+上下文遥测 DTO
│   ├── Messages/                        # 内部强类型消息（Messenger）
│   │   ├── RawDataReceived.cs           # 原始字节已到达
│   │   ├── DeviceTelemetry.cs           # JSON 遥测已解析
│   │   ├── SensorReading.cs             # Modbus 遥测已解析
│   │   ├── DeviceStateChanged.cs        # 设备连接状态变更
│   │   ├── AlarmRecorded.cs             # 报警已触发
│   │   ├── TrendDataLoaded.cs           # 历史趋势数据已加载
│   │   └── LogUpdate.cs                 # 日志已生成
│   └── ValueObjects/                    # 强类型值对象
│       ├── Temperature.cs               # 摄氏度温标（隐式运算 + Dapper TypeHandler）
│       └── Humidity.cs                  # 百分比湿度（隐式运算 + Dapper TypeHandler）
│
├── ViewModels/                          # MVVM 视图模型
│   ├── ViewModelBase.cs                 # 基类（UI 线程调度、集合同步）
│   ├── ConnectionViewModel.cs           # 串口连接管理
│   ├── MonitorViewModel.cs              # 实时监控 + 阈值控制 + 报警判定
│   ├── AlarmHistoryViewModel.cs         # 报警历史查询 + 实时追加
│   ├── TrendViewModel.cs                # 历史趋势查询
│   ├── LogConsoleViewModel.cs           # Serilog Sink 日志控制台
│   └── MainViewModel.cs                 # 根 VM（组合子 VM）
│
├── Views/                               # WPF 界面
│   ├── AlarmAckVisibilityConverter.cs   # 报警确认按钮可见性转换器
│   └── Windows/
│       └── MainWindow.xaml / .cs        # 主窗口（布局 + ScottPlot 渲染）
│
├── Infrastructure/                      # 基础设施
│   ├── Logging/                         # Serilog Sink
│   │   ├── SystemLogModel.cs            # 日志数据模型
│   │   └── WpfSerilogSink.cs            # 日志→Messenger→控制台
│   ├── Math/                            # 数学算法
│   │   └── LttbDownsampler.cs           # LTTB 降采样（首尾保留 + 三角形面积）
│   └── UI/                              # UI 工具
│       └── BulkObservableCollection.cs   # 批量添加 ObservableCollection 扩展
│
├── Extensions/
│   └── ServiceCollectionExtensions.cs   # DI 容器注册（Singleton + KeyedService）
│
└── Simulator/                           # Node.js 虚拟设备模拟器
    └── virtual-device.js                # JSON + Modbus 双模模拟
```

## 快速开始

### 前置条件

- .NET 8 SDK
- Node.js 18+（运行模拟器）
- 虚拟串口软件（如 VSPD、com0com），用于模拟 COM 口对联

### 启动

```bash
# 1. 配置虚拟串口对联（如 COM1 ↔ COM2）

# 2. 启动模拟器（终端 1）
cd Simulator
pnpm install
pnpm run sim:json     # JSON 模式，或
pnpm run sim:modbus   # Modbus 模式

# 3. 启动 WPF 上位机（终端 2）
dotnet run --project SmartEdgeHMI.csproj

# 4. 在 UI 中选择对应 COM 口、波特率 115200，点击连接
```

## 路线图

详见 [ROADMAP.md](ROADMAP.md) — 包含多协议扩展、持久化优化、CI/CD 流水线等规划。
