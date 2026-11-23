# `v1.1.0` 标签与发布包追溯报告（已归档）

> 归档：历史追溯报告。当前发布记录见 [../release-notes-v1.2.0.md](../release-notes-v1.2.0.md)。

> 调查日期:2026-11-21;2026-11-22 依 P0-07 升级/回滚演练的新证据增补更正(§2.4 增补 E6/E7,§1 结论第 3 条改写)。调查手段:只读 Git/GitHub 查询 + 独立临时 worktree 编译验证(完成后已删除);2026-11-22 演练中已下载并运行 `v1.1.0` Release 资产。
> 原「未下载 Release 资产内容(超出本次允许手段),zip 内部未直接验证」的边界仅适用于 2026-11-21 首轮调查。

## 1. 结论(3 行以内)

- `v1.1.0` **不可从标签重建**:`dotnet build SmartEdgeHMI.sln -c Release` 在该标签的独立 worktree 中编译失败。
- 确切原因:`SmartEdgeHMI/Core/Services/WatchdogHeartbeatClient.cs` 第 41 行(位于非 DEBUG 分支)引用了从未声明的 `_settingsService`(全文件其余位置均为主构造函数参数 `settingsService`),CS0103。
- 现有 `v1.1.0` 发布包由**带未提交改动的标签工作区**构建:包内 `SmartEdgeHMI.exe` 的 `ProductVersion = 1.0.0+30b74e6fd87e16f46fbd03b6195f29f95bbeb411`(即标签提交)且为 Release 构建(运行日志含仅存在于 `#if DEBUG` 之 `#else` 分支的 `[DBG] Watchdog 未运行或未就绪` 行,见 §2.4 E6/E7),而标签树在 Release 下无法编译 → 构建时 HEAD 仍是标签提交 `30b74e6`,工作区带未提交改动;Release Notes 宣称的固件/串口重构在标签历史之外(子提交 `ad9dbcf`)。实际构建树无法复原。

## 2. 证据表

### 2.1 标签事实

| 项 | `v1.0.0` | `v1.1.0` |
| --- | --- | --- |
| 指向提交 | `4da97ef58e46cb7dd4025c948572d8cc397e7d0b` | `30b74e6fd87e16f46fbd03b6195f29f95bbeb411` |
| 类型 | lightweight(引用直指 commit,无 tag 对象) | lightweight(同左) |
| 签名 | 无(轻量标签无 tag 对象,不可携带签名) | 无(同左) |
| 时间 | 标签自身无时间;所指提交 AuthorDate/CommitDate 均为 2026-06-24 00:55:55 +08:00 | 标签自身无时间;所指提交均为 2026-06-29 12:08:36 +08:00 |
| 在主线(`git merge-base --is-ancestor <tag> main`) | 是 | 是(对 `main` 与 `origin/main` 均是) |
| 远端一致性(`git ls-remote --tags origin`) | 一致;单行引用、无 `^{}` 剥离行,远端同为轻量标签 | 一致(同左) |
| 标签注记 | 无;`git tag -n99` 显示的是所指提交的提交信息 | 无(同左) |

### 2.2 编译结果(仅验证 `v1.1.0`)

| 项 | 结果 |
| --- | --- |
| 方式 | `git worktree add <临时目录> v1.1.0`(detached HEAD)后,在该目录执行 `dotnet build SmartEdgeHMI.sln -c Release`,结束后 worktree 已移除 |
| 结果 | 失败,退出码 1,用时 5.76 秒 |
| 第一条错误(=唯一错误)原文 | `SmartEdgeHMI/Core/Services/WatchdogHeartbeatClient.cs(41,64): error CS0103: 当前上下文中不存在名称“_settingsService”` |
| 原因 | 第 41 行位于 `#if DEBUG` / `#else` 的非 DEBUG 分支;该名称全文件未声明,其余引用均用参数名 `settingsService`。属已提交源码自身的缺陷 |
| 排除项 | 非缺文件 / 缺包 / SDK 版本问题:NuGet restore 全部成功,`SmartEdgeHMI.Watchdog` 项目编译成功,总计 0 警告 1 错误 |
| `v1.0.0` | 未做编译验证(本次任务只要求 `v1.1.0`) |

### 2.3 发布包证据

| 位置 | `v1.1.0` | `v1.0.0` |
| --- | --- | --- |
| GitHub Release | 有;published 2026-06-29T07:23:18Z,created 2026-06-29T04:08:36Z | 有;published 2026-06-28T06:48:14Z |
| Release 资产 | 有:`SmartEdgeHMI-v1.1.0-win-x64.zip`,104,128,612 字节,state=uploaded | 有:`SmartEdgeHMI-v1.0.0-win-x64.zip`,82,209,515 字节,state=uploaded |
| SHA256 记录 | 有:`gh api` 资产 digest `sha256:9dcdfa4318c3d8852f8feb32f092f0ec0d7518cd88271093cb71980a6d503078`;仓库文档内无 | 有:digest `sha256:571669a03ed88afe0eace8a708207cb5aebbcecd77cb568da6804f91c681c640`;仓库文档内无 |
| 本地包记录 | 无:本机 `dist/` 仅含 v1.2.0 候选包及 `.sha256`;Git 历史(`git log --all -- dist/`,zip/exe/msi 入库)为空 | 无(同左) |
| 仓库内其他持久记录 | `README.md` 内下载链接指向该资产 | 提交 `bc94906` 曾更新 README 下载链接 |

### 2.4 包是否构建自该标签(依据可见证据;E6/E7 为 2026-11-22 演练增补)

