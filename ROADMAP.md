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

### 阶段六：真实硬件接入 [已完成]

- [x] Esp8266 C++ 固件，通过 Serial 发送协议报文
- [x] 解析上位机重置/动作指令，控制板载 LED

> 2026-11-22 完成真机两轮验收（NodeMCU v2 + CH340 / COM4）：第一轮 5 项通过、HW-06 失败并暴露 3 条 P1，修复后第二轮六项全过（含 125 分钟浸泡）。记录见 [docs/archive/hardware-validation-2026-11-22.md](docs/archive/hardware-validation-2026-11-22.md)，流程模板见 [docs/hardware-validation.md](docs/hardware-validation.md)。

### 阶段七：稳定性与工程化 [已完成]

- [x] Watchdog 守护进程：命名管道心跳检测，异常时自动重启 HMI
- [x] 工程结构重组：src/tests 分离，Resources 目录统一管理资源文件
- [x] 多语言国际化基础：中/英文资源字典 (Strings.zh-CN.xaml / Strings.en-US.xaml)，全局调色板 (Colors.xaml)
- [x] 单元测试：XUnit 覆盖 CRC 校验、协议解包、报警状态机核心逻辑

### 阶段八：工程化外壳与可信度补全 [已完成]

> 定位：代码本体（分层、命名、死代码）已在 `bc2e5fe` / `c500228` 两轮清理；注释于 2026-11-23 按 [docs/comment-guidelines.md](docs/comment-guidelines.md) 全仓统一。本阶段不再动业务代码结构，只补"外部可验证的信号"与"结果可量化的证据"。按投入产出比排序执行。
> v1.2 里程碑、DoD 与发布记录见 [docs/archive/next-steps-v1.2.md](docs/archive/next-steps-v1.2.md)。

**P0 · 可信度打底（必须先做，后面所有事的可信度都挂在这上面）**

- [x] 接入 GitHub Actions CI：`dotnet restore` → `dotnet build -c Release` → `dotnet test` → `dotnet format --verify-no-changes`（[.github/workflows/ci.yml](.github/workflows/ci.yml)；已提交推送，CI 全绿（run 44772013652 / `5a7c383`））
- [x] 将 README 中硬编码徽章（`build-passing`、`tests-39 passed`）替换为真实 workflow badge（[README.md](README.md) 徽章区 + "工程证据"小节，TASK-004）
- [x] 补齐 `LICENSE` 文件（MIT 全文），与 README 徽章声明一致（[LICENSE](LICENSE)）

**P1 · 仓库卫生**

- [x] 资源归位：4 个演示 GIF 移入 `docs/assets/`，文件名改为英文 kebab-case（`modbus-connection.gif` 等），README 引用路径已同步修正
- [x] 清理 `Simulator/` 根目录下的临时脚本（`test_modbus.js` 已删除）
- [x] 验证并提交工作区遗留改动（`coverlet.collector` 8.0.1 + `SQLitePCLRaw.lib.e_sqlite3` 原生库）：已随后续提交落地；2026-11-23 复验 build 0 错误、129 单测通过、format 退出码 0
- [x] 统一包版本管理：新增 [Directory.Build.props](Directory.Build.props)（公共属性 + CI 标记）与 [Directory.Packages.props](Directory.Packages.props)（集中包版本，含 BenchmarkDotNet 0.16.0-preview.2），消除 csproj 重复版本声明

**P2 · 简历增信（真正拉开差距的两件事）**

- [x] 引入 BenchmarkDotNet 性能基准（[docs/performance-benchmarks.md](docs/performance-benchmarks.md)：LTTB 1 万 / 10 万 / 100 万点耗时与分配、SQLite 逐条 vs 50 / 500 批量事务吞吐，含测试环境与未测量边界）
    - 原始产物（md/csv/html/日志）留存于本地 `BenchmarkDotNet.Artifacts/`（已被 .gitignore 忽略），关键表格已复制进文档；未单独出图归档到 `docs/assets/`
