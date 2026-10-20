# 打版本 + 发布 Release 的本地脚本
#
# 用途：给当前代码打一个 vX.Y.Z 的 tag，并（可选）在本地先跑一遍发布、压缩，方便检查产物；
# 真正的 GitHub Release 由 .github/workflows/release.yml 在 push tag 后自动创建。
#
# 用法：
#   pwsh -File scripts/release.ps1 -Version 2.2.0            # 打 tag（不推送时用 -NoPush）
#   pwsh -File scripts/release.ps1 -Version 2.2.0 -Push      # 打 tag 并推送到 origin（触发 CI 发布）
#   pwsh -File scripts/release.ps1 -Version 2.2.0 -LocalOnly # 只在本地构建 zip，不打 tag
#
# 已安装 gh CLI 且已登录时，加 -Gh 可直接创建 Release（不依赖 CI）。

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$Push,
    [switch]$LocalOnly,
    [switch]$Gh
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$semver = $Version.TrimStart("v")
$tag = "v$semver"
$outDir = Join-Path $root "publish"
$zip = Join-Path $root "TarkovTacticalMap-$tag-win-x64.zip"

Write-Host "== 塔塔战术平板 $tag ==" -ForegroundColor Cyan

# 1) 构建自包含单文件
& (Join-Path $PSScriptRoot "publish.ps1") -Output "publish" -Version $semver
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

# 2) 压缩
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $outDir "TarkovTacticalMap.exe") -DestinationPath $zip -Force
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "已生成 $zip（$size MB）" -ForegroundColor Green

if ($LocalOnly) {
    Write-Host "仅本地构建完成（未打 tag）。" -ForegroundColor Yellow
    exit 0
}

# 3) 打 tag
Push-Location $root
try {
    $existing = git tag --list $tag
    if ($existing) {
        Write-Host "tag $tag 已存在，跳过创建。" -ForegroundColor Yellow
    }
    else {
        git tag -a $tag -m "塔塔战术平板 $tag"
        Write-Host "已创建 tag $tag" -ForegroundColor Green
    }

    if ($Push) {
        $hasRemote = git remote get-url origin 2>$null
        if (-not $hasRemote) { throw "没有配置 origin remote，无法推送。请先 git remote add origin <url>" }
        git push origin $tag
        Write-Host "已推送 tag，GitHub Actions 将自动创建 Release。" -ForegroundColor Green
    }
    else {
        Write-Host "未推送（加 -Push 才会触发 CI 发布）。" -ForegroundColor Yellow
    }

    # 4) 可选：直接用 gh 创建 Release
    if ($Gh) {
        $gh = Get-Command gh -ErrorAction SilentlyContinue
        if (-not $gh) { throw "未安装 gh CLI，无法直接创建 Release" }
        $notes = "自包含单文件可执行程序（win-x64），队友无需安装 .NET 运行时。"
        gh release create $tag (Join-Path $outDir "TarkovTacticalMap.exe") $zip `
            --title "塔塔战术平板 $tag" --generate-notes --notes $notes
    }
}
finally {
    Pop-Location
}
