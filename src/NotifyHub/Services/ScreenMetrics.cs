using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using NotifyHub.Interop;

namespace NotifyHub.Services;

/// <summary>
/// 屏幕度量（v0.2.8 需求 1 的地基）：把「主屏工作区」按**当前真实 DPI**换算成 DIP。
///
/// 为什么不再用 <c>SystemParameters.WorkArea</c>：
///   那是 WPF 在进程启动时按“系统 DPI”算好并缓存的值，一旦运行期间改了显示缩放
///   （100% ↔ 150% 很常见），它不会跟着更新。缓存成 1280×693 之后，
///   右下角通知会被算到 x=888（屏幕中间偏右）、左下角算到 y=575（屏幕左侧中间），
///   而左上角恰好还是 (8,8) 看起来正常——这正是历史 bug 的全部表现。
///
/// 现在的做法：每次定位都问一次 Win32 要**物理像素**工作区（GetMonitorInfo），
/// 再除以窗口自己的实时 DPI 缩放比。改缩放、换分辨率、多屏都自动正确。
/// </summary>
public static class ScreenMetrics
{
    /// <summary>主屏工作区（DIP，已扣除任务栏）。</summary>
    public static Rect PrimaryWorkAreaDip(Visual? visual = null)
    {
        var scale = ScaleOf(visual);
        var rect = PrimaryWorkAreaPhysical();
        return new Rect(
            rect.Left / scale,
            rect.Top / scale,
            Math.Max(1, (rect.Right - rect.Left) / scale),
            Math.Max(1, (rect.Bottom - rect.Top) / scale));
    }

    /// <summary>主屏整屏（DIP，含任务栏区域）。</summary>
    public static Rect PrimaryScreenDip(Visual? visual = null)
    {
        var scale = ScaleOf(visual);
        var rect = PrimaryScreenPhysical();
        return new Rect(
            rect.Left / scale,
            rect.Top / scale,
            Math.Max(1, (rect.Right - rect.Left) / scale),
            Math.Max(1, (rect.Bottom - rect.Top) / scale));
    }

    /// <summary>某个窗口所在显示器的工作区（DIP）。没有窗口时退回主屏。</summary>
    public static Rect WorkAreaDipFor(Visual? visual, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return PrimaryWorkAreaDip(visual);
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return PrimaryWorkAreaDip(visual);
        }

        var scale = ScaleOf(visual);
        return new Rect(
            info.rcWork.Left / scale,
            info.rcWork.Top / scale,
            Math.Max(1, (info.rcWork.Right - info.rcWork.Left) / scale),
            Math.Max(1, (info.rcWork.Bottom - info.rcWork.Top) / scale));
    }

    /// <summary>视觉对象当前的 DPI 缩放比（1.0 = 100%）。取不到时退回系统 DPI。</summary>
    public static double ScaleOf(Visual? visual)
    {
        if (visual is not null)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(visual);
                if (dpi.DpiScaleX > 0.1)
                {
                    return dpi.DpiScaleX;
                }
            }
            catch
            {
                // 未接入可视化树时 GetDpi 会抛，退回系统值
            }
        }

        try
        {
            var system = NativeMethods.GetDpiForSystem();
            if (system >= 48)
            {
                return system / 96.0;
            }
        }
        catch
        {
            // 极老系统没有该 API
        }

        return 1.0;
    }

    /// <summary>主屏工作区（物理像素）。</summary>
    private static NativeMethods.RECT PrimaryWorkAreaPhysical()
        => MonitorRect(workArea: true);

    /// <summary>主屏整屏（物理像素）。</summary>
    private static NativeMethods.RECT PrimaryScreenPhysical()
        => MonitorRect(workArea: false);

    private static NativeMethods.RECT MonitorRect(bool workArea)
    {
        var point = new NativeMethods.POINT { X = 0, Y = 0 };
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return workArea ? info.rcWork : info.rcMonitor;
        }

        // 兜底：虚拟屏
        return new NativeMethods.RECT
        {
            Left = 0,
            Top = 0,
            Right = (int)SystemParameters.PrimaryScreenWidth,
            Bottom = (int)SystemParameters.PrimaryScreenHeight,
        };
    }
}
