#requires -Version 7.0
<#
.SYNOPSIS
    对 soak.ps1 采集的观察 CSV 输出一份带判定（通过/不通过/需人工判断）的 Markdown 证据表（TASK-124，P1-01）。
.DESCRIPTION
    只读输入文件，不写回 -Csv。判定阈值固定在本脚本 $Thresholds（默认口径），不得在事后修改判定口径，修改阈值必须经过代码评审。
    行数低于期望采样数（Hours*3600/IntervalSec）的 80% 时，总判定为 INCOMPLETE（中途检查，不是结论）。
    退出码：0 = 正常出报告（含 INCOMPLETE）；1 = -Strict 且存在「不通过」判定；2 = 输入/参数错误。
.EXAMPLE
    pwsh ./tools/analyze-soak.ps1 -Csv C:\soak\soak.csv
.EXAMPLE
    pwsh ./tools/analyze-soak.ps1 -Csv C:\soak\soak.csv -OutFile C:\soak\report.md -Strict
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Csv,

    [int]$IntervalSec = 300,

    [double]$Hours = 24,

    [string]$OutFile,

    [switch]$Strict
)

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
# 解析与格式化统一使用 InvariantCulture，避免区域设置改变小数点与千分位的解释
$Inv = [System.Globalization.CultureInfo]::InvariantCulture

# 判定阈值：默认值即判定口径，改动必须经过代码评审
$Thresholds = [pscustomobject]@{
    MaxMissing          = 3      # 采样完整性：缺失 ≤ 3 行
    GapFactor           = 1.35   # 采样完整性：最大间隔 ≤ IntervalSec × 1.35
    MaxAliveFalse       = 0      # 存活：AppAlive=False 条数 = 0
    MaxPidCount         = 1      # 存活：PID 去重 = 1
    MemSlopeMbPerHour   = 10.0   # 内存：后 1/3 工作集斜率 < 10 MB/h
    MemGrowthPct        = 50.0   # 内存：全程工作集增长 < 50%
    MemGcDropPct        = 15.0   # 内存备注：相邻采样下跌 > 15% 记为 GC 回落（非泄漏）
    HandleIncPass       = 50     # 句柄：全程增量 < 50 通过；50–200 需人工判断
    HandleIncFail       = 200    # 句柄：全程增量 > 200 不通过
    WalMaxMb            = 50.0   # WAL：最大 < 50 MB
    WalTailRangePct     = 5.0    # WAL：末 1/10 采样极差 / 最大 ≤ 5%
    TelemetryMinPct     = 98.0   # 遥测：达成率 ≥ 98%（TelemetryRows 增量 / 期望行数）
    IncompleteRowFactor = 0.80   # 行数 < 期望采样数 × 80% → 总判定 INCOMPLETE
}

function Fail2([string]$msg) {
    [Console]::Error.WriteLine('analyze-soak: ' + $msg)
    exit 2
}

if ($IntervalSec -le 0) { Fail2 '-IntervalSec 必须为正数' }
if ($Hours -le 0) { Fail2 '-Hours 必须为正数' }
if (-not (Test-Path -LiteralPath $Csv -PathType Leaf)) { Fail2 "输入文件不存在：$Csv" }

function ConvertTo-Num($text) {
    if ($null -eq $text) { return $null }
    $t = ([string]$text).Trim()
    if ($t.Length -eq 0) { return $null }
    $v = 0.0
    if ([double]::TryParse($t, [System.Globalization.NumberStyles]::Float, $Inv, [ref]$v)) { return $v }
    return $null
}

function ConvertTo-Ts($text) {
    if ($null -eq $text) { return $null }
    $t = ([string]$text).Trim()
    if ($t.Length -eq 0) { return $null }
    $v = [datetime]::MinValue
    if ([datetime]::TryParseExact($t, 'yyyy-MM-dd HH:mm:ss', $Inv, [System.Globalization.DateTimeStyles]::None, [ref]$v)) { return $v }
    if ([datetime]::TryParse($t, $Inv, [System.Globalization.DateTimeStyles]::None, [ref]$v)) { return $v }
    return $null
}

