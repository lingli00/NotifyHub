# List ALL top-level windows belonging to processes matching -Name (default NotifyHub).
param([string]$Name = 'NotifyHub')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinCheck2 {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static List<string> ForPids(HashSet<uint> pids) {
        var found = new List<string>();
        EnumWindows((h, _) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid)) return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            RECT r; GetWindowRect(h, out r);
            found.Add(string.Format("hwnd=0x{0:X} pid={1} vis={2} rect=({3},{4})-({5},{6}) class={7} title='{8}'",
                h.ToInt64(), pid, IsWindowVisible(h), r.L, r.T, r.R, r.B, c, t));
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

$pids = New-Object 'System.Collections.Generic.HashSet[uint32]'
Get-Process -Name $Name -ErrorAction SilentlyContinue | ForEach-Object { [void]$pids.Add([uint32]$_.Id) }
Write-Output "pids: $($pids -join ', ')"
foreach ($line in [WinCheck2]::ForPids($pids)) { Write-Output $line }
