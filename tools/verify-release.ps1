#requires -Version 7.0

<#
.SYNOPSIS
    发布校验：发布包 zip 的实际 SHA256 与 .sha256 文件、发布文档里记录的哈希引用是否一致（只读校验，不修改任何文件）。

.DESCRIPTION
    以 <DistDir>\SmartEdgeHMI-<Version>-<Runtime>.zip 为基准依次校验四项：
    ① zip 与同名 .sha256 文件都存在；
    ② 重算 zip 的 SHA256（小写十六进制）与 .sha256 里记录的哈希一致（.sha256 形如「<hash>  <文件名>」）；
    ③ 当前哈希在 docs\release-checklist.md 与 docs\release-notes-v<Version>.md 各自至少出现一次；
    ④ 只判定「包哈希引用行」：同一行同时写了发布包文件名 SmartEdgeHMI-<Version>-<Runtime>.zip
       （大小写不敏感）与 64 位小写十六进制串（正则 \b[0-9a-f]{64}\b）的行；其中不等于当前哈希的，
       所在行必须含「作废」或「void」之一，否则逐条报出。
       证据文件（如 soak.csv）等其他哈希引用行不同时含包文件名，有意不参与判定，避免把证据链哈希误判为过期。
    文档里的省略写法（如 c02c87aa…e230b）匹配不到 64 位，不参与第④项判定，也不做模糊匹配。
    本脚本只读，不创建、不修改、不删除任何文件。

.OUTPUTS
    退出码：0 四项校验全部通过；1 未预期异常；2 参数或目录无效（zip / .sha256 / 发布文档不存在）；
    3 zip 与 .sha256 不一致（含 .sha256 内容无法按「<hash>  <文件名>」解析）；4 文档缺少当前哈希；
    5 存在未标注作废的包哈希引用行里的非当前哈希。

.EXAMPLE
    pwsh tools/verify-release.ps1 -Version 1.2.0

.EXAMPLE
    pwsh tools/verify-release.ps1 -Version 1.2.0 -DistDir dist -Runtime win-x64 -DocsDir docs
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Version,

    [ValidateNotNullOrEmpty()]
    [string]$DistDir = 'dist',

    [ValidateNotNullOrEmpty()]
    [string]$Runtime = 'win-x64',

    [ValidateNotNullOrEmpty()]
    [string]$DocsDir = 'docs'
)

$ErrorActionPreference = 'Stop'
$script:ExitCode = 0

# 仓库根（脚本在 tools/ 下，上一级即仓库根），相对的 -DistDir / -DocsDir 都从这里解析。
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

# 四项校验的状态，供最后的摘要行汇总；未执行到的项保持「未校验」。
$script:ItemStatus = [ordered]@{
    '① 产物存在'            = '未校验'
    '② .sha256 与包一致'     = '未校验'
    '③ 文档含当前哈希'       = '未校验'
    '④ 非当前哈希已标注作废'  = '未校验'
}

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

function Join-RepoPath {
    # 相对路径相对仓库根解析；绝对路径原样使用。
    param([Parameter(Mandatory = $true)][string]$Value)
    if ([System.IO.Path]::IsPathRooted($Value)) { return [System.IO.Path]::GetFullPath($Value) }
    return [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot $Value))
}

function Write-Summary {
    # 摘要固定为输出的最后一行，汇总四项结果；四项全部通过才用绿色。
    $parts = @()
    foreach ($key in $script:ItemStatus.Keys) {
        $parts += ('{0}={1}' -f $key, $script:ItemStatus[$key])
    }
    $line = '摘要：' + ($parts -join '；')
    if ($script:ExitCode -eq 0) {
        Write-Host $line -ForegroundColor Green
    }
    else {
        Write-Host $line -ForegroundColor Red
    }
}

