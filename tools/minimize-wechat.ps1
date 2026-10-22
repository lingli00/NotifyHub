# Re-minimize the WeChat main window (leave user's desktop as we found it).
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class MinWeChat {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
"@
$done = $false
$cb = [MinWeChat+EnumProc]{
    param($h, $l)
    $c = New-Object System.Text.StringBuilder 256
    [void][MinWeChat]::GetClassName($h, $c, 256)
    $t = New-Object System.Text.StringBuilder 256
    [void][MinWeChat]::GetWindowText($h, $t, 256)
    if ($c.ToString() -eq 'Qt51514QWindowIcon' -and $t.ToString() -eq '微信') {
        [void][MinWeChat]::ShowWindow($h, 6)  # SW_MINIMIZE
        Write-Output "minimized: 0x$($h.ToString('X'))"
        $script:done = $true
    }
    return $true
}
[void][MinWeChat]::EnumWindows($cb, [IntPtr]::Zero)
if (-not $done) { Write-Output "WeChat window not found" }
