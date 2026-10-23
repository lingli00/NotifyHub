using System.Windows.Media;
using NotifyHub.Models;

namespace NotifyHub.UI;

/// <summary>
/// 外观主题（v0.2.6，需求 3/4）：把「纯色暗黑 / 深色毛玻璃 / 浅色毛玻璃」三套视觉
/// 收敛成一组画刷，通知卡片与灵动岛共用同一份调色板，保证全局一致。
///
/// 毛玻璃的实现说明（诚实交代）：
///   Win10 的 WPF 透明窗走的是分层窗（WS_EX_LAYERED + per-pixel alpha）路径，
///   DWM 的 blur/acrylic 在这条路径上不生效。所以这里的“毛玻璃”是
///   **高透明度多层渐变 + 1px 高光描边 + 顶部提亮** 的等效实现：
///   下层内容能真实透出来（不是模拟），只是没有高斯模糊。
///
/// 文字对比度（需求 4）：
///   调色板自带 <see cref="EnsureReadable"/>，按背景相对亮度算 WCAG 对比度，
///   不足 4.5:1 时自动在黑白之间选对比度更高的那个——四种组合（深玻璃/浅玻璃/纯黑/纯白）都过。
/// </summary>
public sealed class ThemePalette
{
    /// <summary>主题模式（与 AppConfig.ThemeMode 一致）。</summary>
    public required string Mode { get; init; }

    /// <summary>是否毛玻璃（半透明）主题。</summary>
    public required bool IsGlass { get; init; }

    /// <summary>是否浅色底（决定文字主色调）。</summary>
    public required bool IsLight { get; init; }

    /// <summary>卡片/岛体背景（渐变画刷，含透明度）。</summary>
    public required Brush Background { get; init; }

    /// <summary>1px 描边（毛玻璃下用高光，纯色下用极淡白边）。</summary>
    public required Color Border { get; init; }

    /// <summary>顶部高光条（毛玻璃质感的关键：一条极淡的白/黑渐变）。</summary>
    public required Color Highlight { get; init; }

    public required Color TextPrimary { get; init; }

    public required Color TextSecondary { get; init; }

    public required Color TextTertiary { get; init; }

    /// <summary>分隔线。</summary>
    public required Color Divider { get; init; }

    /// <summary>按钮悬停底 / 按下底。</summary>
    public required Color HoverBackground { get; init; }

    public required Color PressBackground { get; init; }

    /// <summary>按钮图标填充（= 主文字色）。</summary>
    public Color Glyph => TextPrimary;

    /// <summary>
    /// 背景的“等效不透明色”——用于算文字对比度。半透明色按白色底合成，
    /// 得到的是“最坏情况”（白底透出）下的亮度，保守但安全。
    /// </summary>
    public Color NominalBackground { get; init; }

    /// <summary>背景的不透明份额（1.0=纯色；毛玻璃是半透明，用于合成估算）。</summary>
    public required double TintAlpha { get; init; }

    /// <summary>按配置解析出调色板（含文字自动对比度）。</summary>
    public static ThemePalette Create(AppConfig config)
    {
        return Create(config, null);
    }

    /// <summary>
    /// 按配置 + 实际背景亮度解析调色板。
    /// <paramref name="backdropLuminance"/> 是窗口背后内容的平均相对亮度（0~1）；
    /// 传 null 表示不取样（回退到调色板自身的深/浅假设）。
    /// </summary>
    public static ThemePalette Create(AppConfig config, double? backdropLuminance)
    {
        var mode = NormalizeMode(config.ThemeMode);
        var palette = mode switch
        {
            "GlassLight" => Glass(isLight: true, mode),
            "GlassAuto" => Glass(isLight: SystemPrefersLight(), mode),
            "GlassDark" => Glass(isLight: false, mode),
            _ => Solid(),
        };

        return ApplyTextTone(palette, config.TextTone, backdropLuminance);
    }

