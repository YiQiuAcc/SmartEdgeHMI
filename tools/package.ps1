#requires -Version 7.0
<#
.SYNOPSIS
    SmartEdgeHMI 一键打包: 测试 → 发布主程序与 Watchdog → 压缩 zip → 生成 SHA256。

.DESCRIPTION
    可在任意工作目录执行, 脚本内通过 $PSScriptRoot 推回仓库根目录。
    版本解析顺序: 显式 -Version → git describe --tags --abbrev=0 (去掉前缀 v) → 0.0.0-dev。
    幂等: 重复执行覆盖 dist/ 下同名产物, 不残留半成品 (先删旧 zip 与发布目录)。
    产物:
      dist/publish/<版本>/           发布目录 (主程序与 Watchdog 同目录)
      dist/SmartEdgeHMI-<版本>-<RID>.zip
      dist/SmartEdgeHMI-<版本>-<RID>.zip.sha256   (格式: <hash>  <文件名>)

.EXAMPLE
    pwsh tools/package.ps1 -Version 1.2.0

.EXAMPLE
    pwsh tools/package.ps1 -Version 1.2.0-dev -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

# 任意工作目录可执行: tools/ 的上一级即仓库根
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    # 版本解析: 显式 -Version → git describe --tags --abbrev=0 (去掉前缀 v) → 0.0.0-dev
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $tag = $null
        try { $tag = (& git describe --tags --abbrev=0) 2>$null } catch { $tag = $null }
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tag)) {
            $Version = $tag.Trim().TrimStart('v')
        }
        else {
            $Version = '0.0.0-dev'
        }
    }

    Write-Host "==> 版本: $Version (Configuration: $Configuration, Runtime: $Runtime)"

    $distDir    = Join-Path $root 'dist'
    $publishDir = Join-Path $distDir (Join-Path 'publish' $Version)
    $zipName    = "SmartEdgeHMI-$Version-$Runtime.zip"
    $zipPath    = Join-Path $distDir $zipName
    $shaPath    = "$zipPath.sha256"

    # 幂等: 先删旧 zip / 校验和 / 发布目录, 不残留半成品
    foreach ($stale in @($zipPath, $shaPath)) {
        if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }
    }
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    # [1/5] 测试 (可用 -SkipTests 跳过)
    if (-not $SkipTests) {
        Write-Host '==> [1/5] dotnet test'
        & dotnet test SmartEdgeHMI.sln -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "dotnet test 失败 (退出码 $LASTEXITCODE)" }
    }
    else {
        Write-Host '==> [1/5] dotnet test 已跳过 (-SkipTests)'
    }

    # [2/5] 发布主程序。
    # 注意: Watchdog (SmartEdgeHMI.Watchdog/Program.cs) 按 "自身同目录下 SmartEdgeHMI.exe"
    # 定位主程序 (AppContext.BaseDirectory 拼接文件名), 因此两个项目必须发布到同一目录。
    Write-Host '==> [2/5] dotnet publish (SmartEdgeHMI)'
    & dotnet publish SmartEdgeHMI/SmartEdgeHMI.csproj -c $Configuration -r $Runtime --self-contained true -p:Version=$Version -p:DebugType=none -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish SmartEdgeHMI 失败 (退出码 $LASTEXITCODE)" }

    # [3/5] 发布看门狗 (与主程序同一目录)
    Write-Host '==> [3/5] dotnet publish (SmartEdgeHMI.Watchdog)'
    & dotnet publish SmartEdgeHMI.Watchdog/SmartEdgeHMI.Watchdog.csproj -c $Configuration -r $Runtime --self-contained true -p:Version=$Version -p:DebugType=none -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish SmartEdgeHMI.Watchdog 失败 (退出码 $LASTEXITCODE)" }

    # [4/5] 打包: 发布目录内容压到 zip 根 (解压后直接可见 SmartEdgeHMI.exe)
    Write-Host "==> [4/5] Compress-Archive -> $zipName"
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force

    # [5/5] SHA256 写入 .sha256 (格式: <hash>  <文件名>)
    # -NoNewline + 显式 LF: Out-File 默认 CRLF 会让 GNU sha256sum -c 把 \r 当作文件名一部分而校验失败
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $zipName`n" | Out-File -FilePath $shaPath -Encoding ascii -NoNewline
    Write-Host "==> [5/5] SHA256 -> $(Split-Path -Path $shaPath -Leaf)"

    # 摘要
    $zipItem = Get-Item -LiteralPath $zipPath
    $sizeMB  = [math]::Round($zipItem.Length / 1MB, 2)
    Write-Host ''
    Write-Host '==================== 打包摘要 ===================='
    Write-Host "版本      : $Version"
    Write-Host "zip 路径  : $($zipItem.FullName)"
    Write-Host ("大小      : {0} MB ({1} 字节)" -f $sizeMB, $zipItem.Length)
    Write-Host "SHA256    : $hash"
    Write-Host '=================================================='
}
finally {
    Pop-Location
}
