# Full-screen capture with CAPTUREBLT (layered WPF windows included). ASCII only.
param(
    [string]$OutFile = "$env:TEMP\full.png"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Cap2 {
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
}
"@

Add-Type -AssemblyName System.Windows.Forms
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$w = $b.Width
$h = $b.Height

$screen = [Cap2]::GetDC([IntPtr]::Zero)
$mem = [Cap2]::CreateCompatibleDC($screen)
$bmp = [Cap2]::CreateCompatibleBitmap($screen, $w, $h)
$old = [Cap2]::SelectObject($mem, $bmp)
# SRCCOPY | CAPTUREBLT
$ok = [Cap2]::BitBlt($mem, 0, 0, $w, $h, $screen, 0, 0, 0x40CC0020)
$img = [System.Drawing.Image]::FromHbitmap($bmp)
[Cap2]::SelectObject($mem, $old) | Out-Null
[Cap2]::DeleteObject($bmp) | Out-Null
[Cap2]::DeleteDC($mem) | Out-Null
[Cap2]::ReleaseDC([IntPtr]::Zero, $screen) | Out-Null

if (-not $ok) { $img.Dispose(); throw "BitBlt failed" }

$img.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$img.Dispose()
Write-Output "saved $OutFile ${w}x${h}"
