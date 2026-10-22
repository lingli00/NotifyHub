using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotifyHub.UI;

/// <summary>
/// 托盘图标：不依赖外部资源文件，直接用 WPF 矢量绘制，
/// 避免单文件发布后 ico 路径找不到的问题（暂停时变灰，便于一眼看出状态）。
/// </summary>
public static class IconFactory
{
    private const string BellPath =
        "M 16,4 C 14.2,4 13,5.3 13,7.2 C 13,9.4 9.6,11.6 9.1,15.6 L 8.4,19.2 " +
        "C 8.3,19.9 8.8,20.5 9.5,20.5 L 22.5,20.5 C 23.2,20.5 23.7,19.9 23.6,19.2 " +
        "L 22.9,15.6 C 22.4,11.6 19,9.4 19,7.2 C 19,5.3 17.8,4 16,4 Z";

    public static ImageSource Create(double size, bool paused = false)
    {
        var background = paused
            ? Color.FromRgb(0x8A, 0x8A, 0x8A)
            : Color.FromRgb(0x25, 0x63, 0xEB);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(
                new SolidColorBrush(background),
                null,
                new Rect(0, 0, size, size),
                size * 0.22,
                size * 0.22);

            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));

            var bell = Geometry.Parse(BellPath);
            bell.Freeze();
            dc.DrawGeometry(Brushes.White, null, bell);
            dc.DrawEllipse(Brushes.White, null, new Point(16, 23.2), 2.1, 2.1);

            dc.Pop();
        }

        var pixels = Math.Max(16, (int)Math.Ceiling(size * 2));
        var bitmap = new RenderTargetBitmap(pixels, pixels, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
