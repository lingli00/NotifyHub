# Bring a top-level window (by exact/partial title) to the foreground. ASCII only.
param(
    [Parameter(Mandatory = $true)][string]$Title
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Front {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
"@

$found = [IntPtr]::Zero
$cb = [Front+EnumProc]{
    param($h, $l)
    if ([Front]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256
        [Front]::GetWindowText($h, $sb, 256) | Out-Null
        if ($sb.ToString().Contains($Title)) { $script:found = $h; return $false }
    }
    return $true
}
[Front]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

if ($found -eq [IntPtr]::Zero) { Write-Output "NOT FOUND: $Title"; exit 1 }

[Front]::ShowWindow($found, 9) | Out-Null   # SW_RESTORE
[Front]::SetForegroundWindow($found) | Out-Null
Write-Output ("FRONT: 0x{0:X} {1}" -f $found.ToInt64(), $Title)
