using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace NotifyHub.UI;

/// <summary>
/// 演示模式专用取证：用 Win32 读取自身窗口的真实位置/可见性/是否被 DWM 遮蔽，
/// 并用 BitBlt(CAPTUREBLT) 从屏幕抓一张包含全部通知卡片的图，便于离线核对。
/// </summary>
public static class DemoCapture
{
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

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int x1, int y1, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    private const int DwmwaCloaked = 14;
    private const int RopSrcCopyOrCaptureBlt = 0x40CC0020;

    /// <summary>汇总所有通知窗口的 Win32 真实状态。</summary>
    public static string Describe(IReadOnlyList<NotificationWindow> windows)
    {
        var lines = new List<string>();

        foreach (var window in windows)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                lines.Add("hwnd=NULL");
                continue;
            }

            GetWindowRect(handle, out var rect);
            DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, 4);

            lines.Add(
                $"hwnd=0x{handle.ToInt64():X} win32vis={IsWindowVisible(handle)} cloaked={cloaked} " +
                $"px=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) wpf=({window.Left:F0},{window.Top:F0})");
        }

        return string.Join(" | ", lines);
    }

    /// <summary>从屏幕抓取覆盖所有通知窗口的区域（含 CAPTUREBLT，分层窗口可见）。</summary>
    public static string? CaptureRegion(IReadOnlyList<NotificationWindow> windows, string filePath)
    {
        if (windows.Count == 0)
        {
            return null;
        }

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        foreach (var window in windows)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
            {
                continue;
            }

            left = Math.Min(left, rect.Left);
            top = Math.Min(top, rect.Top);
            right = Math.Max(right, rect.Right);
            bottom = Math.Max(bottom, rect.Bottom);
        }

        if (left >= right || top >= bottom)
        {
            return null;
        }

        left -= 40;
        top -= 40;
        right += 40;
        bottom += 40;
        var width = right - left;
        var height = bottom - top;

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        var ok = BitBlt(memory, 0, 0, width, height, screen, left, top, RopSrcCopyOrCaptureBlt);

        BitmapSource? result = null;
        if (ok)
        {
            result = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            result.Freeze();
        }

        SelectObject(memory, old);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);

        if (result is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using var stream = File.Create(filePath);
            encoder.Save(stream);
        }
        catch
        {
            return null;
        }

        return $"{filePath} ({width}x{height} from {left},{top})";
    }
}
