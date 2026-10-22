# Screen capture with CAPTUREBLT so layered (WPF transparent) windows are included.
# Usage: powershell -ExecutionPolicy Bypass -File screenshot2.ps1 -OutFile shot.png
param(
    [string]$OutFile = "$env:TEMP\shot.png",
    [int]$CropW = 700,
    [int]$CropH = 640
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Cap {
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
$w = [Math]::Min($CropW, $b.Width)
$h = [Math]::Min($CropH, $b.Height)
$x0 = $b.Width - $w
$y0 = 0

$screen = [Cap]::GetDC([IntPtr]::Zero)
$mem = [Cap]::CreateCompatibleDC($screen)
$hBmp = [Cap]::CreateCompatibleBitmap($screen, $w, $h)
$old = [Cap]::SelectObject($mem, $hBmp)
# SRCCOPY(0x00CC0020) | CAPTUREBLT(0x40000000)
$ok = [Cap]::BitBlt($mem, 0, 0, $w, $h, $screen, $x0, $y0, 0x40CC0020)

if (-not $ok) { throw "BitBlt failed" }
# Convert to Image FIRST (it copies pixels), then release GDI handles
$img = [System.Drawing.Image]::FromHbitmap($hBmp)
[Cap]::SelectObject($mem, $old) | Out-Null
[Cap]::DeleteObject($hBmp) | Out-Null
[Cap]::DeleteDC($mem) | Out-Null
[Cap]::ReleaseDC([IntPtr]::Zero, $screen) | Out-Null

$img.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$img.Dispose()
Write-Output "saved: $OutFile ($w x $h from $x0,$y0)"