function FmtN($v, [int]$digits = 1) {
    if ($null -eq $v) { return '—' }
    return ([double]$v).ToString('N' + $digits, $Inv)
}
function FmtI($v) {
    if ($null -eq $v) { return '—' }
    return [math]::Round([double]$v).ToString('N0', $Inv)
}
function FmtMb($bytes, [int]$digits = 2) {
    if ($null -eq $bytes) { return '—' }
    return (([double]$bytes) / 1MB).ToString('N' + $digits, $Inv)
}
function FmtPct($v, [int]$digits = 1) {
    if ($null -eq $v) { return '—' }
    return ([double]$v).ToString('N' + $digits, $Inv) + '%'
}
function FmtTs($ts) {
    if ($null -eq $ts) { return '—' }
    return ([datetime]$ts).ToString('yyyy-MM-dd HH:mm:ss', $Inv)
}

function Get-ValidList($list, [string]$field) {
    @($list | Where-Object { $null -ne $_.$field })
}

# 输入列表须已按字段过滤（不含 null）：返回首值/末值/最大（含时间）/最小（含时间）
function Get-SeriesStats($list, [string]$field) {
    $out = @{ First = $null; Last = $null; Max = $null; MaxTs = $null; Min = $null; MinTs = $null; Count = @($list).Count }
    if ($out.Count -gt 0) {
        $out.First = $list[0].$field
        $out.Last  = $list[-1].$field
        $maxV = [double]::NegativeInfinity
        $minV = [double]::PositiveInfinity
        foreach ($s in $list) {
            $v = [double]$s.$field
            if ($v -gt $maxV) { $maxV = $v; $out.MaxTs = $s.Ts }
            if ($v -lt $minV) { $minV = $v; $out.MinTs = $s.Ts }
        }
        $out.Max = $maxV
        $out.Min = $minV
    }
    return $out
}

# 后 1/3 采样的最小二乘斜率（换算成每小时）；样本不足时返回 $null
function Get-SlopePerHour($list, [string]$field) {
    $k = [math]::Floor(@($list).Count / 3)
    if ($k -lt 2) { return $null }
    $win = @($list | Select-Object -Last $k)
    $t0 = $win[0].Ts
    $xs = [System.Collections.Generic.List[double]]::new()
    $ys = [System.Collections.Generic.List[double]]::new()
    foreach ($s in $win) {
        $v = $s.$field
        if ($null -ne $v) {
            [void]$xs.Add((($s.Ts - $t0).TotalSeconds))
            [void]$ys.Add([double]$v)
        }
    }
    if ($xs.Count -lt 2) { return $null }
    $mx = ($xs | Measure-Object -Average).Average
    $my = ($ys | Measure-Object -Average).Average
    $sxy = 0.0
    $sxx = 0.0
    for ($i = 0; $i -lt $xs.Count; $i++) {
        $dx = $xs[$i] - $mx
        $sxy += $dx * ($ys[$i] - $my)
        $sxx += $dx * $dx
    }
    if ($sxx -le 0) { return $null }
    return ($sxy / $sxx) * 3600.0
}

# 读入（只读）
$raw = Import-Csv -LiteralPath $Csv
if (@($raw).Count -eq 0) { Fail2 "CSV 没有数据行：$Csv" }
$firstRow = @($raw)[0]
$requiredColumns = @('Timestamp','AppAlive','Pid','WorkingSetMB','PrivateMB','Handles','Threads','CpuSeconds','DbBytes','WalBytes','ShmBytes','TelemetryRows','AlarmRows','AlarmsNormal','JournalMode','LogBytes','LogErrors','LogWarnings','LinkEvents')
$absent = @($requiredColumns | Where-Object { -not ($firstRow.PSObject.Properties.Name -contains $_) })
if ($absent.Count -gt 0) { Fail2 ('CSV 缺少必需列：' + ($absent -join ', ')) }

$samples = [System.Collections.Generic.List[object]]::new()
$badTs = 0
foreach ($r in $raw) {
    $ts = ConvertTo-Ts $r.'Timestamp'
    if ($null -eq $ts) { $badTs++ }
    $aliveRaw = ([string]$r.'AppAlive').Trim()
    [void]$samples.Add([pscustomobject]@{
        Ts           = $ts
        AliveRaw     = $aliveRaw
        AliveFalse   = ($aliveRaw -match '^false$')
        AliveUnknown = ($aliveRaw.Length -gt 0 -and $aliveRaw -notmatch '^(true|false)$')
        Pid          = ([string]$r.'Pid').Trim()
        Ws           = ConvertTo-Num $r.'WorkingSetMB'
        Handles      = ConvertTo-Num $r.'Handles'
        Threads      = ConvertTo-Num $r.'Threads'
        Cpu          = ConvertTo-Num $r.'CpuSeconds'
        Db           = ConvertTo-Num $r.'DbBytes'
        Wal          = ConvertTo-Num $r.'WalBytes'
        Telemetry    = ConvertTo-Num $r.'TelemetryRows'
        AlarmRows    = ConvertTo-Num $r.'AlarmRows'
        Journal      = ([string]$r.'JournalMode').Trim()
        LogBytes     = ConvertTo-Num $r.'LogBytes'
        LogErr       = ConvertTo-Num $r.'LogErrors'
        LogWrn       = ConvertTo-Num $r.'LogWarnings'
        Link         = ConvertTo-Num $r.'LinkEvents'
    })
}

