# Identify the owner process of a window handle. ASCII only.
param(
    [Parameter(Mandatory = $true)][string]$Hwnd
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WOwn {
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
}
"@

$h = [IntPtr][Convert]::ToInt64($Hwnd.Replace("0x", ""), 16)
$owner = [uint32]0
[WOwn]::GetWindowThreadProcessId($h, [ref]$owner) | Out-Null
$p = Get-Process -Id $owner -ErrorAction SilentlyContinue
$sb = New-Object System.Text.StringBuilder 256
[WOwn]::GetWindowText($h, $sb, 256) | Out-Null
$cn = New-Object System.Text.StringBuilder 256
[WOwn]::GetClassName($h, $cn, 256) | Out-Null
Write-Output "pid=$owner name=$($p.ProcessName) title=$($sb) class=$($cn)"
