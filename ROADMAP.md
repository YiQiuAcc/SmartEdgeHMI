## 开发路线图 (Milestones)

### 阶段一：基础设施与架构搭建 [已完成]

- [x] 创建 .NET 8 WPF 项目，配置 .gitignore
- [x] 引入核心 NuGet 包：CommunityToolkit.Mvvm、ScottPlot.WPF、Serilog、Dapper 等
- [x] 搭建 MVVM 骨架：Constants、Models、Services、ViewModels、Views 分层目录
- [x] 全局安全机制：
    - Mutex 实现全局单例进程锁，防止重复启动导致硬件端口冲突
    - 三级异常捕获（UI线程/后台线程/Task线程），保障系统稳定性

### 阶段二：底层通信驱动层 [已完成]

- [x] 定义通信契约：TelemetryPayload 与 CommandPayload 数据模型
- [x] 实现 SerialPortService：COM 口扫描、安全打开/关闭，后台 Task 监听串口
- [x] 实现 JSON-Lines 协议解析：WeakReferenceMessenger 异步广播，支持 CRLF/LF
- [x] 实现 Modbus RTU 协议：半字节查表 CRC16 校验、保持寄存器读写
- [x] 滑动窗口缓冲区：基于 ArrayPool 的零分配粘包/断包处理

### 阶段三：UI 构建与数据绑定 [大部分完成]

- [x] 主控面板：连接状态指示灯、COM 口/波特率选择、连接/断开按钮
- [x] 动态曲线图：ScottPlot 实时绘制传感器数据趋势（30 FPS 渲染）
- [x] 实时遥测数据展示
- [x] 控制下发面板：温度报警阈值滑块、设备紧急复位按钮
- [x] 日志/报警视图：DataGrid 展示报警记录与系统日志，ERROR 级别红色高亮
- [x] LTTB 数据降采样算法：百万级历史数据压缩至数千点，支撑大规模趋势回看
- [x] ISA-18.2 工业报警状态机：未确认/已确认/恢复状态管理，一键确认全选

### 阶段四：虚拟设备模拟器联调 [已完成]

- [x] 编写 Node.js 虚拟下位机模拟器，通过虚拟串口通信
- [x] 支持 JSON-Lines 主动上报模式（1s/次）
- [x] 支持 Modbus RTU 被动轮询模式（一问一答）
- [x] 模拟温湿度自然波动（Random Walk），阈值超限自动触发故障状态
- [x] 全链路闭环验证：接收采集 → 波形渲染 → 指令下发 → 下位机响应

### 阶段五：边缘存储落地 [已完成]

- [x] SQLite + Dapper 封装，异步初始化和表结构自动迁移
- [x] WAL 模式 + 临时内存存储优化，读写分离
- [x] Channel 生产者-消费者异步批处理：队列满 50 条或 1 秒窗口触发批量写入
- [x] 报警边缘触发状态机：上升沿记录、恢复迟滞计数防抖

### 阶段六：真实硬件接入 [待规划]

- [ ] Arduino / ESP32 C++ 固件，通过 Serial 发送协议报文
- [ ] 解析上位机重置/动作指令，控制板载 LED 或继电器

### 阶段七：稳定性与工程化 [待规划]

- [ ] Watchdog 守护进程：命名管道心跳检测，异常时自动重启 HMI
- [ ] 多语言国际化：WPF 资源字典实现运行期中英文切换
- [ ] 单元测试：XUnit 覆盖 CRC 校验、协议解包、报警状态机核心逻辑