# 采样概况
$n = $samples.Count
$expectedSamples = [int][math]::Round($Hours * 3600.0 / $IntervalSec)
$missingCount = [math]::Max(0, $expectedSamples - $n)
$isIncomplete = $n -lt ($expectedSamples * $Thresholds.IncompleteRowFactor)

$tsOk = @(Get-ValidList $samples 'Ts')
$firstTs = if ($tsOk.Count -ge 1) { $tsOk[0].Ts } else { $null }
$lastTs  = if ($tsOk.Count -ge 1) { $tsOk[-1].Ts } else { $null }
$spanSec = if ($tsOk.Count -ge 2) { ($tsOk[-1].Ts - $tsOk[0].Ts).TotalSeconds } else { $null }
$spanHours = if ($null -ne $spanSec) { $spanSec / 3600.0 } else { $null }

$maxGapSec = $null; $maxGapFrom = $null; $maxGapTo = $null
$gapOverCount = 0
$nonMonoCount = 0
for ($i = 1; $i -lt $tsOk.Count; $i++) {
    $dt = ($tsOk[$i].Ts - $tsOk[$i - 1].Ts).TotalSeconds
    if ($dt -le 0) { $nonMonoCount++; continue }
    if ($null -eq $maxGapSec -or $dt -gt $maxGapSec) {
        $maxGapSec = $dt; $maxGapFrom = $tsOk[$i - 1].Ts; $maxGapTo = $tsOk[$i].Ts
    }
    if ($dt -gt ($IntervalSec * $Thresholds.GapFactor)) { $gapOverCount++ }
}

# 存活
$falseSamples = @($samples | Where-Object { $_.AliveFalse })
$unknownAliveCount = @($samples | Where-Object { $_.AliveUnknown }).Count
$pidGroups = @($samples | Where-Object { $_.Pid.Length -gt 0 } | Group-Object -Property Pid)
$pidCount = $pidGroups.Count
$emptyPidCount = @($samples | Where-Object { $_.Pid.Length -eq 0 }).Count

# 内存
$wsList = @(Get-ValidList $tsOk 'Ws')
$ws = Get-SeriesStats $wsList 'Ws'
$wsSlope = Get-SlopePerHour $tsOk 'Ws'
$wsGrowthPct = $null
if ($null -ne $ws.First -and $null -ne $ws.Last -and [double]$ws.First -gt 0) {
    $wsGrowthPct = ([double]$ws.Last - [double]$ws.First) / [double]$ws.First * 100.0
}
$gcDrops = @()
for ($i = 1; $i -lt $wsList.Count; $i++) {
    $prev = [double]$wsList[$i - 1].Ws
    $cur  = [double]$wsList[$i].Ws
    if ($prev -gt 0 -and $cur -lt ($prev * (1.0 - $Thresholds.MemGcDropPct / 100.0))) {
        $gcDrops += [pscustomobject]@{ Ts = $wsList[$i].Ts; From = $prev; To = $cur }
    }
}

# 句柄 / 线程
$hdList = @(Get-ValidList $tsOk 'Handles')
$hd = Get-SeriesStats $hdList 'Handles'
$hdSlope = Get-SlopePerHour $tsOk 'Handles'
$hdInc = if ($null -ne $hd.First -and $null -ne $hd.Last) { ([double]$hd.Last - [double]$hd.First) } else { $null }
$thList = @(Get-ValidList $tsOk 'Threads')
$th = Get-SeriesStats $thList 'Threads'
$thSlope = Get-SlopePerHour $tsOk 'Threads'
$thInc = if ($null -ne $th.First -and $null -ne $th.Last) { ([double]$th.Last - [double]$th.First) } else { $null }

