#requires -Version 7.0

<#
.SYNOPSIS
    SmartEdgeHMI 用户数据恢复工具（配合 tools/backup-data.ps1 产出的 manifest.json 使用）。

.DESCRIPTION
    先对备份目录做完整性校验（重算每个文件的 SHA256 与 manifest.json 比对），校验通过之前
    一个字节都不写；然后把目标发布目录里现有的 SmartEdgeHMI.db / SmartEdgeHMI.db-wal /
    SmartEdgeHMI.db-shm / settings.json（存在的话）整体移到
    <AppDir>\data-before-restore-<yyyyMMdd-HHmmss>\ 保留历史（只移动、绝不删除），
    再把备份文件拷回目标目录（保留修改时间），最后再做一次恢复后校验。

    只要检测到目标目录里有 SmartEdgeHMI 进程正在运行就拒绝执行（-DryRun 也不放行）；
    其他目录的 SmartEdgeHMI 实例不受影响。

.OUTPUTS
    退出码：0 成功（含 -DryRun 预演完成）；1 未预期异常；2 检测到 SmartEdgeHMI 进程在跑；
    3 目标 AppDir 不存在；4 预校验失败（manifest 缺失/损坏/哈希不一致），未写入任何数据；
    5 恢复后校验失败（原有数据仍在 data-before-restore-* 目录里，可手动移回）。

.EXAMPLE
    ./tools/restore-data.ps1 -BackupDir "D:\backup\SmartEdgeHMI\20261121-183000" -AppDir "D:\deploy\SmartEdgeHMI"

.EXAMPLE
    ./tools/restore-data.ps1 -BackupDir "D:\backup\SmartEdgeHMI\20261121-183000" -AppDir "D:\deploy\SmartEdgeHMI" -DryRun
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$BackupDir,

    [Parameter(Mandatory = $true, Position = 1)]
    [ValidateNotNullOrEmpty()]
    [string]$AppDir,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$script:ExitCode = 0

# 携带退出码的受控异常：所有可预期失败都经 Stop-Run 抛出，脚本末尾唯一的 exit 统一翻译退出码。
class ControlledExitException : System.Exception {
    [int] $Code
    ControlledExitException([int]$code, [string]$message) : base($message) { $this.Code = $code }
}

function Stop-Run {
    param(
        [Parameter(Mandatory = $true)][int]$Code,
        [Parameter(Mandatory = $true)][string]$Message
    )
    $script:ExitCode = $Code
    throw [ControlledExitException]::new($Code, $Message)
}

