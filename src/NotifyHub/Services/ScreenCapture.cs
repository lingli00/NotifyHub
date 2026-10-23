using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotifyHub.Services;

/// <summary>
/// 整屏截图（v0.2.10 需求 6 的"截图"部分）。
///
/// 为什么不用 <c>Graphics.CopyFromScreen</c>：.NET 8 上没有那个 4 参重载，而且默认抓不到
/// 分层窗口（我们的通知卡/灵动岛都是 AllowsTransparency 分层窗，会拍成透明洞）。
/// BitBlt + CAPTUREBLT 才抓得到。
/// </summary>
public static class ScreenCapture
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    /// <summary>抓整个虚拟桌面并保存为 PNG；失败返回 null。</summary>
    public static string? CaptureToPng(string path, bool virtualScreen = true)
    {
        try
        {
            var x = virtualScreen ? GetSystemMetrics(SM_XVIRTUALSCREEN) : 0;
            var y = virtualScreen ? GetSystemMetrics(SM_YVIRTUALSCREEN) : 0;
            var w = virtualScreen ? GetSystemMetrics(SM_CXVIRTUALSCREEN) : GetSystemMetrics(SM_CXSCREEN);
            var h = virtualScreen ? GetSystemMetrics(SM_CYVIRTUALSCREEN) : GetSystemMetrics(SM_CYSCREEN);

            if (w <= 0 || h <= 0)
            {
                return null;
            }

            var source = GetDC(IntPtr.Zero);
            if (source == IntPtr.Zero)
            {
                return null;
            }

            var mem = CreateCompatibleDC(source);
            var bitmap = CreateCompatibleBitmap(source, w, h);

            try
            {
                SelectObject(mem, bitmap);
                if (!BitBlt(mem, 0, 0, w, h, source, x, y, SRCCOPY | CAPTUREBLT))
                {
                    return null;
                }

                var image = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = File.Create(path);
                encoder.Save(stream);
                return path;
            }
            finally
            {
                DeleteObject(bitmap);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, source);
            }
        }
        catch (Exception ex)
        {
            LogService.Shared.Warn("Snapshot", "整屏截图失败", ex.Message);
            return null;
        }
    }

    /// <summary>主屏尺寸（物理像素，诊断用）。</summary>
    public static (int Width, int Height) PrimaryScreenSize
        => (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