# CPU
$cpuList = @(Get-ValidList $tsOk 'Cpu')
$cpuFirst = if ($cpuList.Count -ge 1) { [double]$cpuList[0].Cpu } else { $null }
$cpuLast  = if ($cpuList.Count -ge 1) { [double]$cpuList[-1].Cpu } else { $null }
$cpuDelta = if ($cpuList.Count -ge 2) { $cpuLast - $cpuFirst } else { $null }
$cpuPct = if ($null -ne $cpuDelta -and $spanSec -gt 0) { $cpuDelta / $spanSec * 100.0 } else { $null }

# 数据库
$dbList = @(Get-ValidList $tsOk 'Db')
$dbFirst = if ($dbList.Count -ge 1) { [double]$dbList[0].Db } else { $null }
$dbLast  = if ($dbList.Count -ge 1) { [double]$dbList[-1].Db } else { $null }
$dbDelta = if ($dbList.Count -ge 2) { $dbLast - $dbFirst } else { $null }
$dbRateMbH = if ($null -ne $dbDelta -and $spanHours -gt 0) { ($dbDelta / 1MB) / $spanHours } else { $null }
$dbExtrap30dMb = if ($null -ne $dbRateMbH -and $dbList.Count -ge 1) { ($dbLast / 1MB) + $dbRateMbH * 720.0 } else { $null }

$walList = @(Get-ValidList $tsOk 'Wal')
$walMax = $null; $walMaxTs = $null
if ($walList.Count -gt 0) {
    $walMax = [double]::NegativeInfinity
    foreach ($s in $walList) {
        if ([double]$s.Wal -gt $walMax) { $walMax = [double]$s.Wal; $walMaxTs = $s.Ts }
    }
}
$k10 = [math]::Max(1, [math]::Floor($walList.Count / 10))
$walTailRange = $null
if ($walList.Count -ge 2) {
    $tail = @($walList | Select-Object -Last $k10)
    $tmin = [double]::PositiveInfinity
    $tmax = [double]::NegativeInfinity
    foreach ($s in $tail) {
        $v = [double]$s.Wal
        if ($v -gt $tmax) { $tmax = $v }
        if ($v -lt $tmin) { $tmin = $v }
    }
    $walTailRange = $tmax - $tmin
}
elseif ($walList.Count -eq 1) { $walTailRange = 0.0 }
$walRangePct = if ($null -ne $walTailRange -and $null -ne $walMax -and $walMax -gt 0) { $walTailRange / $walMax * 100.0 } else { $null }

$journalSummary = @($samples | Where-Object { $_.Journal.Length -gt 0 } | Group-Object -Property Journal | ForEach-Object { "$($_.Name) ×$($_.Count)" }) -join '、'
if ([string]::IsNullOrEmpty($journalSummary)) { $journalSummary = $null }

# 数据链路
$telList = @(Get-ValidList $tsOk 'Telemetry')
$telFirst = if ($telList.Count -ge 1) { [double]$telList[0].Telemetry } else { $null }
$telLast  = if ($telList.Count -ge 1) { [double]$telList[-1].Telemetry } else { $null }
$telDelta = if ($telList.Count -ge 2) { $telLast - $telFirst } else { $null }
$expectedTelRows = $Hours * 3600.0
$telRowsPerSec = if ($null -ne $telDelta -and $spanSec -gt 0) { $telDelta / $spanSec } else { $null }
$telAchPct = if ($null -ne $telDelta) { $telDelta / $expectedTelRows * 100.0 } else { $null }

$alrList = @(Get-ValidList $tsOk 'AlarmRows')
$alarmLast = if ($alrList.Count -ge 1) { [double]$alrList[-1].AlarmRows } else { $null }

$linkList = @(Get-ValidList $tsOk 'Link')
$linkFirst = if ($linkList.Count -ge 1) { [double]$linkList[0].Link } else { $null }
$linkLast  = if ($linkList.Count -ge 1) { [double]$linkList[-1].Link } else { $null }
$linkDelta = if ($linkList.Count -ge 2) { $linkLast - $linkFirst } else { $null }

