using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using NotifyHub.Interop;

namespace NotifyHub.Services;

/// <summary>
/// 系统能力探测（v0.2.9 需求 7）：在 Win10 / Win11 上走同一套代码，靠**运行时探测**决定行为，
/// 而不是靠编译期假设或"先试再说"。
///
/// 设计原则（为什么这么做）：
///   1. 所有探测都必须"失败也有答案"——任何一个 API 不存在、注册表读不到、DWM 关掉了，
///      都要退回安全默认值，绝不允许抛出去把主流程带崩。
///   2. 毛玻璃**不依赖任何系统级 API**。设置窗口自己画（渐变 + 半透明层 + 背景亮度采样），
///      在 Win10 1909 和 Win11 24H2 上像素级一致。DWM 的后台模糊（SetWindowCompositionAttribute /
///      DWMWA_SYSTEMBACKDROP_TYPE）在 Win11 上确实更"真"，但它需要 AllowsTransparency=False，
///      会把圆角和阴影一并交出去，且在关掉"透明效果"的机器上直接变黑块——收益不值得这个风险。
///   3. 真正需要区分系统版本的地方只有两处：**动画开关**（两代系统都有，但默认值/所在面板不同）
///      和**刷新率**（高刷屏要跑满，60Hz 屏不能空转）。
/// </summary>
public sealed record SystemCapabilities
{
    /// <summary>系统名（"Windows 10 专业版" / "Windows 11 家庭中文版"）。</summary>
    public string OsName { get; init; } = "Windows";

    /// <summary>完整版本号，例如 "10.0.19045.5737"。</summary>
    public string OsVersion { get; init; } = "0.0.0";

    /// <summary>Build 号（19045 / 22631 / 26100 …）。</summary>
    public int Build { get; init; }

    /// <summary>是否 Windows 11（Build ≥ 22000）。</summary>
    public bool IsWindows11 { get; init; }

    /// <summary>主屏 DPI（96 = 100%）。</summary>
    public int DpiForSystem { get; init; } = 96;

    /// <summary>主屏缩放比（1.5 = 150%）。</summary>
    public double PrimaryScale { get; init; } = 1.0;

    /// <summary>主屏物理分辨率。</summary>
    public string PrimaryResolution { get; init; } = "";

    /// <summary>主屏工作区（DIP）。</summary>
    public Rect WorkArea { get; init; }

    /// <summary>界面缩放（需求 1 的统一缩放系数）。</summary>
    public double UiScale { get; init; } = 1.0;

    /// <summary>主屏刷新率（Hz）。探测失败时为 0。</summary>
    public int RefreshHz { get; init; }

    /// <summary>系统"透明效果"开关（Win10 1803+ / Win11 都有）。关掉时自绘毛玻璃会自动加深，避免糊成一片。</summary>
    public bool TransparencyEffectsEnabled { get; init; } = true;

    /// <summary>系统浅色/深色（HKCU ...\Themes\Personalize\AppsUseLightTheme）。</summary>
    public bool AppsUseLightTheme { get; init; }

    /// <summary>系统"动画效果"开关。关掉时滑动/跑道动画降级为静态，避免用户以为卡住。</summary>
    public bool AnimationsEnabled { get; init; } = true;

    /// <summary>DWM 合成是否开启（关了的话 DPI / 分层窗行为会退化）。</summary>
    public bool CompositionEnabled { get; init; } = true;

    /// <summary>dwmapi.dll 是否可用（Win11 的圆角/材质属性都在里面，探测不到就别调）。</summary>
    public bool DwmApiAvailable { get; init; }

    /// <summary>当前采用的毛玻璃实现方式（写进日志/env-report，方便用户反馈时一眼定位）。</summary>
    public string GlassMode => "SelfRendered";

