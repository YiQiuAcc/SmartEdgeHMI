# 复盘：串口断开连接后的未观察 Task 异常（CTS 释放竞态）

- 分析对象：提交 30390aa（"修复断开连接未观察到的 Task 异常"，2026-08-25）
- 复盘日期：2026-11-16 ｜ 范围：仅该提交 + 当前工作区代码，静态核实，未运行程序

## 摘要

关闭串口（ClosePort、服务 Dispose、重开同端口）时，`CleanupContext` 会 Cancel 并 Dispose 掉
`PortContext` 持有的 `CancellationTokenSource`。修复前，仍在运行的后台读取循环（fire-and-forget）
随后实时访问 `ctx.Cts.Token`（循环条件、重连延迟、稳定等待、写锁等处），CTS 已释放时其 `Token`
getter 抛出 `ObjectDisposedException`；该任务无人等待，异常成为未观察任务异常，无日志、静默丢失。
修复两点：构造 `PortContext` 时缓存 `Token` 并让所有异步路径改用缓存值；新增 `ObserveAndLogAsync`
扩展接入全部 fire-and-forget 任务，记录异常。

## 影响

- 断开连接/退出应用后，后台读取、协议管道、Modbus 轮询、看门狗心跳等任务的异常完全无日志。
- 读取循环被 ODE 静默终止：在关闭场景属预期停止但异常不可见；若同型异常发生在其他时序，则读循环死亡且无告警。
- 运行时在 GC 终结阶段触发 `TaskScheduler.UnobservedTaskException`（.NET 4.5+ 默认不崩溃，但异常信息丢失）。
- 写路径 `WriteBytesAsync`/`WriteStringAsync` 在 CTS 释放后调用同样会从 `.Cts.Token` getter 抛 ODE。

## 触发场景

1. 用户断开 COM1 → `ClosePort`（SerialPortService.cs:338-347）→ `CleanupContext`：
   `Cts.Cancel` → 关闭并释放串口 → `Cts.Dispose`（SerialPortService.cs:356-362）。
2. 同一时刻后台读取任务仍在循环中，下次求值 `ctx.Cts.Token` 时 CTS 已 Dispose → ObjectDisposedException。
3. 同一清理路径的其他入口：服务 `Dispose`（SerialPortService.cs:370-382）、`OpenPortInternal`
   重开已存在端口时替换旧上下文（SerialPortService.cs:82-85）。
4. 修复前的实时求值点（见提交 30390aa diff 删除行）：读取循环条件、`ReadFromPortSafe` 的 token 参数、
   重连延迟 `Task.Delay`、稳定等待 `Task.Delay`、`RunReadSessionAsync` 判断、两处 `WriteLock.WaitAsync`。

## 根因

1. 生命周期错配：`PortContext.Dispose` 释放 CTS（SerialPortService.cs:39-44）早于后台任务退出，
   而任务在释放后仍实时访问 `ctx.Cts.Token`；CTS Dispose 后 `Token` getter 抛
   ObjectDisposedException（.NET 已文档化行为）。
2. fire-and-forget 无观察：任务以 `_ = Task.Run(...)` 启动（提交前代码），结果被丢弃，
   异常无人观察、无日志。
3. 两因叠加：取消本应让循环安静退出（各处均已捕获 OperationCanceledException），但
   "先 Dispose、后访问"的竞态把可预期的取消变成了不可预期的 ODE。

## 修复方案

1. Token 缓存：`PortContext` 构造时快照 `CancellationToken Token { get; } = cts.Token`
   （SerialPortService.cs:36）；提交将 7 处异步路径全部改用 `ctx.Token`：循环条件(134)、
   `ReadFromPortSafe`(137)、重连延迟(179)、稳定等待(214)、会话判断(227)、写锁等待(301、323)。
   运行路径不再触碰可能已释放的 CTS；修复后 `Cts` 仅剩清理路径访问（Cancel/Dispose，359、42 行）。
2. 异常观察器：新增 `TaskExtensions.ObserveAndLogAsync`（TaskExtensions.cs:8-22）——
   `await task.ConfigureAwait(false)`，吞 `OperationCanceledException`，其余异常经 Serilog 记录。
3. 接入全部 fire-and-forget 任务：串口读取（SerialPortService.cs:94-95）、看门狗心跳
   （WatchdogHeartbeatClient.cs:19-20）、JSON 管道（JsonProtocolParser.cs:32）、
   Modbus 管道与轮询（ModbusProtocolParser.cs:97、150）、报警处理（MonitorViewModel.cs:111）。
4. `CleanupContext` 保持先 Cancel 后 Dispose 的顺序（SerialPortService.cs:359-361）：已取消 Token
   的 `Task.Delay`/`WaitAsync` 命中"已取消"快速路径，不会走到向已释放 CTS 注册回调的分支。

## 验证证据

- 提交新增单元测试 SmartEdgeHMI.Tests/Utils/TaskExtensionsTests.cs：
  `ObserveAndLogAsync_FaultedTask_CompletesWithoutRethrowing`——faulted Task 经扩展后不重抛。
- 覆盖缺口：CTS 释放竞态（关闭串口后的循环退出路径）无直接测试；本次复盘为静态代码核实，
  未做运行时复现。复盘后新增的 `OpenPort_PortDoesNotExist_...` 测试覆盖的是"打开失败不启动
  后台循环"，仍不覆盖"关闭与读循环并发"的竞态。

## 防复发

- 约定一：持有 CancellationTokenSource 的上下文在构造时缓存 Token，异步路径一律使用缓存 Token，
  禁止在可能的 Dispose 之后访问 `.Cts.Token`（本次 ODE 的直接来源）。
- 约定二：任何 fire-and-forget 任务必须挂 `ObserveAndLogAsync`；code review 检查 `_ = Task.Run`
  模式。扩展本身已有单测，可继续补"ClosePort 与读循环并发"的竞态测试。

## 残余风险

1. `PortContext.Cts` 仍是公开可写属性（SerialPortService.cs:35）：若未来重设 CTS，缓存的 Token
   不会随之更新，将出现"取消失灵"。应改为 get-only 或重设时同步刷新 Token。
2. `ObserveAndLogAsync` 仅记日志，无告警/上报；异常被"可观测地吞掉"，依赖人工巡检日志。
3. 重连等待期间 ClosePort：取消使 `HandleReconnectDelayAndOpenAsync` 返回 false 走 `attempt++`
   路径，会连续打满重连日志，并以 `FinalizePortState` 的"重连已达最大次数"警告 +
   `StateChanged(Error)` 收尾（SerialPortService.cs:102-123、231-243）——主动关闭后出现误导性
   Error 事件。可在捕获取消时直接退出循环。
4. `ReadLoopAsync` 内部已捕获全部异常（SerialPortService.cs:160-164），观察器实际兜底的是外层
   重连循环与事件订阅者（`StateChanged` 委托异常等）；新增订阅者抛异常不会被上游感知。