# 日志
$lbList = @(Get-ValidList $tsOk 'LogBytes')
$lbFirst = if ($lbList.Count -ge 1) { [double]$lbList[0].LogBytes } else { $null }
$lbLast  = if ($lbList.Count -ge 1) { [double]$lbList[-1].LogBytes } else { $null }
$leList = @(Get-ValidList $tsOk 'LogErr')
$leFirst = if ($leList.Count -ge 1) { [double]$leList[0].LogErr } else { $null }
$leLast  = if ($leList.Count -ge 1) { [double]$leList[-1].LogErr } else { $null }
$leInc = if ($leList.Count -ge 2) { $leLast - $leFirst } else { $null }
$lwList = @(Get-ValidList $tsOk 'LogWrn')
$lwFirst = if ($lwList.Count -ge 1) { [double]$lwList[0].LogWrn } else { $null }
$lwLast  = if ($lwList.Count -ge 1) { [double]$lwList[-1].LogWrn } else { $null }
$lwInc = if ($lwList.Count -ge 2) { $lwLast - $lwFirst } else { $null }
$logDecreaseNote = ''
if (($null -ne $leInc -and $leInc -lt 0) -or ($null -ne $lwInc -and $lwInc -lt 0)) {
    $logDecreaseNote = '错误/警告计数出现下降——可能因日志文件按天滚动或应用重启换了计数基线，需人工核对'
}

# 判定
$verdicts = [System.Collections.Generic.List[object]]::new()

# 1 采样完整性
if ($null -eq $maxGapSec) {
    $vSampling = '需人工判断'
    $samplingActual = "缺失 $(FmtI $missingCount) 行；最大间隔：有效时间戳不足，无法计算"
} else {
    $samplingActual = "缺失 $(FmtI $missingCount) 行；最大间隔 $(FmtI $maxGapSec) 秒"
    if ($missingCount -le $Thresholds.MaxMissing -and $maxGapSec -le ($IntervalSec * $Thresholds.GapFactor)) { $vSampling = '通过' } else { $vSampling = '不通过' }
}
[void]$verdicts.Add([pscustomobject]@{
    Item      = '采样完整性'
    Actual    = $samplingActual
    Threshold = "缺失 ≤ $($Thresholds.MaxMissing) 且最大间隔 ≤ $(FmtI ($IntervalSec * $Thresholds.GapFactor)) 秒（缺失按整段期望 $expectedSamples 行计）"
    Verdict   = $vSampling
})

# 2 存活
$aliveActual = "AppAlive=False 共 $($falseSamples.Count) 条；PID 去重 $pidCount 个"
$vAlive = if ($falseSamples.Count -le $Thresholds.MaxAliveFalse -and $pidCount -eq $Thresholds.MaxPidCount) { '通过' } else { '不通过' }
[void]$verdicts.Add([pscustomobject]@{
    Item      = '存活'
    Actual    = $aliveActual
    Threshold = "AppAlive=False = $($Thresholds.MaxAliveFalse) 且 PID 去重 = $($Thresholds.MaxPidCount)"
    Verdict   = $vAlive
})

# 3 内存
$memActual = "后 1/3 斜率 $(FmtN $wsSlope 2) MB/h；全程增长 $(FmtPct $wsGrowthPct 1)"
$vMem = if ($null -eq $wsSlope -or $null -eq $wsGrowthPct) { '需人工判断' }
        elseif ($wsSlope -ge $Thresholds.MemSlopeMbPerHour -or $wsGrowthPct -ge $Thresholds.MemGrowthPct) { '不通过' }
        else { '通过' }
[void]$verdicts.Add([pscustomobject]@{
    Item      = '内存（工作集）'
    Actual    = $memActual
    Threshold = "后 1/3 斜率 < $(FmtN $Thresholds.MemSlopeMbPerHour 1) MB/h 且全程增长 < $(FmtN $Thresholds.MemGrowthPct 0)%"
    Verdict   = $vMem
})

# 4 句柄
$hdActual = "全程增量 $(FmtI $hdInc) 个"
$hdThreshold = "< $($Thresholds.HandleIncPass) 通过；$($Thresholds.HandleIncPass)–$($Thresholds.HandleIncFail) 需人工判断；> $($Thresholds.HandleIncFail) 不通过"
$vHandle = if ($null -eq $hdInc) { '需人工判断' }
           elseif ($hdInc -gt $Thresholds.HandleIncFail) { '不通过' }
           elseif ($hdInc -ge $Thresholds.HandleIncPass) { '需人工判断' }
           else { '通过' }
[void]$verdicts.Add([pscustomobject]@{
    Item      = '句柄'
    Actual    = $hdActual
    Threshold = $hdThreshold
    Verdict   = $vHandle
})

