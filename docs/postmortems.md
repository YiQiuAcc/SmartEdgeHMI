# 故障复盘合集

> 本文件合并两次缺陷复盘：串口 CTS 释放竞态与 Watchdog 心跳管道同名串台。原始单篇已合并，历史版本可在 git 历史中检索。

## 1. 串口断开连接后的未观察 Task 异常（CTS 释放竞态）

- 分析对象：提交 8d89037（"修复断开连接未观察到的 Task 异常"，2026-08-25）
- 复盘日期：2026-11-16 ｜ 范围：仅该提交 + 当前工作区代码，静态核实，未运行程序

### 摘要

关闭串口（ClosePort、服务 Dispose、重开同端口）时，`CleanupContext` 会 Cancel 并 Dispose 掉
`PortContext` 持有的 `CancellationTokenSource`。修复前，仍在运行的后台读取循环（fire-and-forget）
随后实时访问 `ctx.Cts.Token`（循环条件、重连延迟、稳定等待、写锁等处），CTS 已释放时其 `Token`
getter 抛出 `ObjectDisposedException`；该任务无人等待，异常成为未观察任务异常，无日志、静默丢失。
修复两点：构造 `PortContext` 时缓存 `Token` 并让所有异步路径改用缓存值；新增 `ObserveAndLogAsync`
扩展接入全部 fire-and-forget 任务，记录异常。

### 影响

- 断开连接/退出应用后，后台读取、协议管道、Modbus 轮询、看门狗心跳等任务的异常完全无日志。
- 读取循环被 ODE 静默终止：在关闭场景属预期停止但异常不可见；若同型异常发生在其他时序，则读循环死亡且无告警。
- 运行时在 GC 终结阶段触发 `TaskScheduler.UnobservedTaskException`（.NET 4.5+ 默认不崩溃，但异常信息丢失）。
- 写路径 `WriteBytesAsync`/`WriteStringAsync` 在 CTS 释放后调用同样会从 `.Cts.Token` getter 抛 ODE。

### 触发场景

1. 用户断开 COM1 → `ClosePort`（SerialPortService.cs:338-347）→ `CleanupContext`：
   `Cts.Cancel` → 关闭并释放串口 → `Cts.Dispose`（SerialPortService.cs:356-362）。
2. 同一时刻后台读取任务仍在循环中，下次求值 `ctx.Cts.Token` 时 CTS 已 Dispose → ObjectDisposedException。
3. 同一清理路径的其他入口：服务 `Dispose`（SerialPortService.cs:370-382）、`OpenPortInternal`
   重开已存在端口时替换旧上下文（SerialPortService.cs:82-85）。
4. 修复前的实时求值点（见提交 8d89037 diff 删除行）：读取循环条件、`ReadFromPortSafe` 的 token 参数、
   重连延迟 `Task.Delay`、稳定等待 `Task.Delay`、`RunReadSessionAsync` 判断、两处 `WriteLock.WaitAsync`。

### 根因

1. 生命周期错配：`PortContext.Dispose` 释放 CTS（SerialPortService.cs:39-44）早于后台任务退出，
   而任务在释放后仍实时访问 `ctx.Cts.Token`；CTS Dispose 后 `Token` getter 抛
   ObjectDisposedException（.NET 已文档化行为）。
2. fire-and-forget 无观察：任务以 `_ = Task.Run(...)` 启动（提交前代码），结果被丢弃，
   异常无人观察、无日志。
3. 两因叠加：取消本应让循环安静退出（各处均已捕获 OperationCanceledException），但
   "先 Dispose、后访问"的竞态把可预期的取消变成了不可预期的 ODE。

### 修复方案

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

### 验证证据

- 提交新增单元测试 SmartEdgeHMI.Tests/Utils/TaskExtensionsTests.cs：
  `ObserveAndLogAsync_FaultedTask_CompletesWithoutRethrowing`——faulted Task 经扩展后不重抛。
