$ErrorActionPreference = "Stop"
$src = @"
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public class WinPlace {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, char[] b, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT {
        public int length, flags, showCmd;
        public RECT minPos, maxPos, normalPos;
    }
    delegate bool EnumProc(IntPtr h, IntPtr p);
    public static List<string> List(int target) {
        var outp = new List<string>();
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != (uint)target) return true;
            var txt = new char[256]; GetWindowTextW(h, txt, 256);
            var name = new string(txt).TrimEnd('\0');
            if (name.IndexOf("settings", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("NotifyHub", StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r; GetWindowRect(h, out r);
            var wp = new WINDOWPLACEMENT();
            wp.length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINDOWPLACEMENT));
            GetWindowPlacement(h, ref wp);
            outp.Add(string.Format("hwnd=0x{0:X} vis={1} text={2} rect={3},{4},{5}x{6} showCmd={7} normal={8},{9},{10}x{11}",
                h.ToInt64(), IsWindowVisible(h), name, r.L, r.T, r.R-r.L, r.B-r.T, wp.showCmd,
                wp.normalPos.L, wp.normalPos.T, wp.normalPos.R-wp.normalPos.L, wp.normalPos.B-wp.normalPos.T));
            return true;
        }, IntPtr.Zero);
        return outp;
    }
}
"@
Add-Type -TypeDefinition $src
$targetPid = [int]$args[0]
$rows = [WinPlace]::List($targetPid)
$rows.Sort()
foreach ($r in $rows) { Write-Output $r }
