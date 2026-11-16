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

### 阶段三：UI 构建与数据绑定 [已完成]

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

### 阶段六：真实硬件接入 [未完成]

- [x] Esp8266 C++ 固件，通过 Serial 发送协议报文
- [x] 解析上位机重置/动作指令，控制板载 LED

> 硬件验收未执行，见 [docs/hardware-validation.md](docs/hardware-validation.md)。

### 阶段七：稳定性与工程化 [已完成]

- [x] Watchdog 守护进程：命名管道心跳检测，异常时自动重启 HMI
- [x] 工程结构重组：src/tests 分离，Resources 目录统一管理资源文件
- [x] 多语言国际化基础：中/英文资源字典 (Strings.zh-CN.xaml / Strings.en-US.xaml)，全局调色板 (Colors.xaml)
- [x] 单元测试：XUnit 覆盖 CRC 校验、协议解包、报警状态机核心逻辑

### 阶段八：工程化外壳与可信度补全 [基本完成 · 剩硬件验收与遗留提交]

> 定位：代码本体（分层、命名、注释、死代码）已在 `bc2e5fe` / `13d0daa` 两轮清理完毕，本阶段不再动业务代码结构，只补"外部可验证的信号"与"结果可量化的证据"。按投入产出比排序执行。
> 后续规划见 [docs/next-steps-v1.2.md](docs/next-steps-v1.2.md)。

**P0 · 可信度打底（必须先做，后面所有事的可信度都挂在这上面）**

- [x] 接入 GitHub Actions CI：`dotnet restore` → `dotnet build -c Release` → `dotnet test` → `dotnet format --verify-no-changes`（[.github/workflows/ci.yml](.github/workflows/ci.yml)；**尚未提交推送，CI 首绿待确认**）
- [x] 将 README 中硬编码徽章（`build-passing`、`tests-39 passed`）替换为真实 workflow badge（[README.md](README.md) 徽章区 + "工程证据"小节，TASK-004）
- [x] 补齐 `LICENSE` 文件（MIT 全文），与 README 徽章声明一致（[LICENSE](LICENSE)）

**P1 · 仓库卫生**

- [x] 资源归位：4 个演示 GIF 移入 `docs/assets/`，文件名改为英文 kebab-case（`modbus-connection.gif` 等），README 引用路径已同步修正
- [x] 清理 `Simulator/` 根目录下的临时脚本（`test_modbus.js` 已删除）
- [ ] 验证并提交工作区遗留改动（`coverlet.collector` 8.0.1 + `SQLitePCLRaw.lib.e_sqlite3` 原生库）：**未提交** —— 当前 CI / LICENSE / GIF 归档 / 基准 / 文档等改动均在 Git 工作区；提交前须跑通 `dotnet test` 确认全绿
- [x] 统一包版本管理：新增 [Directory.Build.props](Directory.Build.props)（公共属性 + CI 标记）与 [Directory.Packages.props](Directory.Packages.props)（集中包版本，含 BenchmarkDotNet 0.16.0-preview.2），消除 csproj 重复版本声明

**P2 · 简历增信（真正拉开差距的两件事）**

- [x] 引入 BenchmarkDotNet 性能基准（[docs/performance-benchmarks.md](docs/performance-benchmarks.md)：LTTB 1 万 / 10 万 / 100 万点耗时与分配、SQLite 逐条 vs 50 / 500 批量事务吞吐，含测试环境与未测量边界）
    - 原始产物（md/csv/html/日志）留存于本地 `BenchmarkDotNet.Artifacts/`（已被 .gitignore 忽略），关键表格已复制进文档；未单独出图归档到 `docs/assets/`
- [x] 撰写故障复盘文档 [docs/postmortem-serial-dispose.md](docs/postmortem-serial-dispose.md)，基于 `30390aa`（关闭 COM1 后访问已释放 CTS 导致未观察 Task 异常）复盘：故障现象 → 排查路径 → 根因 → 修复方案 → 防复发措施
- [ ] 真实硬件验收：**未执行**（本机仅有 ELTIMA 虚拟串口对），可执行验收模板见 [docs/hardware-validation.md](docs/hardware-validation.md)

**明确不做（避免负收益）**

- ~~全仓二次格式化 / 目录结构重排~~：现有分层已足以支撑架构叙事，再动只制造 review 噪音
- ~~为凑覆盖率硬补 WPF UI 测试~~：性价比低，不如把已有协议层/状态机测试的叙事讲透
- ~~重构承重架构~~：项目已可运行且有演示，不动地基
