using System.Runtime.InteropServices;
using NotifyHub.Interop;

namespace NotifyHub.Services;

/// <summary>
/// 全屏检测：前台窗口完整覆盖某台显示器且没有标题栏 → 判定为全屏
/// （观影类全屏视频 / 游戏 / F11）。灵动岛据此整套自动隐藏，退出后恢复。
/// 轮询 1.5 秒，纯 Win32，不触碰 UIA。
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private readonly LogService _log;
    private readonly Timer _timer;
    private bool _last;
    private bool _disposed;

    /// <summary>全屏状态变化（线程池线程回调）。</summary>
    public event Action<bool>? FullscreenChanged;

    public FullscreenWatcher(LogService log)
    {
        _log = log;
        _timer = new Timer(_ => Check(), null, 1500, 1500);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    private void Check()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var fullscreen = Detect();

            if (fullscreen == _last)
            {
                return;
            }

            _last = fullscreen;
            _log.Info("Fullscreen", fullscreen ? "检测到全屏应用，灵动岛自动隐藏" : "已退出全屏，恢复灵动岛");
            FullscreenChanged?.Invoke(fullscreen);
        }
        catch
        {
            // 检测失败保持原状态即可
        }
    }

    /// <summary>前台窗口是否处于全屏（铺满整台显示器且无标题栏）。</summary>
    public static bool Detect()
    {
        var hwnd = NativeMethods.GetForegroundWindow();

        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

        if (pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        var cls = NativeMethods.GetClassName(hwnd);

        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);

        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        // 窗口矩形覆盖整个显示器（允许 2px 误差）
        var covers = rect.Left <= info.rcMonitor.Left + 2
                     && rect.Top <= info.rcMonitor.Top + 2
                     && rect.Right >= info.rcMonitor.Right - 2
                     && rect.Bottom >= info.rcMonitor.Bottom - 2;

        if (!covers)
        {
            return false;
        }

        // 有标题栏的窗口铺满显示器 → 是“自动隐藏任务栏 + 最大化”，不算观影全屏
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
        return (style & NativeMethods.WS_CAPTION) == 0;
    }
}
