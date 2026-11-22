# tools

## 数据备份 / 恢复（P0-07 演练工具）

- `backup-data.ps1`：`pwsh ./tools/backup-data.ps1 -AppDir <发布目录> -OutDir <备份根目录> [-DryRun]` —— 冷备份 `SmartEdgeHMI.db` / `-wal` / `-shm` / `settings.json`（存在才纳入）到 `<OutDir>\<yyyyMMdd-HHmmss>\`（保留修改时间），生成 `manifest.json`（SHA256 清单、应用版本、SQLite journal_mode 与行数）并自校验；升级前、回滚前都必须先备份。
- `restore-data.ps1`：`pwsh ./tools/restore-data.ps1 -BackupDir <某次备份目录> -AppDir <目标发布目录> [-DryRun]` —— 先按 manifest 重算 SHA256 校验备份（失败退出码 4 且一个字节都不写），再把目标目录现有的数据文件整体移到 `<AppDir>\data-before-restore-<时间戳>\`（只移动、不删除）后拷回，并做恢复后校验。
- 守卫：目标目录里有 `SmartEdgeHMI` 进程正在运行时一律拒绝执行（含 `-DryRun`，退出码 2）；同一秒重跑会在目录名后追加序号，绝不覆盖已有备份。
- manifest 白名单：`restore-data.ps1` 只接受 `SmartEdgeHMI.db` / `SmartEdgeHMI.db-wal` / `SmartEdgeHMI.db-shm` / `settings.json` 四个文件名（与 `backup-data.ps1` 备份集一致），manifest 里的其他名字（含 `..\` 这类路径穿越形状）一律退出码 4 拒绝且不写入任何数据——manifest 不是可信输入。

| 退出码 | backup-data.ps1 | restore-data.ps1 |
|---|---|---|
| 0 | 成功 / DryRun 预演完成 | 同左 |
| 1 | 未预期异常 | 同左 |
| 2 | 目标目录里 SmartEdgeHMI 进程在跑 | 同左 |
| 3 | AppDir 下没有任何数据库文件（可改用 `-AppDir $env:LOCALAPPDATA\SmartEdgeHMI`） | 目标 AppDir 不存在 |
| 4 | 备份副本自校验失败 | 预校验失败（manifest 缺失/损坏/哈希不一致），未写任何数据 |
| 5 | — | 恢复后校验失败（原有数据仍在 `data-before-restore-*` 目录） |

## soak 观察数据判定（TASK-124）

- `analyze-soak.ps1`：`pwsh ./tools/analyze-soak.ps1 -Csv <soak.csv> [-IntervalSec 300] [-Hours 24] [-OutFile <报告.md>] [-Strict]` —— 只读分析 soak 采样 CSV，输出带判定（通过/不通过/需人工判断）的 Markdown 证据表（采样概况、存活、内存、句柄/线程、CPU、数据库、数据链路、日志、判定表、总判定），绝不写回 `-Csv`；判定阈值写死在脚本内，事后不改口径。
- 总判定：全部通过 `PASS`；有「不通过」`FAIL`；只有「需人工判断」`PASS-WITH-NOTES`；行数 < 期望采样数（`Hours*3600/IntervalSec`）的 80% 时一律 `INCOMPLETE`（只用于中途检查，不是结论）。
- 阈值口径：缺失 ≤3 且最大间隔 ≤1.35×IntervalSec；存活 0 次 `AppAlive=False` 且 PID 去重 =1；内存后 1/3 斜率 <10 MB/h 且全程增长 <50%；句柄全程增量 <50（50–200 需人工判断，>200 不通过）；WAL 最大 <50 MB 且末 1/10 极差占比 ≤5%；遥测达成率 ≥98%；日志错误/警告无新增。
- 退出码：`0` 正常出报告；`1` 仅在 `-Strict` 且存在「不通过」；`2` 输入/参数错误。

## 发布包校验（TASK-127）

- `verify-release.ps1`：`pwsh ./tools/verify-release.ps1 -Version 1.2.0` —— 只读校验 `dist\SmartEdgeHMI-<Version>-<Runtime>.zip` 的实际 SHA256 与同名 `.sha256` 一致，且当前哈希已写入 `release-checklist.md` 与 `release-notes-v<Version>.md`、两份文档里的包哈希引用行都标注「作废 / void」；不改任何文件，把「重打包后忘记改全文档」变成一条命令的硬失败。
- 「作废 / void」判定只覆盖**包哈希引用行**：同一行同时写了发布包文件名 `SmartEdgeHMI-<Version>-<Runtime>.zip`（大小写不敏感）与 64 位哈希的行，其中不等于当前哈希的必须标注；证据文件（如 soak.csv）等其他哈希引用行不同时含包文件名，有意不参与判定，避免把证据链哈希误判为过期。

| 参数 | 必填 | 默认 | 说明 |
|---|---|---|---|
| `-Version` | 是 | — | 版本号（如 `1.2.0`），用于定位 `SmartEdgeHMI-<Version>-<Runtime>.zip` 与 `release-notes-v<Version>.md` |
| `-DistDir` | 否 | `dist` | 产物目录，相对仓库根解析（绝对路径亦可） |
| `-Runtime` | 否 | `win-x64` | 目标运行时标识 |
| `-DocsDir` | 否 | `docs` | 发布文档目录，相对仓库根解析 |

- 退出码：`0` 四项全部通过；`1` 未预期异常；`2` 参数或目录无效（zip / `.sha256` / 发布文档不存在）；`3` zip 与 `.sha256` 不一致（含 `.sha256` 格式无法解析）；`4` 文档缺少当前哈希；`5` 存在未标注作废的包哈希引用行里的非当前哈希。
- 示例：`pwsh tools/verify-release.ps1 -Version 1.2.0`
- 时机：发布前、以及每次改完这两份发布文档后各跑一次。