function Test-AppClosed {
    # 守卫：目标目录里正在运行的 SmartEdgeHMI 进程 → 拒绝（-DryRun 也一样），避免碰正在写入的数据。
    # 判定依据：进程可执行文件所在目录是否就是目标目录；路径读不出来时按最坏情况从严拒绝。
    # 机器上其他目录的 SmartEdgeHMI 实例（例如正在跑的观察进程）不影响对别的目录的备份/恢复。
    param(
        [Parameter(Mandatory = $true)][string]$Action,
        [Parameter(Mandatory = $true)][string]$TargetDir
    )
    $procs = @(Get-Process SmartEdgeHMI -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }

    $targetFullPath = [System.IO.Path]::GetFullPath($TargetDir).TrimEnd('\')
    $blocking = @()
    foreach ($p in $procs) {
        $procPath = $null
        try { $procPath = $p.MainModule.FileName } catch { $procPath = $null }
        if (-not $procPath) { $blocking += $p.Id; continue }
        $procDir = (Split-Path -Parent $procPath).TrimEnd('\')
        if ([string]::Equals($procDir, $targetFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            $blocking += $p.Id
        }
    }
    if ($blocking.Count -gt 0) {
        $pidList = ($blocking | Select-Object -Unique) -join '、'
        Stop-Run -Code 2 -Message "检测到 SmartEdgeHMI 进程（PID $pidList）正在目标目录里运行，${Action}必须先在界面里正常退出程序与 Watchdog 后再执行本脚本（-DryRun 也不放行，避免误操作正在写入的数据）。"
    }
}

function Read-BackupManifest {
    # 读 manifest.json 并做基本结构校验；任何一步失败都抛退出码 4（此时还没有写过任何字节）。
    param([Parameter(Mandatory = $true)][string]$BackupPath)

    if (-not (Test-Path -LiteralPath $BackupPath -PathType Container)) {
        Stop-Run -Code 4 -Message "备份目录不存在：'$BackupPath'，无法校验备份内容，未写入任何数据。"
    }
    $manifestPath = Join-Path $BackupPath 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Stop-Run -Code 4 -Message "备份目录里没有 manifest.json（'$manifestPath'），无法校验备份内容，未写入任何数据。"
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        Stop-Run -Code 4 -Message "manifest.json 解析失败（$($_.Exception.Message)），无法校验备份内容，未写入任何数据。"
    }
    $entries = @($manifest.files)
    if ($entries.Count -eq 0) {
        Stop-Run -Code 4 -Message 'manifest.json 里没有 files 记录，无法校验备份内容，未写入任何数据。'
    }
    foreach ($entry in $entries) {
        if (-not $entry.name -or $null -eq $entry.bytes -or -not $entry.sha256) {
            Stop-Run -Code 4 -Message 'manifest.json 的 files 记录缺少 name / bytes / sha256 字段，无法校验备份内容，未写入任何数据。'
        }
    }
    return ,$entries
}

function Find-ExistingDataFiles {
    # 目标目录里现存的、会被本次恢复影响的数据文件（标准四件套 ∪ manifest 列出的文件）。
    param(
        [Parameter(Mandatory = $true)][string]$TargetDir,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][array]$ManifestEntries
    )
    $names = @('SmartEdgeHMI.db', 'SmartEdgeHMI.db-wal', 'SmartEdgeHMI.db-shm', 'settings.json')
    $names = @($names + @($ManifestEntries | ForEach-Object { [string]$_.name }) | Select-Object -Unique)
    $existing = @()
    foreach ($name in $names) {
        $path = Join-Path $TargetDir $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $existing += Get-Item -LiteralPath $path -Force
        }
    }
    return ,$existing
}