- 覆盖缺口：CTS 释放竞态（关闭串口后的循环退出路径）无直接测试；本次复盘为静态代码核实，
  未做运行时复现。复盘后新增的 `OpenPort_PortDoesNotExist_...` 测试覆盖的是"打开失败不启动
  后台循环"，仍不覆盖"关闭与读循环并发"的竞态。

### 防复发

- 约定一：持有 CancellationTokenSource 的上下文在构造时缓存 Token，异步路径一律使用缓存 Token，
  禁止在可能的 Dispose 之后访问 `.Cts.Token`（本次 ODE 的直接来源）。
- 约定二：任何 fire-and-forget 任务必须挂 `ObserveAndLogAsync`；code review 检查 `_ = Task.Run`
  模式。扩展本身已有单测，可继续补"ClosePort 与读循环并发"的竞态测试。

### 残余风险

1. `PortContext.Cts` 仍是公开可写属性（SerialPortService.cs:35）：若未来重设 CTS，缓存的 Token
   不会随之更新，将出现"取消失灵"。应改为 get-only 或重设时同步刷新 Token。
2. `ObserveAndLogAsync` 仅记日志，无告警/上报；异常被"可观测地吞掉"，依赖人工巡检日志。
3. 重连等待期间 ClosePort：取消使 `HandleReconnectDelayAndOpenAsync` 返回 false 走 `attempt++`
   路径，会连续打满重连日志，并以 `FinalizePortState` 的"重连已达最大次数"警告 +
   `StateChanged(Error)` 收尾（SerialPortService.cs:102-123、231-243）——主动关闭后出现误导性
   Error 事件。可在捕获取消时直接退出循环。
4. `ReadLoopAsync` 内部已捕获全部异常（SerialPortService.cs:160-164），观察器实际兜底的是外层
   重连循环与事件订阅者（`StateChanged` 委托异常等）；新增订阅者抛异常不会被上游感知。

## 2. Watchdog 心跳管道同名串台导致反复重启

> 日期：2026-11-22
> 严重度：**高**（发生即全停摆且无法自愈）／触发概率：低（需要同机其他进程先占用该管道名）
> 状态：**已修复**（v1.2.0 阻塞项，TASK-126）
> 影响版本：`v1.0.0` 起的所有版本（管道名为编译期常量，自首版即存在）

### 1. 摘要

Watchdog 与主程序之间的心跳管道使用**固定的全局名字** `SmartEdgeHMI_Watchdog_Pipe`。若该名字在主程序启动前已被其他进程占用，Watchdog 无法建立心跳通道，但它仍按「10 秒无心跳」判定主程序无响应，于是把自己刚启动的**健康进程**杀掉并重启，重置计时器后再次判定超时——形成周期约 11 秒的无限重启。

整个过程主程序侧**没有任何告警**：心跳客户端只认管道名，不校验对端身份，连上冒充者后 `hb` 帧照发不误。

### 2. 时间线

| 时间 | 事件 |
| --- | --- |
| 2026-11-21 16:51–16:58 | 24 小时观察期内首次出现 10 条 `[WRN] 心跳发送异常, 3000ms 后重试`。当时定性为「本机测试活动占用全局管道名造成的日志污染，非产品缺陷」——**这个定性是错的**，见 §6 |
| 2026-11-22 00:22 | 24 小时观察结束，判定 PASS-WITH-NOTES，唯一「需人工判断」项即上述 10 条 WRN |
| 2026-11-22 11:06–11:07 | 按「验证管道被占用的真实后果」设计实验，75 秒内复现 6 次强杀 |
| 2026-11-22 11:10 | 结论升级为 v1.2.0 阻塞项，派单修复 |

### 3. 复现（可重复）

1. 任选一个进程，以 `maxInstances=1` 绑定 `SmartEdgeHMI_Watchdog_Pipe` 并阻塞在 `WaitForConnection()`；
2. 启动 `SmartEdgeHMI.Watchdog.exe`（它会自动拉起同目录的 `SmartEdgeHMI.exe`）；
3. 观察 75 秒。

