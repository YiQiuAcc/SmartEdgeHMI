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
