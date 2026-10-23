using System.Runtime.InteropServices;
using System.Windows;

namespace NotifyHub.Services;

/// <summary>
/// 背景亮度取样（v0.2.6 需求 4「动态对比度检测」）：
/// 毛玻璃是半透明的，文字最终压在与下层内容的合成色上——
/// 深色毛玻璃压在白色网页上会变成中灰，白字就糊了。
/// 这里在显示前 BitBlt 一小块下层像素，算平均亮度，供主题选文字色。
///
/// 成本：一次 ~380×140 的屏幕抓取（每 6 像素采样一次），实测 &lt;2ms，
/// 只在卡片/岛出现时调一次，不参与动画循环。
/// </summary>
public static class BackdropProbe
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    /// <summary>
    /// 取屏幕矩形（DIP）背后的平均相对亮度（0=纯黑，1=纯白）。
    /// 失败返回 null（调用方回退到调色板的默认假设）。
    /// </summary>
    public static double? AverageLuminance(Rect dipRect)
    {
        try
        {
            var scale = 1.0;
            try
            {
                // 用主屏 DPI 换算物理像素
                var source = PresentationSource.FromVisual(Application.Current?.MainWindow);
                scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
            catch
            {
                // 拿不到就用 96 DPI
            }

            var x = (int)Math.Round(dipRect.X * scale);
            var y = (int)Math.Round(dipRect.Y * scale);
            var w = (int)Math.Round(dipRect.Width * scale);
            var h = (int)Math.Round(dipRect.Height * scale);

            if (w <= 0 || h <= 0)
            {
                return null;
            }

            return SampleLuminance(x, y, w, h);
        }
        catch
        {
            return null;
        }
    }

    private static double? SampleLuminance(int x, int y, int w, int h)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, w, h);

        try
        {
            SelectObject(memDc, bitmap);
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY | CAPTUREBLT))
            {
                return null;
            }

            var header = new BitmapInfoHeader
            {
                biSize = Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = w,
                biHeight = -h, // 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };

            var buffer = new byte[w * h * 4];
            if (GetDIBits(memDc, bitmap, 0, (uint)h, buffer, ref header, 0) == 0)
            {
                return null;
            }

            double sum = 0;
            var count = 0;

            // 每 6 像素采一个点，够用且快
            for (var py = 0; py < h; py += 6)
            {
                for (var px = 0; px < w; px += 6)
                {
                    var offset = (py * w + px) * 4; // BGRA
                    var b = buffer[offset];
                    var g = buffer[offset + 1];
                    var r = buffer[offset + 2];

                    // 快速感知亮度（Rec.601），够判深浅
                    sum += (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                    count++;
                }
            }

            return count == 0 ? null : sum / count;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hDc, IntPtr hBitmap, uint start, uint lines, byte[] bits,
        ref BitmapInfoHeader info, uint usage);
}
