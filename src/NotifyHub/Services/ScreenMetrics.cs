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
    /// <summary>界面缩放的基准工作区高度（DIP，= 1080p 减任务栏）。</summary>
    public const double BaseWorkHeight = 1040;

    /// <summary>界面缩放的基准工作区宽度（DIP）。</summary>
    public const double BaseWorkWidth = 1920;

    /// <summary>界面缩放允许范围。</summary>
    public const double MinUiScale = 0.85;

    public const double MaxUiScale = 1.7;

    /// <summary>
    /// 界面缩放（v0.2.9 需求 1）：让"卡片/岛占屏比例"在各分辨率下保持一致。
    ///
    /// 为什么不能只靠 DIP：DIP 只把「物理像素 ÷ 系统缩放」折算成逻辑单位，
    /// 用户完全可以在 2560×1440 上把缩放设成 100%，这时 1 DIP = 1 物理像素，
    /// 一张 356 DIP 的卡片只占屏宽 13.9%（1080p@100% 是 18.5%）——看起来就是"通知变小了"。
    ///
    /// 这里以工作区**高度**为基准（卡片是纵向堆叠的，高度最能决定观感），
    /// 再用宽度做一次上限约束，避免 32:9 超宽屏把卡片放大到荒唐的尺寸。
    /// 实测比例：
    ///   1920×1040   → 1.00（1080p 100%，卡片占屏高 10.8%）
    ///   1707×933    → 0.90（2.5K 150%，占 10.7%）
    ///   2560×1400   → 1.35（2.5K 100%，占 10.8%）
    ///   3840×2100   → 1.70（4K 100%，被上限截住，占 9.0%）
    /// 也就是说无论缩放怎么设，观感比例基本恒定。
    /// </summary>
    public static double UiScale(Rect work)
    {
        if (work.Height < 1 || work.Width < 1)
        {
            return 1.0;
        }

        var byHeight = work.Height / BaseWorkHeight;
        var byWidth = work.Width / BaseWorkWidth;

        // 宽屏优先按高度算，但不要让"又宽又矮"的屏幕把卡片撑大
        var raw = Math.Min(byHeight, byWidth * 1.25);
        return Math.Clamp(raw, MinUiScale, MaxUiScale);
    }

    /// <summary>主屏当前的界面缩放。</summary>
    public static double UiScaleFor(Visual? visual = null) => UiScale(PrimaryWorkAreaDip(visual));

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
