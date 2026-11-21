#requires -Version 7.0

<#
.SYNOPSIS
    SmartEdgeHMI 用户数据冷备份工具（发布检查表 §9 / P0-07 演练的可重复脚本）。

.DESCRIPTION
    在 SmartEdgeHMI 已从界面正常退出（Watchdog 也已退出）的前提下，把发布目录里的用户数据
    （SmartEdgeHMI.db、SmartEdgeHMI.db-wal、SmartEdgeHMI.db-shm、settings.json，存在才纳入）
    原样拷贝到 <OutDir>\<yyyyMMdd-HHmmss>\（保留修改时间），生成 manifest.json（SHA256 清单、
    应用版本、SQLite journal_mode 与行数），并对备份副本重算 SHA256 做自校验。

    只要检测到目标目录里有 SmartEdgeHMI 进程正在运行就拒绝执行（-DryRun 也不放行），
    避免备份到正在写入的数据；其他目录的 SmartEdgeHMI 实例不受影响。

.OUTPUTS
    退出码：0 成功（含 -DryRun 预演完成）；1 未预期异常；2 检测到 SmartEdgeHMI 进程在跑；
    3 AppDir 下没有任何数据库文件；4 备份副本自校验失败。

.EXAMPLE
    ./tools/backup-data.ps1 -AppDir "D:\deploy\SmartEdgeHMI" -OutDir "D:\backup\SmartEdgeHMI"

.EXAMPLE
    ./tools/backup-data.ps1 -AppDir "$env:LOCALAPPDATA\SmartEdgeHMI" -OutDir "D:\backup" -DryRun
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$AppDir,

    [Parameter(Mandatory = $true, Position = 1)]
    [ValidateNotNullOrEmpty()]
    [string]$OutDir,

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

function Get-DataFiles {
    # 定位要备份的数据文件：存在才纳入；一个数据库文件都没有 → 退出码 3。
    param([Parameter(Mandatory = $true)][string]$Dir)

    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) {
        Stop-Run -Code 3 -Message "数据目录不存在：'$Dir'。如果数据实际装在用户目录，请改用 -AppDir（值：$env:LOCALAPPDATA\SmartEdgeHMI，即 %LOCALAPPDATA%\SmartEdgeHMI）后重试。"
    }

    $candidateNames = @('SmartEdgeHMI.db', 'SmartEdgeHMI.db-wal', 'SmartEdgeHMI.db-shm', 'settings.json')
    $found = @()
    foreach ($name in $candidateNames) {
        $path = Join-Path $Dir $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $found += Get-Item -LiteralPath $path -Force
        }
    }

    $dbFileCount = @($found | Where-Object { $_.Name -like 'SmartEdgeHMI.db*' }).Count
    if ($dbFileCount -eq 0) {
        Stop-Run -Code 3 -Message "在目录 '$Dir' 下没有找到任何 SmartEdgeHMI 数据库文件（SmartEdgeHMI.db / SmartEdgeHMI.db-wal / SmartEdgeHMI.db-shm）。如果数据实际装在用户目录，请改用 -AppDir（值：$env:LOCALAPPDATA\SmartEdgeHMI，即 %LOCALAPPDATA%\SmartEdgeHMI）后重试。"
    }
    return ,$found
}

function Get-AppFileVersion {
    # 从 SmartEdgeHMI.exe 读 FileVersion；读不到写 unknown，不影响备份。
    param([Parameter(Mandatory = $true)][string]$Dir)
    $exePath = Join-Path $Dir 'SmartEdgeHMI.exe'
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { return 'unknown' }
    $fileVersion = (Get-Item -LiteralPath $exePath -Force).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($fileVersion)) { return 'unknown' }
    return $fileVersion
}