# 5 WAL
$walRangeText = FmtPct $walRangePct 2
if ($null -ne $walMax -and $walMax -eq 0) { $walRangeText = '—（WAL 恒为 0，按最大值口径判）' }
$walActual = "最大 $(FmtMb $walMax 2) MB；末 1/10 极差占比 $walRangeText"
$vWal = if ($null -eq $walMax) { '需人工判断' }
        elseif (($walMax / 1MB) -ge $Thresholds.WalMaxMb) { '不通过' }
        elseif ($null -eq $walRangePct) { '通过' }
        elseif ($walRangePct -gt $Thresholds.WalTailRangePct) { '不通过' }
        else { '通过' }
[void]$verdicts.Add([pscustomobject]@{
    Item      = 'WAL'
    Actual    = $walActual
    Threshold = "最大 < $(FmtN $Thresholds.WalMaxMb 1) MB 且末 1/10 极差占比 ≤ $(FmtN $Thresholds.WalTailRangePct 1)%"
    Verdict   = $vWal
})

# 6 遥测
$telActual = "TelemetryRows 增量 $(FmtI $telDelta) 行 / 期望 $(FmtI $expectedTelRows) 行 → 达成率 $(FmtPct $telAchPct 1)"
$vTel = if ($null -eq $telAchPct) { '需人工判断' }
        elseif ($telAchPct -ge $Thresholds.TelemetryMinPct) { '通过' }
        else { '不通过' }
[void]$verdicts.Add([pscustomobject]@{
    Item      = '遥测'
    Actual    = $telActual
    Threshold = "达成率 ≥ $(FmtN $Thresholds.TelemetryMinPct 1)%（增量 / 期望行数）"
    Verdict   = $vTel
})

# 7 日志
$logActual = "LogErrors 新增 $(FmtI $leInc)；LogWarnings 新增 $(FmtI $lwInc)"
if ($logDecreaseNote) { $logActual += "；$logDecreaseNote" }
$vLog = if ($null -eq $leInc -or $null -eq $lwInc) { '需人工判断' }
        elseif ($leInc -gt 0 -or $lwInc -gt 0) { '需人工判断' }
        else { '通过' }
[void]$verdicts.Add([pscustomobject]@{
    Item      = '日志'
    Actual    = $logActual
    Threshold = '观察期间无新增（有新增 → 需人工判断，错误必须逐条能解释）'
    Verdict   = $vLog
})

$failCount = @($verdicts | Where-Object Verdict -eq '不通过').Count
$noteCount = @($verdicts | Where-Object Verdict -eq '需人工判断').Count
$total = if ($isIncomplete) { 'INCOMPLETE' }
         elseif ($failCount -gt 0) { 'FAIL' }
         elseif ($noteCount -gt 0) { 'PASS-WITH-NOTES' }
         else { 'PASS' }

# 渲染 Markdown
$md = [System.Collections.Generic.List[string]]::new()
[void]$md.Add('# Soak 观察判定报告')
[void]$md.Add('')
[void]$md.Add("- 输入文件：$Csv")
[void]$md.Add("- 生成时间：$(FmtTs (Get-Date))")
[void]$md.Add("- 参数：IntervalSec=$IntervalSec 秒，Hours=$Hours，期望采样数=$expectedSamples")
[void]$md.Add('')
if ($isIncomplete) {
    [void]$md.Add("> ⚠️ **观察未完成**：已采 $n 行，低于期望采样数 $expectedSamples 的 80%（$([math]::Round($expectedSamples * $Thresholds.IncompleteRowFactor)) 行）。本报告只用于中途检查，总判定为 INCOMPLETE，不构成通过/不通过结论。")
    [void]$md.Add('')
}

[void]$md.Add('## 1. 采样概况')
[void]$md.Add('')
[void]$md.Add("- 数据行数：$n（时间戳无法解析：$badTs 行）")
[void]$md.Add("- 首个时间戳：$(FmtTs $firstTs)")
[void]$md.Add("- 末个时间戳：$(FmtTs $lastTs)")
[void]$md.Add("- 实际跨度：$(FmtN $spanHours 2) 小时")
[void]$md.Add("- 期望采样数：$expectedSamples（Hours×3600/IntervalSec = $(FmtI ($Hours * 3600)) / $IntervalSec）")
[void]$md.Add("- 缺失数：$(FmtI $missingCount)（按整段期望计算；观察未完成时该缺口属预期）")
if ($null -ne $maxGapSec) {
    [void]$md.Add("- 最大间隔：$(FmtI $maxGapSec) 秒（出现于 $(FmtTs $maxGapFrom) → $(FmtTs $maxGapTo)）")
} else {
    [void]$md.Add('- 最大间隔：—（有效时间戳不足，无法计算）')
}
[void]$md.Add("- 间隔 > $(FmtI ($IntervalSec * $Thresholds.GapFactor)) 秒（IntervalSec×1.35）的次数：$gapOverCount")
[void]$md.Add("- 时间戳非单调（倒退/相等）次数：$nonMonoCount")
[void]$md.Add('')

