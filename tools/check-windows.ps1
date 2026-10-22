# Check windows by title part from outside the process.
param(
    [string]$Title = 'NotifyHub'
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinCheck {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int val, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static List<string> Find(string titlePart) {
        var found = new List<string>();
        EnumWindows((h, _) => {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            var title = sb.ToString();
            if (title.IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            uint pid; GetWindowThreadProcessId(h, out pid);
            RECT r; GetWindowRect(h, out r);
            int cloaked; DwmGetWindowAttribute(h, 14, out cloaked, 4);
            found.Add(string.Format("hwnd=0x{0:X} pid={1} vis={2} cloaked={3} rect=({4},{5})-({6},{7}) class={8} title={9}",
                h.ToInt64(), pid, IsWindowVisible(h), cloaked, r.L, r.T, r.R, r.B, cls, title));
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

foreach ($line in [WinCheck]::Find($Title)) { Write-Output $line }