| 编号 | 证据 | 出处 |
| --- | --- | --- |
| E1 | `v1.1.0` Release Notes 宣称:ESP8266 固件落地、串口改"同步读取+超时轮询"、GIF 更新 | `gh release view v1.1.0` |
| E2 | 标签树全树无任何 firmware 文件;`SerialPortService` 在标签内仍为异步 `ReadLoopAsync` | `git ls-tree -r v1.1.0` / `git show v1.1.0:…` |
| E3 | E1 所述内容与提交 `ad9dbcf`(2026-06-29T15:10:58+08:00)一致;该提交**不在** `v1.1.0` 历史内,是标签提交的直接子提交,在 `main` 上 | `git merge-base --is-ancestor ad9dbcf v1.1.0` → 否;对 `main` → 是 |
| E4 | Release published(07:23:18Z)晚于 `ad9dbcf` 提交时间(07:10:58Z)约 12 分钟;release created_at(04:08:36Z)与标签提交 CommitDate 秒级一致 | `gh release view` / `git show -s` |
| E5 | `v1.1.0` 时代无 CI、无打包脚本:`.github/`、`tools/package.ps1` 均于 2026-11-16(`f556af6`、`d593727`)才引入;CI run 记录最早为 2026-11-16 | `git ls-tree v1.1.0 -- .github`(空)/ `gh run list` |
| E6 | `v1.1.0` 包内 `SmartEdgeHMI.exe` 的 `ProductVersion = 1.0.0+30b74e6fd87e16f46fbd03b6195f29f95bbeb411`(即标签提交 `30b74e6`),`FileVersion = 1.0.0.0` | 2026-11-22 P0-07 升级/回滚演练:解包核对文件属性 |
| E7 | 该 exe 运行日志含 `[DBG] Watchdog 未运行或未就绪, 3000ms 后重试...`,该行只存在于 `#if DEBUG` 的 `#else`(非 DEBUG)分支 → 包为 Release 编译 | 2026-11-22 P0-07 升级/回滚演练:运行日志 |

判断(基于 E1–E7;E1–E5 为 2026-11-21 首轮调查,E6/E7 为 2026-11-22 演练增补):包内嵌的源码修订号 = 标签提交,但它是 Release 构建,而标签树在 Release 下无法编译(§2.2,CS0103)→ 构建时的源码树与标签树不同:HEAD 仍是 `30b74e6`,工作区带未提交改动。Release Notes 宣称的固件/串口重构在标签历史之外(子提交 `ad9dbcf`),属当时工作区的未提交内容。实际构建树无法复原。

## 3. 与现有文档的对照

| 文档·位置 | 原表述(摘) | 本次结论 |
| --- | --- | --- |
| `docs/archive/next-steps-v1.2.md` §4 P1-02 | "消除旧版本『标签不可编译、包来源不一致』的追溯断点" | 两半均证实:「标签不可编译」实测复现(§2.2);「包来源不一致」有 E1–E7 支持:包由带未提交改动的标签工作区构建,实际构建树无法复原 |
| `docs/release-notes-v1.2.0.md` 已知问题 | "`v1.1.0` tag 不可编译(`WatchdogHeartbeatClient.cs(41,64)` CS0103:`_settingsService` 不存在)" | **证实**:错误位置 (41,64)、错误码 CS0103、变量名逐字一致;完整仓库路径为 `SmartEdgeHMI/Core/Services/WatchdogHeartbeatClient.cs` |
| `docs/release-notes-v1.2.0.md` | "v1.1.0 发布包来源与该 tag 不一致,需追溯" | 证实(§2.4 E1–E7):包由带未提交改动的标签工作区构建(HEAD = 标签提交,Release 编译),实际构建树无法复原 |
| `docs/release-checklist.md` 数据迁移项 | "v1.1.0 tag 不可编译,旧库由 `f556af6` 生成(同 schema)" | "tag 不可编译"证实;旧库由 `f556af6` 生成的过程本次未取证据,无法核实 |
| `docs/release-checklist.md` 回滚项 | "v1.1.0 zip 存档于 GitHub Releases" | 证实:资产存在且 state=uploaded(§2.3) |
| — | `v1.1.0` 的实际构建提交、构建方式与机器、本地构建/打包记录 | **未查到** |

## 4. 后续证据链规则(自 v1.2.0 起)

1. **同一提交五元组**:发布提交上同时产出 tag、候选 zip、SHA256、CI run、Release Notes;五者在 Release Notes 头部逐一写明并互相引用同一提交号,不得各指各的版本。
2. **标签一律 annotated**:对发布提交执行 `git tag -a vX.Y.Z -m …`,不再使用 lightweight(轻量标签无签名、无独立时间,追溯信息只能依赖提交本身)。
3. **打包只经 `tools/package.ps1`**:在该发布提交的干净 worktree 产出;SHA256 由脚本写入 `dist/<zip>.sha256` 并原样抄录进 Release Notes,禁止手工另算或改写。
4. **上传后核对 digest**:用 `gh api` 读取 Release 资产 `digest`,与本地 `.sha256` 一致后才正式 publish;不一致即作废重打。
5. **CI 先行**:发布提交的 CI run 必须为 success 且链接写入 Release Notes;无 CI run 的本机构建不得作为发布包。
6. **不可重建的旧标签策略**:不重写公开历史、不打 corrective tag、不移动现有 tag;`v1.1.0` 不可编译、其发布包不能由该标签复现的限制仅在本文与 v1.2.0 Release Notes 声明;回滚与迁移基线选用 `main` 上可编译的提交,而非 `v1.1.0` 标签。
