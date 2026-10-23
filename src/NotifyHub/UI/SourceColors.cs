using System.Windows.Media;
using NotifyHub.Models;

namespace NotifyHub.UI;

/// <summary>
/// 通知来源配色（v0.2.9 需求 3）。
///
/// 为什么单独抽一个类：同一条消息现在有三个地方要上色——四角通知卡的图标、
/// 灵动岛内的通知行（点 / 标签 / 脊线）、收纳细条的跑道光块。
/// 过去颜色是散落在 <see cref="NotificationWindow.Fill"/> 里的 switch，
/// 岛和细条没法复用，只能自己再抄一份——一抄就会漂移。
///
/// 色板与音乐（黄 #FFD60A）、WorkBuddy 任务（琥珀 #FFB020）、完成（绿 #34C759）
/// 的统一风格：明亮但不刺眼，同一饱和度区间，深色毛玻璃上都清晰。
/// </summary>
public static class SourceColors
{
    /// <summary>微信绿（官方色）。</summary>
    public static readonly Color WeChat = Color.FromRgb(0x07, 0xC1, 0x60);

    /// <summary>QQ 蓝（官方色）。</summary>
    public static readonly Color QQ = Color.FromRgb(0x12, 0xB7, 0xF5);

    /// <summary>ntfy 橙（默认订阅色）。</summary>
    public static readonly Color Ntfy = Color.FromRgb(0xF9, 0x73, 0x16);

    /// <summary>音乐强调黄（与岛上脊线同源）。</summary>
    public static readonly Color Music = Color.FromRgb(0xFF, 0xD6, 0x0A);

    /// <summary>WorkBuddy 执行中琥珀。</summary>
    public static readonly Color Task = Color.FromRgb(0xFF, 0xB0, 0x20);

    /// <summary>完成绿。</summary>
    public static readonly Color Done = Color.FromRgb(0x34, 0xC7, 0x59);

    /// <summary>
    /// 一条消息的强调色：ntfy 多订阅优先用订阅色板，其余按来源取官方色。
    /// </summary>
    public static Color AccentOf(NotifyMessage message)
    {
        if (message.Source == MessageSource.Ntfy
            && message.AccentIndex >= 0
            && message.AccentIndex < AppConfig.NtfyPalette.Count)
        {
            var rgb = AppConfig.NtfyPalette[message.AccentIndex];
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return message.Source switch
        {
            MessageSource.WeChat => WeChat,
            MessageSource.QQ => QQ,
            _ => Ntfy,
        };
    }

    /// <summary>用来源色生成的渐变画刷（图标底/胶囊底，比纯色更有层次）。</summary>
    public static LinearGradientBrush GradientOf(Color color)
    {
        var light = Color.FromRgb(
            (byte)Math.Min(255, color.R + 18),
            (byte)Math.Min(255, color.G + 18),
            (byte)Math.Min(255, color.B + 18));

        return new LinearGradientBrush(light, color, 90);
    }

    /// <summary>图标里的单字（微信「微」/QQ「Q」/ntfy「N」）。</summary>
    public static string GlyphOf(NotifyMessage message) => message.Source switch
    {
        MessageSource.WeChat => "微",
        MessageSource.QQ => "Q",
        _ => "N",
    };
}
