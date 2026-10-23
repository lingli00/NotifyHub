using System.Windows;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>通知四角（与 <c>AppConfig.NotificationCorner</c> 的字符串一一对应）。</summary>
public enum NotifyCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// 通知卡片的几何与四角定位（v0.2.8 需求 1，纯函数、无窗口依赖，便于自检覆盖）。
///
/// 历史 bug 有两个独立原因：
///   1) 用缓存的 <c>SystemParameters.WorkArea</c>：显示缩放从 150% 变回 100% 后，
///      工作区仍是 1280×693（DIP），于是右下角算到 x=888（屏幕中间偏右）、
///      左下角算到 y=575（屏幕左侧中间）。现在由 <c>ScreenMetrics</c> 实时取。
///   2) 混用了「窗口高度」与「卡片本体高度」：窗口 140 里有上下各 14 的阴影留白，
///      卡片本体是 112。底部角按 110 算锚点，卡片实际底边就压到任务栏下面 8px。
///
/// v0.2.9 需求 1：全部几何支持 <c>scale</c>（由 <see cref="ScreenMetrics.UiScale"/> 按
/// 工作区尺寸算出）。基准值仍是 1080p 的 356×112 / 边距 8 / 步进 120，
/// 在其他分辨率上按同一比例放大缩小，于是「卡片占屏比例」在各分辨率下保持一致，
/// 不会出现 2.5K 上卡片小得看不清、或 4K 上留白巨大的畸形观感。
/// </summary>
public static class NotificationLayout
{
    /// <summary>卡片本体宽度（DIP，基准 1080p）。</summary>
    public const double CardBodyWidth = 356;

    /// <summary>卡片本体高度（DIP，基准 1080p）。</summary>
    public const double CardBodyHeight = 112;

    /// <summary>窗口四周透明阴影留白（DIP，不随缩放变化——它只是阴影空间）。</summary>
    public const double ShadowPad = 14;

    /// <summary>窗口宽度（基准值，未缩放）。</summary>
    public const double WindowWidth = CardBodyWidth + (ShadowPad * 2);

    /// <summary>窗口高度（基准值，未缩放）。</summary>
    public const double WindowHeight = CardBodyHeight + (ShadowPad * 2);

    /// <summary>卡片本体与工作区边缘的视觉间距（DIP，基准 1080p）。</summary>
    public const double VisualMargin = 8;

    /// <summary>相邻两张卡片本体之间的间距（DIP，基准 1080p）。</summary>
    public const double Gap = 8;

    /// <summary>纵向堆叠步进（基准值）= 本体高 + 间距。</summary>
    public const double StackStep = CardBodyHeight + Gap;

    // ---------------- 缩放后的几何 ----------------

    /// <summary>缩放后的卡片本体宽度。</summary>
    public static double CardWidth(double scale) => CardBodyWidth * Math.Max(0.1, scale);

    /// <summary>缩放后的卡片本体高度。</summary>
    public static double CardHeight(double scale) => CardBodyHeight * Math.Max(0.1, scale);

    /// <summary>缩放后的窗口宽度（阴影留白不缩放）。</summary>
    public static double WindowWidthFor(double scale) => CardWidth(scale) + (ShadowPad * 2);

    /// <summary>缩放后的窗口高度（阴影留白不缩放）。</summary>
    public static double WindowHeightFor(double scale) => CardHeight(scale) + (ShadowPad * 2);

    /// <summary>缩放后的堆叠步进。</summary>
    public static double StackStepFor(double scale) => CardHeight(scale) + (Gap * Math.Max(0.1, scale));

    public static NotifyCorner Parse(string? corner) => corner switch
    {
        "TopLeft" => NotifyCorner.TopLeft,
        "BottomLeft" => NotifyCorner.BottomLeft,
        "BottomRight" => NotifyCorner.BottomRight,
        _ => NotifyCorner.TopRight,
    };

    /// <summary>该角落是否靠左。</summary>
    public static bool IsLeftAligned(NotifyCorner corner)
        => corner is NotifyCorner.TopLeft or NotifyCorner.BottomLeft;

    /// <summary>该角落是否从底部向上堆叠（新消息在最下方）。</summary>
    public static bool StacksUpward(NotifyCorner corner)
        => corner is NotifyCorner.BottomLeft or NotifyCorner.BottomRight;

    /// <summary>
    /// 第 <paramref name="index"/> 张卡片的窗口左上角坐标（DIP）。
    /// index 从"锚定边"数起：上角 index 0 最贴近顶边（最新），下角 index 0 最贴近底边（最新）。
    /// </summary>
    public static (double Left, double Top) WindowOrigin(
        Rect work, NotifyCorner corner, int index, double scale = 1.0)
    {
        var slot = Math.Max(0, index);
        var s = Math.Max(0.1, scale);
        var margin = VisualMargin * s;
        var step = StackStepFor(s);

        var left = IsLeftAligned(corner)
            ? work.Left + margin - ShadowPad
            : work.Right - margin - CardWidth(s) - ShadowPad;

        var top = StacksUpward(corner)
            ? work.Bottom - margin - CardHeight(s) - ShadowPad - (slot * step)
            : work.Top + margin - ShadowPad + (slot * step);

        return (Math.Round(left), Math.Round(top));
    }

    /// <summary>第 index 张卡片的**本体**矩形（DIP），自检与截图核对用。</summary>
    public static Rect CardRect(Rect work, NotifyCorner corner, int index, double scale = 1.0)
    {
        var (left, top) = WindowOrigin(work, corner, index, scale);
        return new Rect(left + ShadowPad, top + ShadowPad, CardWidth(scale), CardHeight(scale));
    }

    /// <summary>一批卡片的本体矩形（index 0 = 最新）。</summary>
    public static IReadOnlyList<Rect> CardRects(Rect work, NotifyCorner corner, int count, double scale = 1.0)
    {
        var list = new List<Rect>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            list.Add(CardRect(work, corner, i, scale));
        }

        return list;
    }

    /// <summary>卡片最大可堆叠数量（受工作区高度限制，至少 1 张）。</summary>
    public static int MaxFit(Rect work, double scale = 1.0)
    {
        var s = Math.Max(0.1, scale);
        var available = work.Height - (VisualMargin * s * 2);
        var step = StackStepFor(s);
        return Math.Max(1, (int)Math.Floor((available + (Gap * s)) / step));
    }
}