[void]$md.Add('## 2. 存活')
[void]$md.Add('')
[void]$md.Add("- AppAlive=False：$($falseSamples.Count) 条")
foreach ($f in $falseSamples) {
    [void]$md.Add("  - $(FmtTs $f.Ts)（原始值：$($f.AliveRaw)）")
}
[void]$md.Add("- PID 去重：$pidCount 个")
foreach ($g in $pidGroups) {
    $gts = @($g.Group | Where-Object { $null -ne $_.Ts })
    $gFirst = if ($gts.Count -ge 1) { FmtTs $gts[0].Ts } else { '—' }
    $gLast  = if ($gts.Count -ge 1) { FmtTs $gts[-1].Ts } else { '—' }
    [void]$md.Add("  - PID $($g.Name)：$($g.Count) 个采样，$gFirst → $gLast")
}
[void]$md.Add("- AppAlive 值无法识别：$unknownAliveCount 条；PID 为空：$emptyPidCount 条")
if ($pidCount -gt 1) {
    [void]$md.Add('> 注意：PID 去重 > 1，说明观察期间发生过进程重启。')
}
[void]$md.Add('')

[void]$md.Add('## 3. 内存（WorkingSetMB）')
[void]$md.Add('')
[void]$md.Add("- 首值：$(FmtN $ws.First 1) MB；末值：$(FmtN $ws.Last 1) MB")
[void]$md.Add("- 最大：$(FmtN $ws.Max 1) MB（$(FmtTs $ws.MaxTs)）；最小：$(FmtN $ws.Min 1) MB（$(FmtTs $ws.MinTs)）")
[void]$md.Add("- 后 1/3 采样线性斜率：$(FmtN $wsSlope 2) MB/h")
[void]$md.Add("- 全程相对增长：$(FmtPct $wsGrowthPct 1)")
if ($gcDrops.Count -gt 0) {
    $dropLines = @($gcDrops | Select-Object -First 5 | ForEach-Object { "$(FmtTs $_.Ts)（$(FmtN $_.From 1) → $(FmtN $_.To 1) MB）" }) -join '；'
    $moreNote = if ($gcDrops.Count -gt 5) { "（仅列前 5 处，共 $($gcDrops.Count) 处）" } else { '' }
    [void]$md.Add("- 备注：检测到 $($gcDrops.Count) 处相邻采样下跌 > $(FmtN $Thresholds.MemGcDropPct 0)%，按口径视为 GC 回落而非泄漏：$dropLines$moreNote")
} else {
    [void]$md.Add("- 备注：未检测到相邻采样下跌 > $(FmtN $Thresholds.MemGcDropPct 0)% 的情况（无 GC 大幅回落）")
}
[void]$md.Add('')

[void]$md.Add('## 4. 句柄 / 线程')
[void]$md.Add('')
[void]$md.Add("- 句柄：首 $(FmtI $hd.First) → 末 $(FmtI $hd.Last)，最大 $(FmtI $hd.Max)（$(FmtTs $hd.MaxTs)）；后 1/3 斜率 $(FmtN $hdSlope 2) 个/h；全程增量 $(FmtI $hdInc)")
[void]$md.Add("- 线程：首 $(FmtI $th.First) → 末 $(FmtI $th.Last)，最大 $(FmtI $th.Max)（$(FmtTs $th.MaxTs)）；后 1/3 斜率 $(FmtN $thSlope 2) 个/h；全程增量 $(FmtI $thInc)")
[void]$md.Add('')

[void]$md.Add('## 5. CPU')
[void]$md.Add('')
[void]$md.Add("- CpuSeconds：首 $(FmtN $cpuFirst 1) → 末 $(FmtN $cpuLast 1)，差值 $(FmtN $cpuDelta 1) 秒")
[void]$md.Add("- 实际跨度 $(FmtI $spanSec) 秒 → 平均占单核 $(FmtPct $cpuPct 2)")
[void]$md.Add('')