function Read-SqliteInfo {
    # 用本机 python 读 SQLite 元信息：先把备份副本（含 -wal，若有）复制到系统临时目录里的
    # 隔离工作区，再在那里以普通连接打开——这样 PRAGMA journal_mode 才会如实报告 WAL 模式
    # （immutable/mode=ro 只读方式会把它误报成 delete），行数也能包含 -wal 里未 checkpoint
    # 的数据，同时备份副本本身一个字节都不会被碰。任何一步失败都只记 null，绝不影响备份。
    param(
        [Parameter(Mandatory = $true)][string]$DbPath,
        [string]$WalPath
    )

    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) {
        Write-Host '  （本机没有 python，SQLite 元信息记为 null）' -ForegroundColor DarkGray
        return $null
    }

    $pyCode = @'
import json, sqlite3, sys

info = {'journalMode': None, 'telemetryRows': None, 'alarmRows': None}
try:
    con = sqlite3.connect(sys.argv[1])
    try:
        row = con.execute('PRAGMA journal_mode').fetchone()
        if row is not None:
            info['journalMode'] = row[0]
        for key, table in (('telemetryRows', 'TelemetryHistory'), ('alarmRows', 'AlarmRecords')):
            try:
                info[key] = con.execute('SELECT COUNT(*) FROM ' + table).fetchone()[0]
            except Exception:
                pass
    finally:
        con.close()
except Exception:
    pass
