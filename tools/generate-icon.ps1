# 生成 NotifyHub.ico（多尺寸 PNG 条目）。产物已入库，仅在需要改图标时重跑。
# 用法: powershell -ExecutionPolicy Bypass -File tools\generate-icon.ps1
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\NotifyHub\Assets\NotifyHub.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = New-Object System.Collections.ArrayList

function New-RoundedPath([single]$w, [single]$h, [single]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $h - $d, $d, $d, 0, 90)
    $path.AddArc(0, $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    # 逗号包裹，避免 PowerShell 把 GraphicsPath 当集合展开导致返回空
    return ,$path
}

foreach ($size in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # 背景：蓝色圆角方块（渐变）
    $bgPath = New-RoundedPath $size $size ([single]($size * 0.22))
    $p1 = [System.Drawing.PointF]::new(0, 0)
    $p2 = [System.Drawing.PointF]::new([single]$size, [single]$size)
    $c1 = [System.Drawing.Color]::FromArgb(255, 59, 130, 246)
    $c2 = [System.Drawing.Color]::FromArgb(255, 29, 78, 216)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($p1, $p2, $c1, $c2)
    $g.FillPath($brush, $bgPath)

    # 铃铛：在 32x32 设计坐标系里绘制，再整体缩放
    $scale = $size / 32.0
    $state = $g.Save()
    $g.ScaleTransform($scale, $scale)

    $bell = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $bell.AddBezier(16, 4, 14.2, 4, 13, 5.3, 13, 7.2)
    $bell.AddBezier(13, 7.2, 13, 9.4, 9.6, 11.6, 9.1, 15.6)
    $bell.AddLine(9.1, 15.6, 8.4, 19.2)
    $bell.AddBezier(8.4, 19.2, 8.3, 19.9, 8.8, 20.5, 9.5, 20.5)
    $bell.AddLine(9.5, 20.5, 22.5, 20.5)
    $bell.AddBezier(22.5, 20.5, 23.2, 20.5, 23.7, 19.9, 23.6, 19.2)
    $bell.AddLine(23.6, 19.2, 22.9, 15.6)
    $bell.AddBezier(22.9, 15.6, 22.4, 11.6, 19, 9.4, 19, 7.2)
    $bell.AddBezier(19, 7.2, 19, 5.3, 17.8, 4, 16, 4)
    $bell.CloseFigure()

    $white = [System.Drawing.Brushes]::White
    $g.FillPath($white, $bell)
    $g.FillEllipse($white, [single]13.9, [single]21.1, [single]4.2, [single]4.2)

    $g.Restore($state)
    $g.Dispose()

    $memory = [System.IO.MemoryStream]::new()
    $bmp.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [void]$pngs.Add($memory.ToArray())
    $memory.Dispose()
}

# 组装 ICO 容器（Vista+ 支持 PNG 条目）
$outStream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($outStream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $bytes = $pngs[$i]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$dim)
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $bytes.Length
}

foreach ($bytes in $pngs) { $writer.Write($bytes) }
$writer.Flush()

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$target = Join-Path $dir 'NotifyHub.ico'
[System.IO.File]::WriteAllBytes($target, $outStream.ToArray())
$writer.Dispose()
$outStream.Dispose()

$info = Get-Item -LiteralPath $target
Write-Output ("生成完成: {0} ({1:N0} 字节, {2} 个尺寸)" -f $info.FullName, $info.Length, $sizes.Count)

# 导出 256px 预览，便于人工确认图形
$preview = Join-Path $dir 'icon-preview-256.png'
[System.IO.File]::WriteAllBytes($preview, $pngs[$pngs.Count - 1])
Write-Output "预览图: $preview"