- [x] 撰写故障复盘文档 [docs/postmortems.md](docs/postmortems.md)，基于 `8d89037`（关闭 COM1 后访问已释放 CTS 导致未观察 Task 异常）复盘：故障现象 → 排查路径 → 根因 → 修复方案 → 防复发措施
- [x] 真实硬件验收：**已执行（2026-11-22，两轮）**，六项全过（带备注）；第一轮失败原因与修复见 [真机验收记录（归档）](docs/archive/hardware-validation-2026-11-22.md)，流程模板见 [hardware-validation.md](docs/hardware-validation.md)

**明确不做（避免负收益）**

- ~~全仓二次格式化 / 目录结构重排~~：现有分层已足以支撑架构叙事，再动只制造 review 噪音
- ~~为凑覆盖率硬补 WPF UI 测试~~：性价比低，不如把已有协议层/状态机测试的叙事讲透
- ~~重构承重架构~~：项目已可运行且有演示，不动地基

### 阶段九：数据层与发布工程化 [已完成]

> 2026-11-17 夜间批次。主题：把"能跑"的数据层改成"跑久了也不塌"的数据层，并把发布从手工命令变成可复现脚本。

- [x] **报警历史查询下推**：`WHERE + ORDER BY + LIMIT` 替代全表载入，基准 8.6 ms → 118 µs（[performance-benchmarks.md §8](docs/performance-benchmarks.md)）
- [x] **报警上下文查询重写**：按报警时间窗口逐条查询，替代"整张报警表 + 整张遥测表读进内存"，412 ms / 86.7 MB → 1.1 ms / 0.27 MB
- [x] **趋势查询 SQL 分桶**：每桶取温度 min/max 两个真实采样点（保留尖峰），20 万行分配 80 MB → 0.75 MB；时间持平（如实说明，不宣称提速）
- [x] **遥测复合索引 `(DeviceId, Timestamp)`**：窗口查询 591 ms → 1.1 ms，代价是写入侧 1.21×–1.33× 写放大（[§9](docs/performance-benchmarks.md)）
- [x] **数据保留策略**：遥测默认 30 天、5000 行/批、200 批上限；报警默认不删（审计），可配置（[data-retention.md](docs/data-retention.md)）
- [x] **一键打包**：`tools/package.ps1`（测试 → 双 exe 自包含发布 → zip → SHA256），替代手工命令（[release-checklist.md](docs/release-checklist.md)）
- [x] **CI 增强**：trx + XPlat Code Coverage 产物上传、NuGet 漏洞扫描（informational）
- [x] **启动配置校验**：9 个关键参数越界即 Fatal + 弹窗 + 退出，不带病运行
- [x] **日志控制台批量裁剪**：稳态由"每条日志裁一次"降为"每 ~上限/10 条裁一次"
- [x] 单元测试 48 → 99，全部通过（2026-11-21 实测）；截至 v1.2.0 累计 129 通过（2026-11-22 清洁构建）；build 0 错误；format 退出码 0

### 阶段十：注释规范与可维护性 [已完成]

> 2026-11-23。目标：把注释从个人习惯收敛为可评审的仓库标准，不改变产品行为。

- [x] 新增 [docs/comment-guidelines.md](docs/comment-guidelines.md)：注释判据、类型选择、XML 文档门槛、六类必写契约、格式与禁止事项、自检命令
- [x] 全仓按规范清理注释（C# / Watchdog / Shared / Tests / Benchmarks / Firmware / Simulator / tools 脚本）：删除复述代码、装饰线、空壳文档与口语化表述，补齐并发、取消、缓冲区所有权、协议映射与状态迁移契约
- [x] 门禁：build 0 错误、129 单测通过、format 退出码 0、5 个 tools 脚本语法解析通过

### 后续维护

ROADMAP 阶段一～十已全部完成。v1.2.1 维护与收尾清单见 [docs/v1.2.1-backlog.md](docs/v1.2.1-backlog.md)。
