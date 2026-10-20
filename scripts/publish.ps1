# 塔塔战术平板 · 自包含发布脚本
#
# 用法：
#   pwsh -File scripts/publish.ps1                 # 默认 win-x64 单文件自包含
#   pwsh -File scripts/publish.ps1 -Output dist    # 指定输出目录
#   pwsh -File scripts/publish.ps1 -FrameworkDependent  # 框架依赖（体积小，需装 .NET 8 桌面运行时）
#
# 产物：<Output>/TarkovTacticalMap.exe，队友无需安装任何运行时即可运行。

param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Output = "publish",
    [string]$Version = "",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/TarkovTacticalMap.App/TarkovTacticalMap.App.csproj"
$outDir = Join-Path $root $Output

Write-Host "== 发布 塔塔战术平板 ==" -ForegroundColor Cyan
Write-Host "项目   : $project"
Write-Host "配置   : $Configuration / $Runtime"
Write-Host "输出   : $outDir"
Write-Host "版本   : $(if ($Version) { $Version } else { '（使用 csproj 默认值）' })"
Write-Host "自包含 : $(-not $FrameworkDependent)"

$args = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $outDir,
    "-p:DebugType=None",
    "--nologo"
)

if ($Version) {
    $args += @("-p:Version=$($Version.TrimStart('v'))")
}

if ($FrameworkDependent) {
    $args += @("--self-contained", "false")
}
else {
    $args += @(
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true"
    )
}

dotnet @args
if ($LASTEXITCODE -ne 0) { throw "发布失败（dotnet exit $LASTEXITCODE）" }

$exe = Join-Path $outDir "TarkovTacticalMap.exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "发布完成：$exe （$size MB）" -ForegroundColor Green
    Write-Host "提示：首次以房主身份监听端口时，Windows 可能弹出防火墙询问，请允许 TCP 入站。" -ForegroundColor Yellow
}
else {
    Write-Host "发布完成：$outDir" -ForegroundColor Green
}
