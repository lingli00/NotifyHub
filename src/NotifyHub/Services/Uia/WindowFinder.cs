using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NotifyHub.Services.Uia;

/// <summary>
/// 按“进程名 + 窗口类名/标题”定位微信 / QQ 主窗口。
/// 用 EnumWindows 纯 Win32 枚举（快、且不受桌面上其他无响应窗口的 UIA 30 秒超时拖累），
/// 不做任何 UIA 调用；UIA 只留给 SessionListReader 读具体窗口内容。
/// </summary>
public static class WindowFinder
{
    public sealed record WindowInfo(IntPtr Handle, string ClassName, string Title, int ProcessId, double Area, bool Minimized)
    {
        public string Describe() =>
            $"hwnd=0x{Handle.ToInt64():X} pid={ProcessId} class={ClassName} title=\"{Title}\" area={Area:F0}{(Minimized ? " [最小化]" : string.Empty)}";
    }

    /// <summary>排除消息窗口 / 输入法 / 托盘通知窗这类“伪主窗口”。</summary>
    private static readonly string[] ExcludedClassParts =
    {
        "IME", "MSCTFIME", "MessageWindow", "TrayIcon", "SystemMessage", "MSCTF", "Crypto",
    };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    /// <summary>枚举指定进程的所有顶层窗口（含诊断信息，含不可见/最小化窗口）。</summary>
    public static List<WindowInfo> EnumerateWindows(string[] processNames)
    {
        var result = new List<WindowInfo>();
        var processIds = GetProcessIds(processNames);
        if (processIds.Count == 0)
        {
            return result;
        }

        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(hWnd, out var pid);
                    if (!processIds.Contains(pid))
                    {
                        return true;
                    }

                    var className = Interop.NativeMethods.GetClassName(hWnd);
                    var title = Interop.NativeMethods.GetWindowText(hWnd);

                    if (ExcludedClassParts.Any(p => className.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    var area = 0.0;
                    var minimized = false;
                    if (GetWindowRect(hWnd, out var rect))
                    {
                        // 最小化窗口的 rect 是 (-32000,-32000)，面积无意义
                        minimized = rect.Left <= -30000 || rect.Top <= -30000;
                        area = minimized ? 0 : Math.Abs((double)(rect.Right - rect.Left) * (rect.Bottom - rect.Top));
                    }

                    result.Add(new WindowInfo(hWnd, className, title, pid, area, minimized));
                }
                catch
                {
                    // 单个窗口失败不影响整体
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败返回已收集结果
        }

        return result;
    }

    /// <summary>
    /// 找主窗口：类名命中 &gt; 标题命中 &gt; 面积。
    /// 面积封顶，避免最小化的主窗口（面积 0）输给面积虚高的托盘窗口；
    /// 最小化窗口保留候选，点击通知时还需要还原它。
    /// </summary>
    public static IntPtr FindMainWindow(string[] processNames, string[] preferredClasses)
    {
        var windows = EnumerateWindows(processNames);
        if (windows.Count == 0)
        {
            return IntPtr.Zero;
        }

        WindowInfo? best = null;
        var bestScore = double.MinValue;

        foreach (var window in windows)
        {
            var score = Math.Min(window.Area, 500_000);

            if (preferredClasses.Any(c => string.Equals(c, window.ClassName, StringComparison.OrdinalIgnoreCase))
                || window.ClassName.StartsWith("WeChatMainWnd", StringComparison.OrdinalIgnoreCase))
            {
                score += 1_000_000_000;
            }

            // 标题命中也能加分，用于类名在老/新版本间变化的情况
            if (window.Title.Contains("微信", StringComparison.OrdinalIgnoreCase)
                || window.Title.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
                || window.Title.Contains("QQ", StringComparison.OrdinalIgnoreCase))
            {
                score += 1_000_000;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = window;
            }
        }

        return best?.Handle ?? IntPtr.Zero;
    }

    public static string GetWindowTitle(IntPtr hwnd) => hwnd == IntPtr.Zero ? string.Empty : Interop.NativeMethods.GetWindowText(hwnd);

    public static string GetWindowClass(IntPtr hwnd) => hwnd == IntPtr.Zero ? string.Empty : Interop.NativeMethods.GetClassName(hwnd);

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && Interop.NativeMethods.IsWindow(hwnd);

    public static HashSet<int> GetProcessIds(string[] processNames)
    {
        var ids = new HashSet<int>();
        foreach (var name in processNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    ids.Add(process.Id);
                    process.Dispose();
                }
            }
            catch
            {
                // 进程枚举失败忽略
            }
        }

        return ids;
    }
}