[void]$md.Add('## 6. 数据库')
[void]$md.Add('')
[void]$md.Add("- DbBytes：首 $(FmtI $dbFirst) → 末 $(FmtI $dbLast)，增长 $(FmtI $dbDelta) 字节（$(FmtMb $dbDelta 2) MB）")
[void]$md.Add("- 当前增长速率 $(FmtN $dbRateMbH 3) MB/h → 按此速率外推 30 天约 $(FmtN $dbExtrap30dMb 1) MB")
[void]$md.Add("- WalBytes 最大：$(FmtI $walMax) 字节（$(FmtMb $walMax 2) MB，$(FmtTs $walMaxTs)）；末 1/10（$k10 个采样）极差 $(FmtI $walTailRange) 字节，占最大 $(FmtPct $walRangePct 2)")
if ($null -eq $journalSummary) {
    [void]$md.Add('- JournalMode 去重：—（全为空）')
} else {
    [void]$md.Add("- JournalMode 去重：$journalSummary")
}
[void]$md.Add('')

[void]$md.Add('## 7. 数据链路')
[void]$md.Add('')
[void]$md.Add("- TelemetryRows：首 $(FmtI $telFirst) → 末 $(FmtI $telLast)，增量 $(FmtI $telDelta) 行")
[void]$md.Add("- 平均 $(FmtN $telRowsPerSec 3) 行/秒（期望 1 Hz）")
[void]$md.Add("- 达成率（增量 / 期望行数 $(FmtI $expectedTelRows)）：$(FmtPct $telAchPct 1)")
[void]$md.Add("- AlarmRows 末值：$(FmtI $alarmLast)")
[void]$md.Add("- LinkEvents：首 $(FmtI $linkFirst) → 末 $(FmtI $linkLast)，增量 $(FmtI $linkDelta)")
[void]$md.Add('')

[void]$md.Add('## 8. 日志')
[void]$md.Add('')
[void]$md.Add("- LogBytes：首 $(FmtI $lbFirst) → 末 $(FmtI $lbLast)")
[void]$md.Add("- LogErrors：首 $(FmtI $leFirst) → 末 $(FmtI $leLast)（新增 $(FmtI $leInc)）")
[void]$md.Add("- LogWarnings：首 $(FmtI $lwFirst) → 末 $(FmtI $lwLast)（新增 $(FmtI $lwInc)）")
if ($logDecreaseNote) {
    [void]$md.Add("- 备注：$logDecreaseNote")
}
[void]$md.Add('')

[void]$md.Add('## 9. 判定表')
[void]$md.Add('')
[void]$md.Add('| 项 | 实测 | 阈值 | 结论 |')
[void]$md.Add('|---|---|---|---|')
foreach ($v in $verdicts) {
    $conc = switch ($v.Verdict) {
        '通过' { '通过' }
        '不通过' { '**不通过**' }
        default { '**需人工判断**' }
    }
    [void]$md.Add("| $($v.Item) | $($v.Actual) | $($v.Threshold) | $conc |")
}
[void]$md.Add('')

[void]$md.Add('## 总判定')
[void]$md.Add('')
switch ($total) {
    'PASS' { [void]$md.Add('**PASS** —— 全部判定项通过。') }
    'FAIL' { [void]$md.Add("**FAIL** —— 有 $failCount 项「不通过」。") }
    'PASS-WITH-NOTES' { [void]$md.Add("**PASS-WITH-NOTES** —— 无「不通过」，但有 $noteCount 项「需人工判断」。") }
    default { [void]$md.Add("**INCOMPLETE** —— 观察未完成（已采 $n 行 / 期望 $expectedSamples 行，低于 80%），本报告只用于中途检查，不构成通过/不通过结论；判定明细中已有「不通过」$failCount 项、「需人工判断」$noteCount 项。") }
}

# 输出
$report = ($md -join "`r`n") + "`r`n"
if ($OutFile) {
    try {
        Set-Content -LiteralPath $OutFile -Value $report -Encoding utf8 -NoNewline
    } catch {
        Fail2 "无法写出报告文件 $OutFile：$($_.Exception.Message)"
    }
    Write-Output "报告已写入：$OutFile"
} else {
    Write-Output $report
}

if ($Strict -and $failCount -gt 0) { exit 1 }
exit 0
