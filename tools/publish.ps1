# NotifyHub 发布脚本：产出自包含单文件 exe
# 用法: powershell -ExecutionPolicy Bypass -File tools\publish.ps1
# 输出: <仓库根>\dist\NotifyHub.exe
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\NotifyHub\NotifyHub.csproj'
$outDir = Join-Path $root 'dist'

# 依次找 PATH 里的 dotnet、用户目录安装、系统默认安装
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    foreach ($candidate in @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    )) {
        if (Test-Path $candidate) { $dotnet = $candidate; break }
    }
}
if (-not $dotnet) { throw '未找到 dotnet.exe，请先安装 .NET 8 SDK' }

Write-Output "dotnet: $dotnet"
Write-Output "project: $project"

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outDir

if ($LASTEXITCODE -ne 0) { throw "publish 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $outDir 'NotifyHub.exe'
$info = Get-Item $exe
Write-Output ("发布完成: {0} ({1:N1} MB)" -f $info.FullName, ($info.Length / 1MB))