实测结果（候选包 1.2.0）：

- 主程序出现 **7 个不同 PID**，6 次重启，周期约 11 秒；
- Watchdog 日志：每 2 秒一条 `[ERR] 管道服务器循环异常` + `System.IO.IOException: 所有的管道范例都在使用中。`，并 6 条 `[WRN] HMI 心跳超时 (10.0s 无心跳), 正在强制重启...`；
- 主程序日志：7 条 `Watchdog 心跳发送已启动`、6 条 `应用程序正在退出。退出码: 0`、**0 条心跳告警**；
- 冒充者累计收到 36 字节心跳（每条 `hb` 2 字节）。

复现脚本与双方日志存档于交付方本机 `pipe-squat-20261122\` 存档（不入库）。

### 4. 根因

`SmartEdgeHMI.Watchdog/Program.cs`：

1. 管道服务端绑定失败后只 `catch` → 记 ERR → 2 秒后重试，**永不退出，也永不建立通道**；
2. `_lastHeartbeat` 仅在 `StartHmiProcess()` 与收到管道字节时刷新，本次场景下永远停在启动时刻；
3. 监控循环见「10 秒无心跳」即 `KillHmiProcess()` → `StartHmiProcess()`，启动时又重置 `_lastHeartbeat`，于是循环成立；
4. 原本用来兜底的 `_lastHeartbeat != DateTime.MinValue` 判断被 `StartHmiProcess()` 立刻覆盖成当前时间，**形同虚设**。

`SmartEdgeHMI/Core/Services/WatchdogHeartbeatClient.cs`：客户端只按名字连接，不校验对端身份，因此把「连上了别人」当成「心跳正常」。

### 5. 修复

1. **管道名按安装实例派生**：新增 `Shared/WatchdogPipeName.cs`，管道名 = `SmartEdgeHMI_Watchdog_Pipe_` + `SHA256(被测程序所在目录的小写全路径)` 前 16 位十六进制。Watchdog 用被监视程序目录计算，客户端用自身基目录计算，部署下两者一致；不同安装目录、开发构建与发布包之间不再串台。
2. **失效安全**：监控循环仅在管道服务端**当前处于已绑定状态**时才允许按心跳超时杀进程。通道根本不可用时无法区分「主程序无响应」与「心跳通道坏了」，此时杀进程只会制造雪崩，改为降级并告警。「进程已退出则重启」的路径不受影响。
3. **日志降噪**：绑定失败的带堆栈 ERR 由每 2 秒一条改为每 60 秒一条，其余降为 Debug。

### 6. 之前为什么判错

24 小时观察期那 10 条 WRN 出现时，我方把它归因为「测试污染」，理由是：时间戳与测试运行吻合、`WatchdogHeartbeatClientTests` 确实会在该管道名上起假服务端。这个归因**只解释了日志从哪来，没有追问「如果生产环境也发生同样的事会怎样」**——而当时观察中的主程序并没有 Watchdog 在跑，所以缺陷没有暴露，反而显得无害。

教训：当测试活动能触发某条告警时，不能停在「这是测试造成的」，要再问一句「同样的条件在生产路径上会导致什么」。把测试当作环境噪音屏蔽掉，等于放弃了最便宜的一次缺陷发现机会。

### 7. 防复发

- 管道名派生逻辑抽成 `Shared/WatchdogPipeName.cs` 单点实现，两个工程链接同一个源文件，避免两端算法漂移（漂移会导致心跳静默失效）。
- 锁死取值写入单元测试（`C:\Program Files\SmartEdgeHMI` → `SmartEdgeHMI_Watchdog_Pipe_30686708c530bb19`），算法改动必须同步改测试。
- 测试用的假服务端改用测试目录派生的名字，**测试不再占用生产管道名**，从根上消除观察期日志污染。
- 遗留风险（未修，另行评估）：心跳协议无鉴权，同目录下知道派生规则的进程仍可冒充；管道名字对同目录进程是可预测的。当前判为可接受，因为冒充者必须与安装目录同路径。