print(json.dumps(info))
'@

    $scratchDir = Join-Path ([System.IO.Path]::GetTempPath()) ('smh-sqlite-read-' + [Guid]::NewGuid().ToString('N'))
    try {
        [System.IO.Directory]::CreateDirectory($scratchDir) | Out-Null
        $scratchDb = Join-Path $scratchDir 'SmartEdgeHMI.db'
        [System.IO.File]::Copy($DbPath, $scratchDb, $true)
        if ($WalPath -and (Test-Path -LiteralPath $WalPath -PathType Leaf)) {
            [System.IO.File]::Copy($WalPath, (Join-Path $scratchDir 'SmartEdgeHMI.db-wal'), $true)
        }

        $output = & python -I -c $pyCode $scratchDb 2>$null
        if ($LASTEXITCODE -ne 0) { throw "python 退出码为 $LASTEXITCODE" }
        $jsonLine = @($output | Where-Object { $_ -and $_.ToString().TrimStart().StartsWith('{') }) | Select-Object -Last 1
        if (-not $jsonLine) { throw 'python 没有输出 JSON' }
        return ($jsonLine | ConvertFrom-Json)
    }
    catch {
        Write-Host "  （读取 SQLite 元信息失败，记为 null：$($_.Exception.Message)）" -ForegroundColor DarkGray
        return $null
    }
    finally {
        if (Test-Path -LiteralPath $scratchDir) {
            Remove-Item -LiteralPath $scratchDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-BackupMain {
    # 1. 守卫（-DryRun 也执行）：目标目录里有 SmartEdgeHMI 进程在跑 → 退出码 2
    Test-AppClosed -Action '冷备份' -TargetDir $AppDir

    $appPath = [System.IO.Path]::GetFullPath($AppDir)
    Write-Host '== SmartEdgeHMI 用户数据冷备份 =='
    Write-Host "数据目录：$appPath"

    # 2. 定位数据文件（存在才纳入）
    $dataFiles = Get-DataFiles -Dir $appPath
    $totalBytes = ($dataFiles | Measure-Object -Property Length -Sum).Sum
    Write-Host ("找到 {0} 个数据文件：" -f $dataFiles.Count)
    foreach ($f in $dataFiles) {
        Write-Host ("  {0}（{1:N0} 字节）" -f $f.Name, $f.Length)
    }

    # 产出目录：<OutDir>\<yyyyMMdd-HHmmss>（同一秒重跑时追加序号，绝不覆盖已有备份）
    $outRoot = [System.IO.Path]::GetFullPath($OutDir)
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupDir = Join-Path $outRoot $stamp
    $seq = 2
    while (Test-Path -LiteralPath $backupDir) {
        $backupDir = Join-Path $outRoot ('{0}-{1}' -f $stamp, $seq)
        $seq++
    }

    # 5. -DryRun：只打印文件清单、目标目录、预计字节数，不创建任何文件
    if ($DryRun) {
        Write-Host ''
        Write-Host '—— DryRun 预演：以下为执行计划，不创建任何文件 ——'
        Write-Host "目标备份目录：$backupDir（按当前时间推算，实际执行时会重新取时间戳）"
        foreach ($f in $dataFiles) {
            Write-Host ("  将拷贝 {0}：{1:N0} 字节，最后修改时间 {2:yyyy-MM-dd HH:mm:ss}" -f $f.Name, $f.Length, $f.LastWriteTime)
        }
        Write-Host ("预计拷贝 {0} 个文件，共 {1:N0} 字节；随后生成 manifest.json 并做 SHA256 自校验。" -f $dataFiles.Count, $totalBytes)
        return
    }

    # 3. 产出：拷贝（保留修改时间）+ manifest.json
    Write-Host ''
    Write-Host '第 1 步：拷贝数据文件（保留修改时间）…'
    [System.IO.Directory]::CreateDirectory($backupDir) | Out-Null
    foreach ($f in $dataFiles) {
        [System.IO.File]::Copy($f.FullName, (Join-Path $backupDir $f.Name), $true)
        Write-Host "  已拷贝：$($f.Name)"
    }

    Write-Host '第 2 步：计算源文件 SHA256，读取 SQLite 元信息（失败不影响备份）…'
    $fileEntries = @()
    foreach ($f in $dataFiles) {
        $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $fileEntries += [ordered]@{
            name          = $f.Name
            bytes         = $f.Length
            sha256        = $hash
            lastWriteTime = $f.LastWriteTime.ToString('yyyy-MM-ddTHH:mm:sszzz')
        }
        Write-Host ("  {0}：{1:N0} 字节，SHA256={2}" -f $f.Name, $f.Length, $hash)
    }

    $sqliteInfo = $null
    $mainDbCopy = Join-Path $backupDir 'SmartEdgeHMI.db'
    if (Test-Path -LiteralPath $mainDbCopy -PathType Leaf) {
        $sqliteInfo = Read-SqliteInfo -DbPath $mainDbCopy -WalPath (Join-Path $backupDir 'SmartEdgeHMI.db-wal')
    }

    Write-Host '第 3 步：写入 manifest.json …'
    $manifest = [ordered]@{
        createdAt  = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        machine    = $env:COMPUTERNAME
        appVersion = Get-AppFileVersion -Dir $appPath
        appDir     = $appPath
        files      = $fileEntries
        sqlite     = $sqliteInfo
    }
    $manifestPath = Join-Path $backupDir 'manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    Write-Host "  已写入：$manifestPath"

    # 4. 自校验：对备份副本重算 SHA256 与 manifest 比对，不一致 → 退出码 4
    Write-Host '第 4 步：自校验（重算备份副本 SHA256，与 manifest 比对）…'
    foreach ($entry in $fileEntries) {
        $copyPath = Join-Path $backupDir $entry.name
        if (-not (Test-Path -LiteralPath $copyPath -PathType Leaf)) {
            Stop-Run -Code 4 -Message "自校验失败：备份目录里缺少副本 '$($entry.name)'。"
        }
        $copyHash = (Get-FileHash -LiteralPath $copyPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $copyBytes = (Get-Item -LiteralPath $copyPath -Force).Length
        if ($copyBytes -ne $entry.bytes -or -not [string]::Equals($copyHash, [string]$entry.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Run -Code 4 -Message ("自校验失败：'{0}' 的备份副本（{1} 字节 / SHA256 {2}）与 manifest 记录（{3} 字节 / SHA256 {4}）不一致。" -f $entry.name, $copyBytes, $copyHash, $entry.bytes, $entry.sha256)
        }
        Write-Host "  校验通过：$($entry.name)"
    }

    # 6. 摘要
    Write-Host ''
    Write-Host ('摘要：备份目录={0}；文件数={1}；总字节={2:N0}；校验=通过' -f $backupDir, $fileEntries.Count, $totalBytes) -ForegroundColor Green
}

try {
    Invoke-BackupMain
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