    /// <summary>多行纯文本报告（--env-report 直接打印）。</summary>
    public string ToReport()
    {
        var lines = new List<string>
        {
            $"系统          {OsName}",
            $"版本          {OsVersion}  (Build {Build}, {(IsWindows11 ? "Windows 11" : "Windows 10")})",
            $"DWM 合成      {(CompositionEnabled ? "开启" : "关闭")}",
            $"dwmapi.dll    {(DwmApiAvailable ? "可用" : "不可用")}",
            $"主屏          {PrimaryResolution}  缩放 {PrimaryScale * 100:F0}%  DPI {DpiForSystem}",
            $"工作区        {WorkArea.Width:F0}×{WorkArea.Height:F0} DIP",
            $"界面缩放      {UiScale:F2}×",
            $"刷新率        {(RefreshHz > 0 ? RefreshHz + " Hz" : "未探测到")}",
            $"透明效果      {(TransparencyEffectsEnabled ? "开启" : "关闭")}",
            $"应用主题      {(AppsUseLightTheme ? "浅色" : "深色")}",
            $"动画效果      {(AnimationsEnabled ? "开启" : "关闭")}",
            $"毛玻璃实现    {GlassMode}（不依赖系统模糊 API，Win10/Win11 表现一致）",
        };
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>单行摘要（日志用）。</summary>
    public string ToLogLine()
        => $"{OsName} {OsVersion} win11={IsWindows11} dpi={DpiForSystem} scale={PrimaryScale:F2} " +
           $"work={WorkArea.Width:F0}x{WorkArea.Height:F0} ui={UiScale:F2} hz={RefreshHz} " +
           $"anim={AnimationsEnabled} transparent={TransparencyEffectsEnabled} " +
           $"light={AppsUseLightTheme} glass={GlassMode}";

    private static SystemCapabilities? _cached;

    /// <summary>进程级缓存：探测要读注册表 + 调几个 API，没必要每次都做。</summary>
    public static SystemCapabilities Current => _cached ??= Probe();

    /// <summary>重新探测（测试用）。</summary>
    public static SystemCapabilities Refresh() => _cached = Probe();

    public static SystemCapabilities Probe()
    {
        var (osName, version, build) = ReadOsVersion();
        var dpi = SafeDpi();
        var scale = dpi / 96.0;
        var work = SafeWorkArea(scale);
        var resolution = SafeResolution();
        var hz = SafeRefreshHz();

        return new SystemCapabilities
        {
            OsName = osName,
            OsVersion = version,
            Build = build,
            IsWindows11 = build >= 22000,
            DpiForSystem = dpi,
            PrimaryScale = scale,
            PrimaryResolution = resolution,
            WorkArea = work,
            UiScale = ScreenMetrics.UiScale(work),
            RefreshHz = hz,
            TransparencyEffectsEnabled = SafeTransparency(),
            AppsUseLightTheme = SafeLightTheme(),
            AnimationsEnabled = SafeAnimations(),
            CompositionEnabled = SafeComposition(),
            DwmApiAvailable = SafeDwmApi(),
        };
    }

    // ── 系统版本：Environment.OSVersion 在没打 manifest 时会把 Win11 报成 Win10，
    //     RtlGetVersion 永远返回真实值，所以以它为准，失败才退回 Environment。
    private static (string Name, string Version, int Build) ReadOsVersion()
    {
        var build = 0;
        var major = 10;
        var minor = 0;
        var revision = 0;
        var name = "Windows";

        try
        {
            var info = new Interop.NativeMethods.OSVERSIONINFOEXW
            {
                dwOSVersionInfoSize = Marshal.SizeOf<Interop.NativeMethods.OSVERSIONINFOEXW>(),
            };
            if (NativeMethods.RtlGetVersionX(ref info) == 0)
            {
                major = (int)info.dwMajorVersion;
                minor = (int)info.dwMinorVersion;
                build = (int)info.dwBuildNumber;
                revision = (int)info.dwRevision;
            }
        }
        catch
        {
            // ntdll 不可用（理论上不可能），退回 Environment
        }

        if (build == 0)
        {
            var v = Environment.OSVersion.Version;
            major = v.Major;
            minor = v.Minor;
            build = v.Build;
            revision = v.Revision;
        }

        try
        {
            var product = ReadRegistryString(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
            if (!string.IsNullOrWhiteSpace(product))
            {
                name = product!;
            }
        }
        catch
        {
            name = build >= 22000 ? "Windows 11" : "Windows 10";
        }

        // ProductName 在 Win11 上仍写着 "Windows 10"（微软历史包袱），手动纠正
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            name = "Windows 11" + name.Substring("Windows 10".Length);
        }

        return (name, $"{major}.{minor}.{build}.{revision}", build);
    }

    private static int SafeDpi()
    {
        try
        {
            var dpi = (int)NativeMethods.GetDpiForSystem();
            if (dpi >= 48)
            {
                return dpi;
            }
        }
        catch
        {
        }

        return 96;
    }

    private static Rect SafeWorkArea(double scale)
    {
        try
        {
            return ScreenMetrics.PrimaryWorkAreaDip();
        }
        catch
        {
            var w = SystemParameters.WorkArea.Width;
            var h = SystemParameters.WorkArea.Height;
            return new Rect(0, 0, w > 1 ? w : 1920, h > 1 ? h : 1040);
        }
    }

    private static string SafeResolution()
    {
        try
        {
            var screen = ScreenMetrics.PrimaryScreenDip();
            var scale = SafeDpi() / 96.0;
            return $"{screen.Width * scale:F0}×{screen.Height * scale:F0}";
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>刷新率：EnumDisplaySettings 对主屏当前模式取值，两代系统都支持。</summary>
    private static int SafeRefreshHz()
    {
        try
        {
            var mode = new Interop.NativeMethods.DEVMODE_EXW
            {
                dmSize = (short)Marshal.SizeOf<Interop.NativeMethods.DEVMODE_EXW>(),
            };
            if (NativeMethods.EnumDisplaySettingsW(null, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode))
            {
                if (mode.dmDisplayFrequency is > 1 and < 1000)
                {
                    return mode.dmDisplayFrequency;
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    private static bool SafeTransparency()
    {
        try
        {
            // 0 = 关闭转场/透明效果；键不存在（老系统）视为开启
            var value = ReadRegistryInt(
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency");
            return value is null or 1;
        }
        catch
        {
            return true;
        }
    }

    private static bool SafeLightTheme()
    {
        try
        {
            var value = ReadRegistryInt(
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme");
            return value is null || value == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeAnimations()
    {
        try
        {
            // 只看 ClientAreaAnimation：这就是 Win10「显示动画」/ Win11「动画效果」那个总开关
            // （SPI_GETCLIENTAREAANIMATION）。MenuAnimation 是"菜单淡入"的独立子项，
            // 很多精简版系统默认关着，把它算进来会误判成"用户不想要动画"。
            return SystemParameters.ClientAreaAnimation;
        }
        catch
        {
            return true;
        }
    }

    private static bool SafeComposition()
    {
        try
        {
            return NativeMethods.DwmIsCompositionEnabledNative(out var enabled) == 0 && enabled;
        }
        catch
        {
            return true;
        }
    }

    private static bool SafeDwmApi()
    {
        try
        {
            return NativeMethods.DwmIsCompositionEnabledNative(out _) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadRegistryInt(string path, string key)
    {
        var value = Registry.GetValue(path, key, null);
        return value switch
        {
            int i => i,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? ReadRegistryString(string path, string key)
    {
        try
        {
            return Registry.GetValue(path, key, null) as string;
        }
        catch
        {
            return null;
        }
    }
}