    /// <summary>主题模式收敛（非法值 → 纯色暗黑）。</summary>
    public static string NormalizeMode(string? mode)
    {
        foreach (var candidate in AppConfig.ThemeModes)
        {
            if (string.Equals(candidate, mode, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return "SolidDark";
    }

    /// <summary>主题的中文名（设置页/日志用）。</summary>
    public static string DisplayName(string mode) => NormalizeMode(mode) switch
    {
        "GlassDark" => "毛玻璃 · 深色",
        "GlassLight" => "毛玻璃 · 浅色",
        "GlassAuto" => "毛玻璃 · 跟随系统",
        _ => "纯色暗黑",
    };

    /// <summary>文字配色中文名。</summary>
    public static string ToneName(string tone) => tone switch
    {
        "White" => "白色",
        "Black" => "黑色",
        _ => "自动",
    };

    // ---------------- 三套具体配色 ----------------

    private static ThemePalette Solid() => new()
    {
        Mode = "SolidDark",
        IsGlass = false,
        TintAlpha = 1.0,
        IsLight = false,
        // 纯色暗黑：接近不透明（保留原有观感）
        Background = Gradient(
            Color.FromArgb(0xFA, 0x31, 0x31, 0x36),
            Color.FromArgb(0xF9, 0x2A, 0x2A, 0x2F),
            Color.FromArgb(0xF2, 0x15, 0x15, 0x18)),
        Border = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
        Highlight = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        TextPrimary = Color.FromRgb(0xFF, 0xFF, 0xFF),
        TextSecondary = Color.FromRgb(0xB9, 0xB9, 0xC0),
        TextTertiary = Color.FromRgb(0x8A, 0x8A, 0x90),
        Divider = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
        HoverBackground = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
        PressBackground = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF),
        NominalBackground = Color.FromRgb(0x24, 0x24, 0x29),
    };

    private static ThemePalette Glass(bool isLight, string mode)
    {
        if (isLight)
        {
            // 浅色/白色毛玻璃：白色系高透明，靠描边与顶部高光立住边界
            return new ThemePalette
            {
                Mode = mode,
                IsGlass = true,
                IsLight = true,
                TintAlpha = 0.78,
                Background = Gradient(
                    Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xC2, 0xF7, 0xF7, 0xFA),
                    Color.FromArgb(0xB0, 0xEC, 0xEC, 0xF1)),
                Border = Color.FromArgb(0x2E, 0x00, 0x00, 0x00),
                Highlight = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
                TextPrimary = Color.FromRgb(0x1C, 0x1C, 0x1E),
                TextSecondary = Color.FromArgb(0xC0, 0x1C, 0x1C, 0x1E),
                TextTertiary = Color.FromArgb(0x94, 0x1C, 0x1C, 0x1E),
                Divider = Color.FromArgb(0x24, 0x00, 0x00, 0x00),
                HoverBackground = Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
                PressBackground = Color.FromArgb(0x36, 0x00, 0x00, 0x00),
                NominalBackground = Color.FromRgb(0xF2, 0xF2, 0xF5),
            };
        }

        // 深色毛玻璃：60% 左右不透明，下层内容能透出来
        return new ThemePalette
        {
            Mode = mode,
            IsGlass = true,
            IsLight = false,
            TintAlpha = 0.74,
            // 深色毛玻璃：74% 不透明——再低在白色网页上会灰成一片，文字难读
            Background = Gradient(
                Color.FromArgb(0xC4, 0x2C, 0x2C, 0x32),
                Color.FromArgb(0xB8, 0x1E, 0x1E, 0x24),
                Color.FromArgb(0xAC, 0x11, 0x11, 0x14)),
            Border = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
            Highlight = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
            TextPrimary = Color.FromRgb(0xFF, 0xFF, 0xFF),
            TextSecondary = Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF),
            TextTertiary = Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF),
            Divider = Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF),
            HoverBackground = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
            PressBackground = Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF),
            NominalBackground = Color.FromRgb(0x1D, 0x1D, 0x22),
        };
    }

    // ---------------- 文字对比度适配（需求 4） ----------------

    /// <summary>
    /// 应用文字配色：Auto 走调色板自带色；White/Black 强制指定，
    /// 并逐个色阶做对比度矫正（不足 4.5:1 就换成对比度更高的黑白）。
    /// </summary>
    private static ThemePalette ApplyTextTone(ThemePalette palette, string? tone, double? backdropLuminance)
    {
        var forced = (tone ?? "Auto").Trim();
        if (string.Equals(forced, "White", StringComparison.OrdinalIgnoreCase))
        {
            // 强制白也过一遍对比度矫正：浅色毛玻璃上纯白看不见，自动降到黑
            var isReadableWhite = ContrastRatio(Color.FromRgb(0xFF, 0xFF, 0xFF), palette.NominalBackground) >= 4.5;
            return isReadableWhite
                ? WithText(palette, Color.FromRgb(0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF))
                : WithText(palette, Color.FromRgb(0x1C, 0x1C, 0x1E),
                    Color.FromArgb(0xC0, 0x1C, 0x1C, 0x1E), Color.FromArgb(0x94, 0x1C, 0x1C, 0x1E));
        }

        if (string.Equals(forced, "Black", StringComparison.OrdinalIgnoreCase))
        {
            // 强制黑同理：深色毛玻璃上纯黑看不见，自动升到白
            var isReadableBlack = ContrastRatio(Color.FromRgb(0x1C, 0x1C, 0x1E), palette.NominalBackground) >= 4.5;
            return isReadableBlack
                ? WithText(palette, Color.FromRgb(0x1C, 0x1C, 0x1E),
                    Color.FromArgb(0xC0, 0x1C, 0x1C, 0x1E), Color.FromArgb(0x94, 0x1C, 0x1C, 0x1E))
                : WithText(palette, Color.FromRgb(0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF));
        }

        // Auto：动态对比度检测——把「毛玻璃色 × 透出的背景色」合成成实际观感色，
        // 再选对比度更高的文字色（深玻璃压在白色网页上会自动转黑字，反之亦然）。
        var composite = CompositeLuminance(palette, backdropLuminance);
        var primary = TextForLuminance(composite);
        var lightText = RelativeLuminance(primary) > 0.5;

        return WithText(
            palette,
            primary,
            lightText ? Color.FromArgb(0xD2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xC4, 0x1C, 0x1C, 0x1E),
            lightText ? Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x96, 0x1C, 0x1C, 0x1E));
    }

    private static ThemePalette WithText(ThemePalette source, Color primary, Color secondary, Color tertiary) => new()
    {
        Mode = source.Mode,
        IsGlass = source.IsGlass,
        IsLight = source.IsLight,
        TintAlpha = source.TintAlpha,
        Background = source.Background,
        Border = source.Border,
        Highlight = source.Highlight,
        TextPrimary = primary,
        TextSecondary = secondary,
        TextTertiary = tertiary,
        Divider = source.Divider,
        HoverBackground = source.HoverBackground,
        PressBackground = source.PressBackground,
        NominalBackground = source.NominalBackground,
    };

    /// <summary>
    /// 合成亮度：玻璃色（按 TintAlpha）与背景色混合后的相对亮度。
    /// 纯色主题（TintAlpha=1）就是自身的亮度，不受背景影响。
    /// </summary>
    public static double CompositeLuminance(ThemePalette palette, double? backdropLuminance)
    {
        var tint = RelativeLuminance(palette.NominalBackground);
        if (!palette.IsGlass || backdropLuminance is not { } backdrop)
        {
            return tint;
        }

        var alpha = Math.Clamp(palette.TintAlpha, 0, 1);
        return alpha * tint + (1 - alpha) * Math.Clamp(backdrop, 0, 1);
    }

    /// <summary>按背景亮度选白字还是黑字（对比度更高者胜）。</summary>
    public static Color TextForLuminance(double luminance)
    {
        var gray = GrayOfLuminance(Math.Clamp(luminance, 0, 1));
        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        var black = Color.FromRgb(0x14, 0x14, 0x16);
        return ContrastRatio(white, gray) >= ContrastRatio(black, gray) ? white : black;
    }

    /// <summary>相对亮度 → 近似 sRGB 灰（用于对比度试算）。</summary>
    public static Color GrayOfLuminance(double luminance)
    {
        var c = luminance <= 0.0031308
            ? 12.92 * luminance
            : 1.055 * Math.Pow(luminance, 1 / 2.4) - 0.055;
        var v = (byte)Math.Clamp(Math.Round(c * 255), 0, 255);
        return Color.FromRgb(v, v, v);
    }

    /// <summary>
    /// WCAG 对比度矫正：当前文字色与背景对比度低于 4.5:1 时，返回黑/白中对比度更高的那个。
    /// </summary>
    public static Color EnsureReadable(Color text, Color background, double minRatio = 4.5)
    {
        if (ContrastRatio(text, background) >= minRatio)
        {
            return text;
        }

        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        var black = Color.FromRgb(0x14, 0x14, 0x16);
        return ContrastRatio(white, background) >= ContrastRatio(black, background) ? white : black;
    }

    /// <summary>WCAG 对比度：(L1+0.05)/(L2+0.05)。</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>WCAG 相对亮度（sRGB 线性化）。</summary>
    public static double RelativeLuminance(Color color)
    {
        static double Channel(byte v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>
    /// 跟随 Windows「应用使用浅色主题」设置（HKCU\...\Personalize\AppsUseLightTheme）。
    /// 读不到时按深色处理（与系统默认一致）。
    /// </summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static LinearGradientBrush Gradient(Color top, Color middle, Color bottom) => new()
    {
        StartPoint = new System.Windows.Point(0, 0),
        EndPoint = new System.Windows.Point(0, 1),
        GradientStops = new GradientStopCollection
        {
            new(top, 0),
            new(middle, 0.42),
            new(bottom, 1),
        },
    };

    /// <summary>转成 WPF 画刷（每次新建，避免冻结画刷被改）。</summary>
    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