function Invoke-RestoreMain {
    # 1. 守卫（-DryRun 也执行）：目标目录里有 SmartEdgeHMI 进程在跑 → 退出码 2
    Test-AppClosed -Action '恢复' -TargetDir $AppDir

    $backupPath = [System.IO.Path]::GetFullPath($BackupDir)
    $appPath = [System.IO.Path]::GetFullPath($AppDir)
    Write-Host '== SmartEdgeHMI 用户数据恢复 =='
    Write-Host "备份来源：$backupPath"
    Write-Host "目标目录：$appPath"

    if (-not (Test-Path -LiteralPath $appPath -PathType Container)) {
        Stop-Run -Code 3 -Message "目标数据目录不存在：'$appPath'。请确认 -AppDir 路径后重试。"
    }

    # 2. 先校验：校验通过之前一个字节都不写
    $entries = Read-BackupManifest -BackupPath $backupPath

    Write-Host '第 1 步：校验备份完整性（重算 SHA256 与 manifest 比对）…'
    foreach ($entry in $entries) {
        $srcPath = Join-Path $backupPath $entry.name
        if (-not (Test-Path -LiteralPath $srcPath -PathType Leaf)) {
            Stop-Run -Code 4 -Message "校验失败：备份目录里缺少清单文件 '$($entry.name)'，未写入任何数据。"
        }
        $actualHash = (Get-FileHash -LiteralPath $srcPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not [string]::Equals($actualHash, [string]$entry.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Run -Code 4 -Message ("校验失败：'{0}' 的 SHA256 与 manifest 不一致（manifest={1}，实际={2}），未写入任何数据。" -f $entry.name, $entry.sha256, $actualHash)
        }
        Write-Host ("  校验通过：{0}（{1:N0} 字节）" -f $entry.name, $entry.bytes)
    }

    # 3. 规划要让路的现有文件与 data-before-restore 目录（同一秒重跑时追加序号）
    $existing = Find-ExistingDataFiles -TargetDir $appPath -ManifestEntries $entries
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $beforeDir = Join-Path $appPath ('data-before-restore-{0}' -f $stamp)
    $seq = 2
    while (Test-Path -LiteralPath $beforeDir) {
        $beforeDir = Join-Path $appPath ('data-before-restore-{0}-{1}' -f $stamp, $seq)
        $seq++
    }

    # 5. -DryRun：打印计划，不做任何写操作
    if ($DryRun) {
        Write-Host ''
        Write-Host '—— DryRun 预演：以下为执行计划，不做任何写操作 ——'
        Write-Host ("将把 {0} 个备份文件从 '{1}' 拷回 '{2}'：" -f $entries.Count, $backupPath, $appPath)
        foreach ($entry in $entries) {
            Write-Host ("  将恢复 {0}（{1:N0} 字节；同名现有文件会先整体移走，不删除）" -f $entry.name, $entry.bytes)
        }
        if ($existing.Count -gt 0) {
            Write-Host "会先把以下现有文件整体移到：$beforeDir（按当前时间推算，实际执行时重新取时间戳；只移动、不删除）"
            foreach ($item in $existing) {
                Write-Host ("  将移走 {0}（{1:N0} 字节）" -f $item.Name, $item.Length)
            }
        }
        else {
            Write-Host '目标目录里没有需要移走的现有数据文件。'
        }
        return
    }

    Write-Host '第 2 步：把目标目录里的现有数据整体移走（只移动、不删除）…'
    if ($existing.Count -gt 0) {
        [System.IO.Directory]::CreateDirectory($beforeDir) | Out-Null
        foreach ($item in $existing) {
            [System.IO.File]::Move($item.FullName, (Join-Path $beforeDir $item.Name))
            Write-Host ("  已移走：{0} → {1}" -f $item.Name, (Split-Path -Leaf $beforeDir))
        }
    }
    else {
        Write-Host '  目标目录里没有需要移走的现有数据文件。'
    }

    Write-Host '第 3 步：从备份拷回文件（保留修改时间）…'
    foreach ($entry in $entries) {
        [System.IO.File]::Copy((Join-Path $backupPath $entry.name), (Join-Path $appPath $entry.name), $true)
        Write-Host ("  已恢复：{0}" -f $entry.name)
    }

    # 4. 恢复后校验：不一致 → 退出码 5，原数据仍在 data-before-restore-* 里
    Write-Host '第 4 步：恢复后校验（重算 SHA256 与 manifest 比对）…'
    foreach ($entry in $entries) {
        $restoredPath = Join-Path $appPath $entry.name
        if (-not (Test-Path -LiteralPath $restoredPath -PathType Leaf)) {
            Stop-Run -Code 5 -Message ("恢复后校验失败：目标目录里没有 '{0}'。原有数据仍保存在 '{1}' 目录里，可手动移回。" -f $entry.name, $beforeDir)
        }
        $restoredHash = (Get-FileHash -LiteralPath $restoredPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not [string]::Equals($restoredHash, [string]$entry.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Run -Code 5 -Message ("恢复后校验失败：'{0}' 的 SHA256 与 manifest 不一致（manifest={1}，实际={2}）。原有数据仍保存在 '{3}' 目录里，可手动移回。" -f $entry.name, $entry.sha256, $restoredHash, $beforeDir)
        }
        Write-Host ("  校验通过：{0}" -f $entry.name)
    }

    $restoredBytes = ($entries | Measure-Object -Property bytes -Sum).Sum
    Write-Host ''
    Write-Host ('摘要：来源备份={0}；目标目录={1}；恢复文件数={2}；总字节={3:N0}；校验=通过' -f $backupPath, $appPath, $entries.Count, $restoredBytes) -ForegroundColor Green
    if ($existing.Count -gt 0) {
        Write-Host ('     原有数据已整体移到（未删除）：{0}' -f $beforeDir)
    }
}

try {
    Invoke-RestoreMain
}
catch {
    if ($_.Exception -is [ControlledExitException]) {
        Write-Host ''
        Write-Host ('[错误·退出码 {0}] {1}' -f $_.Exception.Code, $_.Exception.Message) -ForegroundColor Red
    }
    else {
        $script:ExitCode = 1
        Write-Host ''
        Write-Host ('[错误·退出码 1] 未预期的异常：{0}' -f $_.Exception.Message) -ForegroundColor Red
        Write-Host ('    位置：{0}' -f $_.InvocationInfo.PositionMessage) -ForegroundColor Red
    }
}

exit $script:ExitCode
