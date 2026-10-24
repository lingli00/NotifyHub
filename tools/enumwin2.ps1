$ErrorActionPreference = "Continue"
$targetPid = 1060
$src = @"
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public class WinEnum2 {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, char[] b, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, char[] b, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    delegate bool EnumProc(IntPtr h, IntPtr p);
    public static List<string> List(int target) {
        var outp = new List<string>();
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != (uint)target) return true;
            var cls = new char[256]; GetClassNameW(h, cls, 256);
            var txt = new char[256]; GetWindowTextW(h, txt, 256);
            RECT r; GetWindowRect(h, out r);
            outp.Add(string.Format("hwnd=0x{0:X} vis={1} cls={2} text={3} rect={4},{5},{6}x{7}",
                h.ToInt64(), IsWindowVisible(h), new string(cls).TrimEnd('\0'),
                new string(txt).TrimEnd('\0'), r.L, r.T, r.R-r.L, r.B-r.T));
            return true;
        }, IntPtr.Zero);
        return outp;
    }
}
"@
try {
    Add-Type -TypeDefinition $src -ErrorAction Stop
    Write-Output "ADDTYPE OK"
} catch {
    Write-Output ("ADDTYPE FAIL: " + $_.Exception.Message)
}
try {
    $rows = [WinEnum2]::List($targetPid)
    Write-Output ("ROWS " + $rows.Count)
    $rows.Sort()
    foreach ($r in $rows) { Write-Output $r }
} catch {
    Write-Output ("LIST FAIL: " + $_.Exception.Message)
}