function Invoke-VerifyMain {
    # 脚本里比较哈希一律 OrdinalIgnoreCase：十六进制只是表示形式，不同哈希不可能只差大小写。

    $distPath = Join-RepoPath -Value $DistDir
    $docsPath = Join-RepoPath -Value $DocsDir
    $zipName  = 'SmartEdgeHMI-{0}-{1}.zip' -f $Version, $Runtime
    $zipPath  = Join-Path $distPath $zipName
    $shaPath  = Join-Path $distPath ($zipName + '.sha256')
    $currentHash = $null

    Write-Host '== 发布包与文档哈希校验 =='
    Write-Host "仓库根：$script:RepoRoot"

    # ① 产物存在
    Write-Host ''
    Write-Host '== 第 1 步：产物存在 =='
    Write-Host "包：$zipPath"
    Write-Host "校验和：$shaPath"
    if (-not (Test-Path -LiteralPath $distPath -PathType Container)) {
        Stop-Run -Code 2 -Message "dist 目录不存在：'$distPath'。可用 -DistDir 指定实际产物目录。"
    }
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        Stop-Run -Code 2 -Message "发布包不存在：'$zipPath'。请先用 tools/package.ps1 打包，或核对 -Version / -Runtime / -DistDir。"
    }
    if (-not (Test-Path -LiteralPath $shaPath -PathType Leaf)) {
        Stop-Run -Code 2 -Message "校验和文件不存在：'$shaPath'。package.ps1 打包时会生成同名 .sha256，请先重新打包。"
    }
    $script:ItemStatus['① 产物存在'] = '通过'
    Write-Host '  zip 与 .sha256 都在。'

    # ② .sha256 与包一致
    Write-Host ''
    Write-Host '== 第 2 步：.sha256 与包比对 =='
    # .sha256 形如「<hash>  <文件名>」：取首个非空行的第一个空白分隔字段作为记录的哈希。
    $shaLines = @(Get-Content -LiteralPath $shaPath | Where-Object { $_.Trim().Length -gt 0 })
    if ($shaLines.Count -eq 0) {
        Stop-Run -Code 3 -Message ".sha256 文件是空的，解析不出哈希：'$shaPath'。"
    }
    $recordedHash = ($shaLines[0].Trim() -split '\s+')[0]
    if ($recordedHash -notmatch '^[0-9a-fA-F]{64}$') {
        Stop-Run -Code 3 -Message ".sha256 首行不符合「<hash>  <文件名>」格式（首个字段不是 64 位十六进制）：'$($shaLines[0])'。"
    }
    $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "  .sha256 记录：$recordedHash"
    Write-Host "  zip 实际值  ：$actualHash"
    if (-not [string]::Equals($recordedHash, $actualHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        $script:ItemStatus['② .sha256 与包一致'] = '不一致'
        Stop-Run -Code 3 -Message ".sha256 与包不一致：'$shaPath' 记录 $recordedHash，重算 zip 得 $actualHash。若刚重新打包，请同步更新两份发布文档里的哈希。"
    }
    $currentHash = $actualHash
    $script:ItemStatus['② .sha256 与包一致'] = '一致'
    Write-Host '  一致。'

    # ③ 文档含当前哈希
    Write-Host ''
    Write-Host '== 第 3 步：文档含当前哈希 =='
    $checklistPath = Join-Path $docsPath 'release-checklist.md'
    $notesPath     = Join-Path $docsPath ('release-notes-v{0}.md' -f $Version)
    foreach ($docPath in @($checklistPath, $notesPath)) {
        if (-not (Test-Path -LiteralPath $docPath -PathType Leaf)) {
            Stop-Run -Code 2 -Message "发布文档不存在：'$docPath'。可用 -DocsDir 指定文档目录。"
        }
    }

    # 两个文档统一逐行扫描 64 位小写十六进制串（\b[0-9a-f]{64}\b），记录 行号/哈希/整行：
    # 第③步看「是否出现当前哈希」，第④步看「包哈希引用行的非当前哈希是否标注作废」。
    # 只认小写十六进制与显式 64 位：大写串、省略写法（如 c02c87aa…e230b）都不参与第④项判定。
    $docScans = @()
    foreach ($docPath in @($checklistPath, $notesPath)) {
        $hits   = @()
        $lineNo = 0
        foreach ($line in (Get-Content -LiteralPath $docPath)) {
            $lineNo++
            foreach ($match in [regex]::Matches($line, '\b[0-9a-f]{64}\b')) {
                $hits += [pscustomobject]@{ Line = $lineNo; Hash = $match.Value; Text = $line }
            }
        }
        $docScans += [pscustomobject]@{ Path = $docPath; Name = (Split-Path -Leaf $docPath); Hits = $hits }
    }

    $missingDocs = @($docScans | Where-Object {
        -not ($_.Hits | Where-Object { [string]::Equals($_.Hash, $currentHash, [System.StringComparison]::OrdinalIgnoreCase) })
    })
    if ($missingDocs.Count -gt 0) {
        $script:ItemStatus['③ 文档含当前哈希'] = '缺失'
        $missingList = ($missingDocs | ForEach-Object { $_.Name }) -join '、'
        Stop-Run -Code 4 -Message "当前哈希（$currentHash）没有写入以下文档：$missingList。重新打包后必须同步更新 release-checklist 与 release-notes。"
    }
    $script:ItemStatus['③ 文档含当前哈希'] = '通过'
    Write-Host ('  当前哈希在 {0} 份文档里都至少出现一次。' -f $docScans.Count)

    # ④ 非当前哈希必须标注作废（只判定包哈希引用行）
    Write-Host ''
    Write-Host '== 第 4 步：非当前哈希必须标注作废 =='
    # 第④项只覆盖包哈希引用行（同一行还写了发布包文件名）；证据文件哈希行不参与判定，避免把证据链哈希误判为过期
    $violations = @()
    foreach ($doc in $docScans) {
        foreach ($hit in $doc.Hits) {
            if ([string]::Equals($hit.Hash, $currentHash, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            # 包文件名大小写不敏感；用 IndexOf 而非 -like，避免 -Version / -Runtime 里的通配符字符被当模式解释。
            if ($hit.Text.IndexOf($zipName, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
            # -match 默认不区分大小写：所在行含「作废」或「void」之一即可。
            if ($hit.Text -notmatch '作废|void') {
                $violations += ('{0}:{1}: {2}: {3}' -f $doc.Name, $hit.Line, $hit.Hash, $hit.Text)
            }
        }
    }
    if ($violations.Count -gt 0) {
        $script:ItemStatus['④ 非当前哈希已标注作废'] = ('{0} 处未标注' -f $violations.Count)
        Write-Host '  以下包哈希引用行的非当前哈希没有「作废」/「void」标注：'
        foreach ($violation in $violations) {
            Write-Host "  $violation"
        }
        Stop-Run -Code 5 -Message ('共 {0} 处包哈希引用行的非当前哈希未标注作废（见上面逐条列表）。规则：两份发布文档里，同一行写了发布包文件名 {1} 且含 64 位哈希的行中，不等于当前哈希的所在行必须含「作废」或「void」。' -f $violations.Count, $zipName)
    }
    $script:ItemStatus['④ 非当前哈希已标注作废'] = '通过'
    Write-Host '  全部已标注。'
}

try {
    Invoke-VerifyMain
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
finally {
    Write-Host ''
    Write-Summary
}

exit $script:ExitCode
