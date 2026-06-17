# SmartEdge-HMI：工业边缘网关监控系统

SmartEdge-HMI 是一个基于 .NET 8 WPF 构建的工业设备上位机监控系统，具备**双协议通信**、**实时数据可视化**、**边缘持久化存储**和**双向控制**能力。

系统采用 OSI 分层架构设计：物理层（串口字节流收发）、协议层（JSON-Lines / Modbus RTU 解析）、应用层（MVVM 视图模型），各层通过 Channel + WeakReferenceMessenger 异步解耦。支持从虚拟串口模拟器到真实 PLC/边缘网关的平滑切换。

## 技术栈

| 层次 | 技术选型 |
|------|---------|
| UI 框架 | WPF (.NET 8) |
| 架构模式 | MVVM（CommunityToolkit.Mvvm） |
| 物理通信 | System.IO.Ports（串口 RS-232） |
| 协议解析 | JSON-Lines、Modbus RTU（半字节查表 CRC16） |
| 数据通道 | System.Threading.Channels（生产者-消费者） |
| 实时图表 | ScottPlot.WPF（SkiaSharp 渲染，30 FPS） |
| 边缘存储 | SQLite + Dapper（WAL 模式 + 批量写入） |
| 日志 | Serilog（异步、按天轮转、WPF 界面输出） |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |
| 配置 | Microsoft.Extensions.Configuration（JSON） |

## 架构概览

### 分层通信模型（OSI 风格）

```
┌─────────────────────────────────────────────────┐
│  应用层 (ViewModels)                             │
│  MonitorVM / AlarmHistoryVM / ConnectionVM       │
├─────────────────────────────────────────────────┤
│  协议层 (Protocol Services)                      │
│  JsonProtocolService ←→ ModbusProtocolService    │
│  (JSON-Lines 解析)   (CRC16 + 保持寄存器)        │
├─────────────────────────────────────────────────┤
│  物理层 (SerialPortService)                      │
│  字节流收发 · 端口管理 · Channel 数据分发         │
└─────────────────────────────────────────────────┘
```

### 数据流

```
硬件/模拟器 → 串口 → SerialPortService(物理层)
                         ↓ RawDataReceivedMessage
               JsonProtocolService / ModbusProtocolService(协议层)
                         ↓ DeviceTelemetryMessage / SensorReadingMessage
               MonitorViewModel → UI 实时更新
               AlarmStateMachine(边缘触发) → SQLite 批量写入(Channel + WAL)
```

## 核心功能

### 双协议通信
- **JSON-Lines 协议**：换行符分隔的 JSON 报文，支持 CRLF/LF，被动接收 + 主动下发
- **Modbus RTU 协议**：03 读保持寄存器、06 写单个寄存器，半字节查表 CRC16 校验、零分配滑动窗口缓冲区解决粘包断包

### 实时监控
- 连接状态指示灯（红/绿）、COM 口 / 波特率 / 协议选择
- ScottPlot 实时曲线图（30 FPS），支持多端口独立曲线
- 温度报警阈值滑块、设备紧急复位按钮

### 报警与日志
- 边缘触发报警状态机：上升沿记录、恢复迟滞计数（防止阈值边界震荡）
- 报警数据批量入库：Channel 生产者-消费者，队列满 50 条或 1 秒窗口触发写入
- 系统日志实时展示，ERROR 级别红色高亮

### 边缘存储
- SQLite WAL 模式 + 临时内存存储
- Dapper ORM + 异步批量提交事务
- 表结构自动迁移（Schema Migration）

### 虚拟设备模拟器
Node.js 编写的双模模拟器，支持：
- JSON-Lines 主动上报（1s/次）
- Modbus RTU 被动轮询（一问一答）
- 温湿度随机游走模拟，阈值超限自动故障

## 项目结构

```
SmartEdgeHMI/
├── App.xaml / App.xaml.cs          # 应用入口、DI 容器、全局异常处理
├── appsettings.json                 # 默认配置（串口、阈值等）
├── Constants/                       # 系统常量 & CRC16 查表
├── Extensions/                      # DI 容器扩展
├── Infrastructure/                  # SlidingBuffer（零分配缓冲区）
├── Models/
│   ├── Dtos/                        # 通信契约（TelemetryPayload、CommandPayload）
│   ├── Messages/                    # 内部消息（强类型 Messenger）
│   └── Entities/                    # 数据实体（AlarmRecordEntity）
├── Services/
│   ├── SerialPortService.cs         # 物理层：串口字节流收发
│   ├── JsonProtocolService.cs       # 协议层：JSON-Lines 解析
│   ├── ModbusProtocolService.cs     # 协议层：Modbus RTU 解析
│   ├── SqliteRepository.cs          # 数据层：SQLite + Channel 批处理
│   ├── AlarmStateMachine.cs         # 报警边缘触发状态机
│   ├── SettingsService.cs           # 配置持久化（原子保存）
│   └── DeviceCommunicationCoordinator.cs  # 统一指令下发调度
├── ViewModels/                      # MVVM ViewModel 层
│   ├── ConnectionViewModel.cs       # 串口连接管理
│   ├── MonitorViewModel.cs          # 监控数据与阈值控制
│   ├── AlarmHistoryViewModel.cs     # 报警历史查询
│   └── LogConsoleViewModel.cs       # 日志控制台
├── Views/                           # WPF 界面
│   └── MainWindow.xaml              # 主窗口布局
└── Simulator/                       # Node.js 虚拟设备模拟器
    └── virtual-device.js            # 双模模拟（JSON + Modbus）
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

详见 [ROADMAP.md](ROADMAP.md) — 包含阶段七的稳定性工程化和硬件接入规划。
