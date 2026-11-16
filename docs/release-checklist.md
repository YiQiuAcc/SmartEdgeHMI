# 发布检查表（Release Checklist）

> 适用范围：SmartEdgeHMI Windows 便携 zip 发布（主程序 + Watchdog）。
> 使用方式：发布负责人逐项勾选；存在未通过项时不得打 tag。
> 性能数字如需引用，一律出自 [performance-benchmarks.md](performance-benchmarks.md)，不得新造口径。

## 1. 版本与分支

- [ ] 版本号确定（SemVer，如 v1.2.0），与 Release Notes / tag 名一致
- [ ] 发布分支为 `main`，工作区干净：`git status` 无未提交 / 未跟踪文件
- [ ] tag 指向本次已验证的提交，tag 名与版本一致
- [ ] CI 在该提交上全绿（restore / build / test / format 四步）

## 2. 构建与测试

- [ ] `pwsh tools/package.ps1 -Version {版本}` 一键完成 test / publish / zip / SHA256（隐含 restore + build；勿用 `-SkipTests`；确需跳过时须另行补跑测试并记录），测试全绿，通过数：________（记录实际值）
- [ ] `dotnet format SmartEdgeHMI.sln --verify-no-changes --no-restore` 退出码 0

## 3. 基准数据是否更新

- [ ] 确认自上次基准运行后，LTTB / SQLite 写入路径代码是否变更；若变更，重跑 `SmartEdgeHMI.Benchmarks` 并更新 [performance-benchmarks.md](performance-benchmarks.md)
- [ ] README 中引用的性能数字与基准文档逐条一致，无超出实测边界的表述
- [ ] 复现命令（基准文档 §7）在当前代码上可执行

## 4. 数据库 schema / 迁移与 WAL 兼容

- [ ] 用新版本打开旧版本（v1.1.0）生成的数据库文件：自动迁移成功，无 schema 报错
- [ ] 迁移后旧数据可读：报警历史 / 遥测历史条数与旧版本一致（抽查）
- [ ] `PRAGMA journal_mode` 返回 `wal`（WAL 随库文件持久化）
- [ ] 已知口径确认：`synchronous` 为连接级设置，新连接实际运行在默认 FULL（见基准文档 §6.4），发布版验证无数据损坏 / 死锁
- [ ] 降级路径说明：若本次 schema 有不可逆变更，须在 Release Notes 标注"回滚需恢复备份"

## 5. appsettings 配置检查

- [ ] 现场可调参数（批量写入 / 刷盘间隔 / 轮询间隔 / Watchdog 超时等）均在 `SmartEdgeHMI/appsettings.json`，无研发硬编码泄漏到现场配置面
- [ ] 默认值开箱即用（首启无需手改即可连接模拟器 / 设备）
- [ ] 发布包内无凭据残留：`Firmware/src/secrets.h`、测试密钥、本地路径不入包
- [ ] 日志级别 / 路径配置合理（生产为 Information 及以上，路径可写）

## 6. Watchdog 联调

- [ ] `SmartEdgeHMI.Watchdog` exe 与主程序成对打包（同目录 / 相对路径正确）
- [ ] 正常运行：3s 间隔心跳持续，Watchdog 不误杀
- [ ] 强杀主进程 → Watchdog 在 10s 心跳超时内重启主程序 → 心跳恢复
- [ ] 防呆启动：双开主程序时第二个实例被 Mutex 拦截
- [ ] 重启后串口重开成功（无端口占用残留导致的连续失败）

## 7. 打包与校验和

- [ ] 打包执行：`pwsh tools/package.ps1 -Version {版本}`（publish / 压缩 / SHA256 一键完成，产物输出至 `dist/`）
- [ ] 产物命名：`SmartEdgeHMI-{版本}-win-x64.zip`（脚本按 `-Version` / `-Runtime` 生成，tag 前缀 `v` 已去除）
- [ ] 包内容核对：主程序 exe + Watchdog exe + `appsettings.json` + 运行所需依赖；不含源码 / 测试 / 基准工程 / 模拟器
- [ ] 干净目录（及一台无开发环境的机器）解压后可直接启动
- [ ] SHA256 校验和（脚本自动写入 `dist/<zip>.sha256`，格式 `<hash>  <文件名>`），记录到 Release Notes：
      `SHA256: ________________________________________`

## 8. Release Notes

- [ ] 三段齐全：新增 / 修复 / 已知问题
- [ ] 修复项覆盖：串口关闭 CTS 竞态（`30390aa`）及相应异常观察器
- [ ] 性能结论引用基准实测数字，不使用"60FPS""零分配"等未实测泛化表述
- [ ] 硬件验收状态如实标注（未执行 / 已执行，链接 [hardware-validation.md](hardware-validation.md)）
- [ ] 升级 / 回滚说明包含数据库备份提示

## 9. 回滚方案（含数据库备份）

- [ ] 上一版本安装包与校验和仍可下载（当前：v1.1.0 zip 存档于 GitHub Releases）
- [ ] 回滚步骤成文并演练过一次：退出程序（含 Watchdog）→ 恢复上一版 zip → 启动验证连接与数据
- [ ] **数据库备份**：发布前对用户数据目录 `*.db`（连同 `-wal` / `-shm`）做冷备份；回滚前**再次**备份新版本已写入的数据，避免回滚覆盖
- [ ] 备份文件校验（可打开、条数正确），存放位置与保留期限已约定

## 10. 发布后 smoke 测试

- [ ] 从 Release 页下载发布包 → 校验 SHA256 → 解压启动成功
- [ ] 连接模拟器（或虚拟串口）：数据入库、图表渲染、指令下发（阈值 / 复位 / LED）、报警触发与确认各验证 1 次
- [ ] 断开 → 重连 1 次，日志无未观察异常（`ObserveAndLogAsync` 无 ERROR 输出）
- [ ] 强杀主进程 → Watchdog 重启 → 自动恢复 1 次
- [ ] smoke 结果（含时间 / 机器）回填到 Release 说明或本表附件

## 11. 责任人与签字

| 角色 | 责任人 | 日期 | 结论 |
| --- | --- | --- | --- |
| 发布负责人（lead） | | | ☐ 通过　☐ 阻塞（原因：________） |
| 质量复核（ZCode） | | | ☐ 通过　☐ 阻塞（原因：________） |

> 全部通过后方可发布 Release 并打 tag；任何一项阻塞即冻结发布，修复后重新走查本表。
