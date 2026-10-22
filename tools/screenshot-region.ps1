# Capture an arbitrary screen region (CAPTUREBLT keeps layered WPF windows visible).
# Usage: screenshot-region.ps1 -X 400 -Y 0 -W 900 -H 200 -OutFile out.png
param(
    [int]$X = 0,
    [int]$Y = 0,
    [int]$W = 800,
    [int]$H = 200,
    [string]$OutFile = "$env:TEMP\region.png"
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

$screen = [Cap2]::GetDC([IntPtr]::Zero)
$mem = [Cap2]::CreateCompatibleDC($screen)
$hBmp = [Cap2]::CreateCompatibleBitmap($screen, $W, $H)
$old = [Cap2]::SelectObject($mem, $hBmp)
$ok = [Cap2]::BitBlt($mem, 0, 0, $W, $H, $screen, $X, $Y, 0x40CC0020)
if (-not $ok) { throw "BitBlt failed" }

$img = [System.Drawing.Image]::FromHbitmap($hBmp)
[Cap2]::SelectObject($mem, $old) | Out-Null
[Cap2]::DeleteObject($hBmp) | Out-Null
[Cap2]::DeleteDC($mem) | Out-Null
[Cap2]::ReleaseDC([IntPtr]::Zero, $screen) | Out-Null

$img.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$img.Dispose()
Write-Output "saved: $OutFile ($W x $H from $X,$Y)"
