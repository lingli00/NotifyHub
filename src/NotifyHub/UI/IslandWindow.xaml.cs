using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using NotifyHub.Interop;
using NotifyHub.Models;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 灵动岛 / 悬浮条：单一圆角长条，两栏「等信息量」布局（v0.2.7）
///   左栏：[App图标/♪] 歌名-歌手 + [上一首][播放/暂停][下一首]，下面挂双行歌词
///   右栏：● WorkBuddy + 一行实时动作（跨两行垂直居中）
/// 固定模式锁定屏幕顶部居中；自由模式下按住空白区可拖动（左键/右键都行）。
/// 拖动与吸附只允许顶部边缘：水平限中间 3/5（最左/最右 1/5 禁停）。
///
/// 收纳规则（v0.2.7 需求 2，已统一）：
///   单一计时器 <c>_autoTimer</c> 管全部情形——新任务/状态更新/新消息/音乐变化会把岛
///   弹出来并重置倒计时；超时（默认 3 秒，可配）后滑到顶部收成细条，鼠标移上去即展开。
///   不再区分"有没有任务在跑"（旧版任务期间常驻、无任务才收纳，表现随机）。
///   三条例外：鼠标停在岛上、完成态 600ms 收尾、音乐播放且开启了"播放时保持展开"。
///
/// 长文本（歌名/歌词/任务状态）走跑马灯：超宽自动横滚、悬停暂停、速度可调。
///
/// 拖拽为什么不走 WPF 的鼠标事件：
///   本窗是 WS_EX_NOACTIVATE + AllowsTransparency 的分层窗口，未激活时第一下点击
///   会被系统当成"激活点击"吞掉，WPF 路由事件收不全，表现为"怎么拖都不动"。
///   所以拖拽链路全部下沉到 HwndSource 的 Win32 消息（WM_NCHITTEST / DOWN / MOVE / UP）
///   并用 SetCapture + SetWindowPos 直接移动 HWND，既可靠又没有 WPF 布局开销。
/// </summary>
public partial class IslandWindow : Window
{
    /// <summary>执行态最长驻留（调用方崩溃/忘记发完成信号时的兜底）。</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    /// <summary>完成态停留时长（需求固定 600ms）。</summary>
    private static readonly TimeSpan DoneHold = TimeSpan.FromMilliseconds(600);

    /// <summary>展开动画时长（需求固定 180ms）。</summary>
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>状态文字切换淡入时长（需求固定 120ms）。</summary>
    private static readonly TimeSpan TextSwapDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>收缩消失动画时长（需求固定 220ms）。</summary>
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>音乐停止缓冲（换曲间隙防闪烁）。</summary>
    private static readonly TimeSpan MusicHideGrace = TimeSpan.FromMilliseconds(2200);

    /// <summary>超时自动收纳的默认时长（秒，v0.2.7 需求 2）。</summary>
    public const int DefaultAutoCollapseSeconds = 3;

    /// <summary>超时自动收纳允许范围（秒）。</summary>
    public const int MinAutoCollapseSeconds = 1;

    public const int MaxAutoCollapseSeconds = 60;

    /// <summary>吸附时条体与屏幕边之间的视觉缝隙。</summary>
    private const double EdgeGap = 8;

    /// <summary>滑入/滑出动画时长。</summary>
    private const int SlideDurationMs = 240;

    /// <summary>
    /// 歌词宽度下限 / 上限（DIP，v0.2.8 自适应宽度）。
    /// 有任务时歌词最多 330（左右两栏配平）；只有音乐+歌词时放宽到 560，
    /// 长歌词可以把岛拉长，而不是挤在 270 里滚动。
    /// </summary>
    private const double MinLyricWidth = 150;
    private const double MaxLyricWidthWithTask = 330;
    private const double MaxLyricWidthSolo = 560;

    /// <summary>歌名区宽度上下限（DIP）：太短会把标题挤成跑马灯，太长会把岛撑宽。</summary>
    private const double MinTitleWidth = 104;

    /// <summary>
    /// 待机态歌名区的固定留白宽度（v0.2.12 需求 3）：没有歌时这里什么都不显示，
    /// 但要占住位置，否则岛会缩成一个只有图标和三键的窄条，和右侧不对称。
    /// </summary>
    private const double MusicTitleMinWidth = 132;
    private const double MaxTitleWidthWithTask = 200;
    private const double MaxTitleWidthSolo = 280;

    /// <summary>任务正文宽度上下限（DIP）。</summary>
    private const double MinStatusWidth = 160;
    private const double MaxStatusWidthWithMusic = 330;
    private const double MaxStatusWidthSolo = 460;

    /// <summary>歌词切换的淡入时长（v0.2.7：新行出现时轻微上浮 + 淡入）。</summary>
    private static readonly TimeSpan LyricSwapDuration = TimeSpan.FromMilliseconds(170);

    private readonly DispatcherTimer _statusTimer = new();
    private readonly DispatcherTimer _musicTimer = new();
    /// <summary>滑动动画是否挂在 CompositionTarget.Rendering 上（v0.2.8：帧同步，替代 15ms 计时器）。</summary>
    private bool _slideFrameHooked;

    /// <summary>上一帧合成时间戳（FrameClock 去重用，v0.2.9 需求 1）。</summary>
    private TimeSpan _slideFrameTime = TimeSpan.MinValue;

    /// <summary>已经排过一次「布局后重新居中」（合并重复请求用）。</summary>
    private bool _recenterScheduled;

    /// <summary>音乐播放器进程是否在运行（v0.2.11 需求 2，由 PresenceWatcher 推送）。</summary>
    private bool _presenceMusic;

    /// <summary>WorkBuddy 进程是否在运行（v0.2.11 需求 2）。</summary>
    private bool _presenceWorkbuddy;

    /// <summary>当前是否在显示"WorkBuddy 在线"占位行。</summary>
    private bool _presenceVisible;

    /// <summary>
    /// 待机展示用的左栏音乐组（v0.2.14 需求 3）：播放器进程在跑但没在放歌时显示
    /// 「图标 + 空歌名区 + 三个播放键」。它只是形态，不算内容（不阻止收纳、不算信息展示）。
    /// </summary>
    private bool _standbyMusicShown;

    /// <summary>检测到的播放器进程名（v0.2.12 需求 3：没有播放会话时也要能显示它的图标）。</summary>
    private string? _presenceMusicProcess;

    /// <summary>
    /// 指针是否正压在岛上（v0.2.10 需求 3）。
    ///
    /// 为什么不用 Pill.IsMouseOver：岛是 **点穿窗口**（WM_NCHITTEST 对非按钮区域返回 HTTRANSPARENT），
    /// 系统根本不会把鼠标消息发给它 —— IsMouseOver 在固定模式下永远是 false，
    /// "悬停放大/悬停暂停计时"这类需求靠它实现不了（实测：悬停前后像素零变化）。
    /// 这里改成自己轮询光标位置，只在岛可见时跑（120ms 一次，一次 GetCursorPos）。
    /// </summary>
    private bool _pointerOverIsland;

    private DispatcherTimer? _hoverWatch;

    /// <summary>细条最近一次现身的时间（防止刚收起就被展开，v0.2.9 需求 2）。</summary>
    private long _stripShownAtMs;

    /// <summary>
    /// 「用户刚双击唤起」的保持截止时刻（v0.2.14 需求 2）。
    /// 从待机条双击拉起播放器/WorkBuddy 后的这段时间里，岛保持展开成待机展示态
    /// （左栏图标 + 三个播放键），用户才有机会点播放键——否则一松开鼠标就收成窄条，
    /// 需求 2 的"通过灵动岛按钮控制冷启动的播放器"根本没法操作。
    /// </summary>
    private long _launchHoldUntilMs;

    /// <summary>保持时长：15 秒（够播放器起来并注册 SMTC，也不至于长期占屏）。</summary>
    private const int LaunchHoldMs = 15_000;

    /// <summary>
    /// 唯一的「超时自动收纳」计时器（v0.2.7 需求 2）。
    /// 旧版有两个互相打架的计时器（10s 无交互吸附 + 1.4s 从细条收回），再加上
    /// “有任务就不吸附”的分支，表现就是随机常驻 / 随机收起。现在所有情形都归它管：
    /// 有新消息、新任务、状态更新、音乐变化时重置，超时统一收成顶部细条。
    /// </summary>
    private readonly DispatcherTimer _autoTimer = new();

    private bool _statusVisible;
    private bool _musicVisible;
    private bool _musicPlaying;
    private bool _fullscreen;
    private bool _hidden = true;
    private bool _dragging;
    private bool _collapsing;
    private MediaInfo? _media;

    /// <summary>
    /// 岛的界面缩放（v0.2.9 需求 1）。整块 Pill 用 LayoutTransform 等比放大，
    /// 因此岛内所有 DIP 常量（最小歌词宽、字号…）仍然按"缩放前的坐标系"工作，
    /// 不需要处处置乘——只有窗口级的定位与吸附间距需要关心它。
    /// </summary>
    private double _uiScale = 1.0;

    /// <summary>当前状态行是否是“新消息提示”的临时内容（超时后整条收起，不留在细条里）。</summary>
    private bool _statusTransient;

    // ---------------- 岛内通知（v0.2.9 需求 3） ----------------

    /// <summary>当前在岛内展示的通知（点击跳转要用它的来源/发件人）。</summary>
    private NotifyMessage? _notice;

    /// <summary>通知行是否可见。</summary>
    private bool _noticeVisible;

    /// <summary>通知行出现的时刻（细条配色用它判断"是不是刚来的通知"）。</summary>
    private DateTime _noticeAt = DateTime.MinValue;

    /// <summary>通知行停留时长（毫秒）：比普通收纳时间略长，够看清也够点。</summary>
    private const int NoticeHoldMs = 6000;

    /// <summary>通知行的停留计时器（到点只收通知行，不影响音乐/任务）。</summary>
    private readonly DispatcherTimer _noticeTimer = new();

    /// <summary>
    /// 空闲展开（v0.2.9 需求 2）：用户手动点开细条时置 true。
    /// 此时岛上没有任何内容也保持展开，超时后回到细条。
    /// </summary>

    /// <summary>岛内通知被点击（由 IslandController 订阅后打开对应消息）。</summary>
    public event EventHandler<NotifyMessage>? NoticeClicked;

    /// <summary>歌词区换句时的位移变换（只影响渲染，不影响布局）。</summary>
    private readonly TranslateTransform _lyricShift = new();

    // 动画防粘连：每次显隐都递增令牌，过期动画的 Completed 不再动窗口状态
    private int _animToken;

    // 吸附状态
    private bool _snapped;
    private bool _sliding;
    private Edge _snapEdge = Edge.None;

    // 吸附后的「细条」形态：岛隐藏，只显示一条可点的细条（贴左右边 = 竖条）
    private SnapStripWindow? _strip;
    private bool _stripMode;

    // 滑动动画（SetWindowPos 逐帧，像素坐标）
    private readonly Stopwatch _slideWatch = new();
    private int _slideFromX;
    private int _slideFromY;
    private int _slideToX;
    private int _slideToY;
    private int _slideDuration;
    private Action? _slideDone;

    // 拖拽状态（HWND 像素坐标，避开 DIP 换算误差累积）
    private IntPtr _hwnd;
    private HwndSource? _source;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartWindow;
    private bool _dragMoved;

    private enum Edge
    {
        None,
        Top,
    }

    public IslandWindow()
    {
        InitializeComponent();

        // 两组内容默认都收起来：只有真正有任务/音乐时才显示，
        // 否则第一次弹出会带出 XAML 里的占位文字（"正在执行任务…"）
        StatusGroup.Visibility = Visibility.Collapsed;
        MusicGroup.Visibility = Visibility.Collapsed;
        Divider.Visibility = Visibility.Collapsed;

        // 跑马灯速度跟随配置（可调）
        MusicText.Speed = MarqueeSpeedProvider?.Invoke() ?? MarqueeText.DefaultSpeed;
        StatusText.Speed = MarqueeSpeedProvider?.Invoke() ?? MarqueeText.DefaultSpeed;

        // 歌词与状态行左对齐（居中的歌词看起来是散的，左对齐才像歌词卡）；
        // 歌名也左对齐，和下方歌词左边界齐平
        MusicText.Anchor = TextAnchor.Left;
        LyricLine1.Anchor = TextAnchor.Left;
        LyricTrans.Anchor = TextAnchor.Left;
        LyricLine2.Anchor = TextAnchor.Left;
        StatusText.Anchor = TextAnchor.Left;
        LyricsPanel.RenderTransform = _lyricShift;

        _statusTimer.Tick += (_, _) => OnStatusHoldElapsed();
        _musicTimer.Tick += (_, _) => OnMusicGraceElapsed();
        _autoTimer.Tick += (_, _) => OnAutoCollapseElapsed();
        _noticeTimer.Interval = TimeSpan.FromMilliseconds(NoticeHoldMs);
        _noticeTimer.Tick += (_, _) => OnNoticeHoldElapsed();

        // 鼠标在岛上时不收纳（用户正在看歌词/状态），离开再重新计时
        Pill.MouseEnter += OnPillMouseEnter;
        Pill.MouseLeave += OnPillMouseLeave;

        // 窗口是 SizeToContent：文字变长/变短会改变宽度。
        // 固定模式必须跟着重量位置，否则岛会越来越偏（不再居中）；
        // 自由模式保持视觉中心不动，避免宽度变化时整条"往右窜"。
        SizeChanged += OnIslandSizeChanged;

        // 构造函数里只有默认字号（provider 要等外部注入后才能读），首帧加载时再刷一次
        Loaded += (_, _) => ApplyLyricsTypography();

        ApplyLyricsTypography();
        ApplyUiScale();
    }

    /// <summary>
    /// 按屏幕尺寸刷新岛的界面缩放（v0.2.9 需求 1）。
    /// 用 LayoutTransform 而不是 RenderTransform：前者参与布局，窗口 SizeToContent
    /// 会跟着变大，居中与吸附不用额外改公式。
    /// </summary>
    public void ApplyUiScale()
    {
        var scale = ScreenMetrics.UiScaleFor(this);
        if (Math.Abs(scale - _uiScale) < 0.01 && Pill.LayoutTransform is ScaleTransform)
        {
            return;
        }

        _uiScale = scale;
        Pill.LayoutTransform = new ScaleTransform(scale, scale);
    }

    /// <summary>当前界面缩放（诊断/自检用）。</summary>
    public double UiScale => _uiScale;

    /// <summary>当前实例（整个应用一个小岛）。</summary>
    public static IslandWindow? Current { get; private set; }

    /// <summary>显示/隐藏结束后回调（独立模式用它决定何时退出进程）。</summary>
    public event EventHandler? Hidden;

    /// <summary>自由模式拖拽结束后提交新位置（DIP）。</summary>
    public event Action<double, double>? PositionCommitted;

    /// <summary>任务完成态出现（用于播放独立的完成提示音）。</summary>
    public event EventHandler? TaskDoneShown;

    /// <summary>音乐快捷控制请求（由 IslandController 订阅后转发给播放器）。</summary>
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;

    /// <summary>注入的配置读取：位置模式（"Fixed"/"Free"）。</summary>
    public Func<string>? ModeProvider { get; set; }

    /// <summary>注入的配置读取：自由模式保存的位置（null=未保存）。</summary>
    public Func<(double Left, double Top)?>? SavedPositionProvider { get; set; }

    /// <summary>注入的配置读取：音乐控制模块开关。</summary>
    public Func<bool>? MusicEnabledProvider { get; set; }

    /// <summary>注入的配置读取：跑马灯滚动速度（DIP/秒）。</summary>
    public Func<int>? MarqueeSpeedProvider { get; set; }

    /// <summary>注入的配置读取：歌词总开关（v0.2.7 需求 1）。</summary>
    public Func<bool>? LyricsEnabledProvider { get; set; }

    /// <summary>注入的配置读取：歌词字号（v0.2.7 需求 1，DIP）。</summary>
    public Func<int>? LyricsFontSizeProvider { get; set; }

    /// <summary>注入的配置读取：超时自动收纳总开关（v0.2.7 需求 2）。</summary>
    public Func<bool>? AutoCollapseEnabledProvider { get; set; }

    /// <summary>注入的配置读取：超时收纳时长（秒，v0.2.7 需求 2）。</summary>
    public Func<int>? AutoCollapseSecondsProvider { get; set; }

    /// <summary>注入的配置读取：音乐播放期间保持展开（不自动收纳）。</summary>
    public Func<bool>? KeepOpenWhilePlayingProvider { get; set; }

    /// <summary>注入的配置读取：灵动岛总长度（百分比，v0.2.9 需求 6）。</summary>
    public Func<int>? IslandWidthPercentProvider { get; set; }

    /// <summary>注入的配置读取：岛内左右区域分配比例（左=播放器占比，v0.2.9 需求 6）。</summary>
    public Func<int>? IslandSplitRatioProvider { get; set; }

    /// <summary>歌词区当前是否展开（诊断用）。</summary>
    public bool IsLyricsShown => LyricsPanel.Visibility == Visibility.Visible;

    /// <summary>当前歌词字号（诊断用，DIP）。</summary>
    public double LyricsFontSize => LyricLine1.FontSize;

    /// <summary>译文行是否正在显示（诊断用）。</summary>
    public bool IsTranslationShown => LyricTrans.Visibility == Visibility.Visible;

    /// <summary>岛当前的实际宽度（诊断/自检用，DIP）。</summary>
    public double IslandWidth => ActualWidth > 1 ? ActualWidth : 0;

    /// <summary>排版诊断快照（--island-shot / 排障日志用）。</summary>
    public string LayoutDebug =>
        $"岛宽={ActualWidth:F0} scale={_uiScale:F2} 歌名宽={MusicText.Width:F0}/自然{MusicText.NaturalWidth:F0} " +
        $"歌词宽={LyricLine1.Width:F0}/自然1={LyricLine1.NaturalWidth:F0}/自然2={LyricLine2.NaturalWidth:F0} " +
        $"任务宽={StatusText.Width:F0}/自然{StatusText.NaturalWidth:F0} " +
        $"通知={(_noticeVisible ? NoticeTag.Text : "无")}/宽{NoticeText.Width:F0} " +
        $"左栏={LeftPane.ActualWidth:F0} 右栏={RightPane.ActualWidth:F0} " +
        $"歌词可见={LyricsPanel.Visibility} 译文={LyricTrans.Visibility} " +
        $"| 歌名[{MusicText.DebugState}] 歌词1[{LyricLine1.DebugState}]";

    /// <summary>自动收纳计时器是否在跑（诊断用）。</summary>
    public bool IsAutoTimerRunning => _autoTimer.IsEnabled;

    private bool AutoCollapseEnabled() => AutoCollapseEnabledProvider?.Invoke() ?? true;

    private bool KeepOpenWhilePlaying() => KeepOpenWhilePlayingProvider?.Invoke() ?? true;

    private int AutoCollapseSeconds()
    {
        var seconds = AutoCollapseSecondsProvider?.Invoke() ?? DefaultAutoCollapseSeconds;
        return Math.Clamp(seconds, MinAutoCollapseSeconds, MaxAutoCollapseSeconds);
    }

    /// <summary>
    /// 按配置刷新歌词排版（v0.2.7 需求 1）：当前行比设定字号大 1.5，下一行小 1.5 且更暗，
    /// 形成明确的“当前 / 下一句”层次。
    /// </summary>
    public void ApplyLyricsTypography()
    {
        var size = LyricsFontSizeProvider?.Invoke() ?? 13;
        size = Math.Clamp(size, AppConfig.MinLyricsFontSize, AppConfig.MaxLyricsFontSize);

        LyricLine1.FontSize = size + 1;
        LyricLine1.FontWeight = FontWeights.SemiBold;
        LyricTrans.FontSize = Math.Max(9, size - 2);
        LyricTrans.FontWeight = FontWeights.Normal;
        LyricLine2.FontSize = Math.Max(9, size - 2);
        LyricLine2.FontWeight = FontWeights.Normal;

        UpdateChrome();
    }

    /// <summary>左栏（音乐 + 歌词）实际宽度，诊断/自检用。</summary>
    public double LeftPaneWidth => LeftPane.ActualWidth;

    /// <summary>右栏（任务 + 通知）实际宽度，诊断/自检用。</summary>
    public double RightPaneWidth => RightPane.ActualWidth;

    /// <summary>
    /// 按当前实际布局重算左右两栏配平（v0.2.7）。正常由 <see cref="UpdateChrome"/>
    /// 自动调用；离屏自检需要显式触发一次（那时没有 Show，不会走 SizeChanged）。
    /// </summary>
    public void RefreshLayoutMetrics()
    {
        UpdateChrome();
        UpdateLayout();
    }

    /// <summary>
    /// 按当前屏幕背景重新取一次样并应用主题（v0.2.6 需求 4）。
    /// 只在岛出现时调用——毛玻璃后的内容会变，取样一次足够。
    /// </summary>
    public void ApplyThemeWithBackdrop(AppConfig config)
    {
        var palette = ThemePalette.Create(config);
        if (config.TextTone.Equals("Auto", StringComparison.OrdinalIgnoreCase) && palette.IsGlass && IsLoaded)
        {
            try
            {
                var origin = PointToScreen(new Point(0, 0));
                var dpi = VisualTreeHelper.GetDpi(this);
                var rect = new Rect(
                    origin.X / dpi.DpiScaleX + Pill.Margin.Left,
                    origin.Y / dpi.DpiScaleY + Pill.Margin.Top,
                    Math.Max(1, Pill.ActualWidth),
                    Math.Max(1, Pill.ActualHeight));

                if (BackdropProbe.AverageLuminance(rect) is { } backdrop)
                {
                    palette = ThemePalette.Create(config, backdrop);
                }
            }
            catch
            {
                // 取样失败就用默认调色板
            }
        }

        ApplyTheme(palette);
    }

    /// <summary>
    /// 应用外观主题（v0.2.6 需求 3/4）：替换窗口资源里的画刷，DynamicResource 会立即刷新
    /// 按钮图标、分隔线、文字色；岛体背景与描边直接赋值。
    /// </summary>
    public void ApplyTheme(ThemePalette palette)
    {
        Resources["IslandFg"] = ThemePalette.Brush(palette.TextPrimary);
        Resources["IslandSubFg"] = ThemePalette.Brush(palette.TextSecondary);
        Resources["IslandGlyph"] = ThemePalette.Brush(palette.Glyph);
        Resources["IslandHover"] = ThemePalette.Brush(palette.HoverBackground);
        Resources["IslandPress"] = ThemePalette.Brush(palette.PressBackground);
        Resources["IslandDivider"] = ThemePalette.Brush(palette.Divider);

        Pill.Background = palette.Background;
        Pill.BorderBrush = ThemePalette.Brush(palette.Border);

        HighlightLayer.Background = new LinearGradientBrush(palette.Highlight, Colors.Transparent, 90)
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
    }

    /// <summary>
    /// 更新歌词区（v0.2.8）：<paramref name="current"/> 当前行，
    /// <paramref name="translation"/> 该行的译文（歌词源自带才有的，没有就传 null），
    /// <paramref name="next"/> 下一行。全为 null 表示无歌词（收起歌词区）。
    ///
    /// 行内容变化时做 170ms 淡入 + 轻微上浮，让"换句"看得见而不是硬切。
    /// 宽度不在这里定：交给 <see cref="BalanceWidths"/> 按实测文本算，
    /// 避免"歌词两个字却占 270 宽"这种右侧大片空白的观感。
    /// </summary>
    public void ShowLyrics(string? current, string? next, string? translation = null)
    {
        var line1 = current ?? string.Empty;
        var line2 = next ?? string.Empty;
        var trans = translation ?? string.Empty;

        var changed = !string.Equals(LyricLine1.Text, line1, StringComparison.Ordinal)
                      || !string.Equals(LyricTrans.Text, trans, StringComparison.Ordinal);
        var wasVisible = LyricsPanel.Visibility == Visibility.Visible;

        LyricLine1.Text = line1;
        LyricTrans.Text = trans;
        LyricLine2.Text = line2;

        // 译文行只在真的有译文时占位（中文歌 / 译文与原文相同 → 整行收起，不留空档）
        LyricTrans.Visibility = trans.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 没词 / 关了开关 / 没音乐：一律收起（空面板会把岛撑高一小截）
        var hasLyric = (LyricsEnabledProvider?.Invoke() ?? true)
                       && _musicVisible
                       && line1.Trim().Length > 0;

        LyricsPanel.Visibility = hasLyric ? Visibility.Visible : Visibility.Collapsed;

        // 有词 → 占位让位；没词但左栏还在（播放中或待机展示）→ 用占位撑住"歌词区域留空"
        LyricSpacer.Visibility = hasLyric || !(_musicVisible || _standbyMusicShown)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (hasLyric != wasVisible)
        {
            UpdateChrome();
            Evaluate();
        }
        else if (hasLyric && changed)
        {
            // 只在"本来就显示、内容变了"时做动画：首次展开交给整岛动画，避免叠加发灰
            AnimateLyricSwap();
            UpdateChrome();
        }
    }

    /// <summary>换句动画：淡入 + 从下方 4px 归位（不动布局，只动渲染变换）。</summary>
    private void AnimateLyricSwap()
    {
        LyricsPanel.BeginAnimation(OpacityProperty, null);
        _lyricShift.BeginAnimation(TranslateTransform.YProperty, null);

        LyricsPanel.Opacity = 1;

        var fade = new DoubleAnimation(0.35, 1, LyricSwapDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var rise = new DoubleAnimation(4, 0, LyricSwapDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        LyricsPanel.BeginAnimation(OpacityProperty, fade);
        _lyricShift.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>小岛当前是否可见（诊断用）。</summary>
    public bool IsShowing => !_hidden && IsVisible;

    /// <summary>当前是否吸附在屏幕边缘（诊断用）。</summary>
    public bool IsSnapped => _snapped;

    /// <summary>当前吸附边（诊断用，None/Left/Right/Top/Bottom）。</summary>
    public string SnapEdgeName => _snapEdge.ToString();

    /// <summary>显示“任务执行中”状态（单行实时动作文字）。</summary>
    public void ShowRunning(string text, string? tag = null)
    {
        EnsureCreated();
        SetStatus(text, done: false, tag);
    }

    /// <summary>显示“任务完成”：对勾 + 完成文字，600ms 后 220ms 收缩。</summary>
    public void ShowDone(string text, string? tag = null)
    {
        EnsureCreated();
        SetStatus(text, done: true, tag);
    }

    /// <summary>
    /// 新消息提示（v0.2.7 需求 2）：有新消息时把灵动岛弹出来并显示一条临时状态，
    /// 超时后整条收起。若此刻有真实任务状态在跑，只弹出/续时，不覆盖任务文字。
    /// </summary>
    public void PokeMessage(string source, string sender, string content)
    {
        EnsureCreated();

        if (_statusVisible && !_statusTransient)
        {
            // 有真实任务在跑：只把岛弹出来 + 续时，不动任务文字
            Evaluate();
            RestartAutoCollapse();
            return;
        }

        var who = string.IsNullOrWhiteSpace(sender) ? source : $"{source} · {sender}";
        var line = string.IsNullOrWhiteSpace(content) ? who : $"{who}：{content}";

        SetStatus(line, done: false, tag: source, transient: true);
    }

    /// <summary>外部事件（新任务/新状态）到达时把岛弹出来并重置收纳倒计时。</summary>
    public void PokeActivity()
    {
        Evaluate();
        RestartAutoCollapse();
    }

    // ---------------- 岛内通知（v0.2.9 需求 3） ----------------

    /// <summary>
    /// 在岛内展示一条通知（微信 / QQ / ntfy），用来源主题色区分，整行可点。
    ///
    /// 展示策略由 <see cref="IslandController"/> 决定：音乐与 WorkBuddy 任务同时存在时
    /// 通知仍走四角弹窗，只有"最多一项内容在跑"时才进岛内（此时岛不会有第四行）。
    /// </summary>
    public void ShowNotice(NotifyMessage message)
    {
        EnsureCreated();

        _notice = message;
        _noticeVisible = true;
        _noticeAt = DateTime.Now;

        var accent = SourceColors.AccentOf(message);
        var brush = ThemePalette.Brush(accent);

        NoticeDot.Fill = brush;
        NoticeTag.Foreground = brush;
        NoticeTag.Text = message.SourceLabel is { Length: > 0 } label ? label : message.SourceText;

        var who = string.IsNullOrWhiteSpace(message.Sender) ? message.SourceText : message.Sender;
        var body = string.IsNullOrWhiteSpace(message.Content) ? "（无内容）" : message.Content;
        NoticeText.Text = FlattenToSingleLine($"{who}：{body}");
        NoticeText.Anchor = TextAnchor.Left;

        var wasVisible = NoticeButton.Visibility == Visibility.Visible;
        NoticeButton.Visibility = Visibility.Visible;

        // 内容淡入（首次出现交给整岛展开动画，避免两层叠加发灰）
        if (wasVisible)
        {
            NoticeButton.BeginAnimation(OpacityProperty, null);
            NoticeButton.Opacity = 1;
            NoticeButton.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.4, 1, TextSwapDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        _noticeTimer.Stop();
        _noticeTimer.Start();

        UpdateChrome();
        Evaluate();
        RestartAutoCollapse();
    }

    /// <summary>收起岛内通知行（点击已读 / 到时自动）。</summary>
    public void HideNotice()
    {
        _noticeTimer.Stop();

        if (!_noticeVisible)
        {
            return;
        }

        _noticeVisible = false;
        _notice = null;
        NoticeButton.Visibility = Visibility.Collapsed;

        UpdateChrome();
        Evaluate();
        RestartAutoCollapse();
    }

    private void OnNoticeHoldElapsed()
    {
        _noticeTimer.Stop();
        HideNotice();
    }

    /// <summary>通知行被点击：交给控制器打开对应会话 / 网页，然后收起该行。</summary>
    private void OnNoticeClick(object sender, RoutedEventArgs e)
    {
        var notice = _notice;
        if (notice is null)
        {
            return;
        }

        RestartAutoCollapse();

        try
        {
            NoticeClicked?.Invoke(this, notice);
        }
        catch (Exception ex)
        {
            LogService.Shared.Warn("Island", "岛内通知点击处理失败", ex.Message);
        }

        HideNotice();
    }

    /// <summary>岛内当前是否展示了通知（诊断用）。</summary>
    /// <summary>
    /// 岛"刚变成可见"（从隐藏/窄条展开）时触发（v0.2.10 需求 4）。
    /// 控制器收到后重定歌词行 + 重启歌词滚动：收起期间 UI 不渲染，状态可能停在半路。
    /// </summary>
    public event EventHandler? IslandBecameVisible;

    /// <summary>重启歌词三行的滚动状态（展开时调用，见 <see cref="MarqueeText.RestartScroll"/>）。</summary>
    public void RestartLyricScroll()
    {
        LyricLine1.RestartScroll();
        LyricTrans.RestartScroll();
        LyricLine2.RestartScroll();
    }

    /// <summary>
    /// 是否处于「待机空闲模式」（v0.2.11 需求 2/4）。
    ///
    /// 待机空闲 = 没有音乐播放器在跑、没有 WorkBuddy 在跑、也没有任务 / 播放 / 通知。
    /// **只有这个模式**才有"左右分区悬浮配色 + 双击快捷唤起"；
    /// 一旦进程在线或来了内容，就进入信息展示模式，那些待机交互全部关闭。
    /// </summary>
    /// <summary>
    /// 是否处于「待机空闲模式」（v0.2.14 需求 3/4/5 重新定义）。
    ///
    /// 待机 = **没有正在播放的音乐、没有正在跑的任务、没有通知**。
    /// 播放器/WorkBuddy 进程挂着但没内容，同样算待机（场景 B）——
    /// 超时后一样收纳回待机样式，左右双击唤起入口始终可用（需求 5）。
    /// 只有场景 A（正在播放 / 正在跑任务）才算信息展示，岛才维持展开。
    /// </summary>
    public bool IsIdleStandby
        => !_musicVisible && !_statusVisible && !_noticeVisible;

    /// <summary>
    /// 由控制器推送"进程是否在线"（v0.2.11 需求 2）：进程一开就切信息展示模式，不等播放/任务。
    /// </summary>
    public void SetPresence(bool musicRunning, bool workBuddyRunning, string? musicProcess = null)
    {
        var processChanged = !string.Equals(_presenceMusicProcess, musicProcess, StringComparison.OrdinalIgnoreCase);

        if (_presenceMusic == musicRunning && _presenceWorkbuddy == workBuddyRunning && !processChanged)
        {
            return;
        }

        _presenceMusic = musicRunning;
        _presenceWorkbuddy = workBuddyRunning;
        _presenceMusicProcess = musicProcess;

        LogService.Shared.Routine(
            "IslandMode",
            "进程在线状态更新",
            $"music={musicRunning}({musicProcess ?? "-"}) workbuddy={workBuddyRunning} standby={IsIdleStandby}");

        // 待机展示/音乐组是否显示取决于 presence，所以这边也要重建一次
        UpdateMusicGroup();
        UpdateChrome();
        Evaluate();
    }

    /// <summary>
    /// 进程在线行的显示规则：
    ///   有真实任务 / 通知时让位（它们是更高优先级的信息）；
    ///   只有进程在线、没有内容时，把"谁在线"显示出来 —— 这就是"信息展示状态"的兜底内容。
    /// </summary>
    private void UpdatePresenceRows()
    {
        var hasReal = _statusVisible || _noticeVisible;

        // 需求 3 的左右分裂布局：**播放器归左栏**（图标 + 空歌名 + 三键），
        // 右栏只留 WorkBuddy / 通知，不再有"音乐"这行小黄标签。
        var showWb = !hasReal && _presenceWorkbuddy && !_statusVisible;

        PresenceWbRow.Visibility = showWb ? Visibility.Visible : Visibility.Collapsed;

        if (showWb)
        {
            PresenceWbText.Text = "已打开 · 等待任务";
        }

        _presenceVisible = showWb;
    }

    public bool IsNoticeShown => _noticeVisible;

    /// <summary>当前是否处于「用户刚唤起」的保持期（诊断/自检用）。</summary>
    public bool IsLaunchHoldActive
        => _launchHoldUntilMs > 0 && Environment.TickCount64 < _launchHoldUntilMs;

    /// <summary>
    /// 记一次「用户主动唤起」（v0.2.14 需求 2）：接下来 15 秒保持展开成待机展示态，
    /// 让用户能直接点左栏的播放键控制刚拉起的播放器。
    /// </summary>
    public void NoteUserLaunch()
    {
        _launchHoldUntilMs = Environment.TickCount64 + LaunchHoldMs;
        LogService.Shared.Routine("IslandAuto", "用户唤起保持展开", $"{LaunchHoldMs / 1000}s");
        Evaluate();
    }

    /// <summary>是否处于收纳细条形态（诊断/自检用）。</summary>
    public bool IsStripMode => _stripMode;

    /// <summary>音乐行是否可见（展示策略判断用）。</summary>
    public bool HasMusicContent => _musicVisible;

    /// <summary>任务行是否可见（展示策略判断用）。</summary>
    public bool HasTaskContent => _statusVisible;

    /// <summary>
    /// 音乐与任务是否**同时**在跑（v0.2.9 需求 3 的展示策略开关）：
    /// 两项都在 → 新通知走四角弹窗；否则进岛内。
    /// </summary>
    public bool IsBusyWithBoth => !NoticeGoesToIsland(_musicVisible, _statusVisible);

    /// <summary>
    /// 展示策略的纯函数版（v0.2.9 需求 3）：
    ///   音乐 + WorkBuddy 任务**同时在跑** → 岛内两行已经满了，新通知走四角弹窗；
    ///   都空 / 只有一项在跑        → 岛内还有空间，新通知直接在岛里展示。
    /// 抽成静态纯函数是为了让自检能直接验证策略分支，不用先把窗口跑起来。
    /// </summary>
    public static bool NoticeGoesToIsland(bool hasMusic, bool hasTask) => !(hasMusic && hasTask);

    /// <summary>
    /// 启动即常驻（v0.2.9 需求 2）：立刻亮出顶部窄条，不等任何内容。
    /// 之后由 <see cref="Evaluate"/> 统一接管——有内容就展开，没内容就保持窄条。
    /// </summary>
    public void ShowPersistentStrip()
    {
        EnsureCreated();

        if (!_fullscreen)
        {
            Evaluate();
        }
    }

    /// <summary>
    /// 预热：离屏把 HWND 建出来并跑完一次布局/渲染。
    /// 不预热的话第一条 --island 命令要多等 150~300ms 的窗口创建 + 首帧渲染，
    /// 直接就把"状态更新 &lt;100ms"吃掉；预热之后 Show 基本只是一次合成。
    /// </summary>
    public void WarmUp()
    {
        // 常驻细条形态下岛窗口本来就是隐藏的，再 Show/Hide 一次会把细条挤掉
        if (_stripMode)
        {
            return;
        }

        if (!_hidden || IsVisible)
        {
            return;
        }

        try
        {
            PositionIsland();
            Opacity = 0;
            IslandShift.Y = -10;

            Show();
            UpdateLayout();
            Hide();

            Opacity = 1;
        }
        catch
        {
            // 预热失败不影响真正显示
        }
    }

    /// <summary>供外部（命令行/管道）调用的统一入口。</summary>
    public static void ShowTask(string text, bool done)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("应用未初始化");

        dispatcher.Invoke(() =>
        {
            Current ??= new IslandWindow();

            if (done)
            {
                Current.ShowDone(text);
            }
            else
            {
                Current.ShowRunning(text);
            }
        });
    }

    /// <summary>音乐状态更新（null=无播放会话）。</summary>
    public void SetMedia(MediaInfo? media)
    {
        var wasPlaying = _musicPlaying;
        _media = media;
        _musicPlaying = media is { IsPlaying: true };

        if (_musicPlaying)
        {
            _musicTimer.Stop();
        }
        else if (wasPlaying)
        {
            // 暂停/停止：缓冲 2.2s 再隐藏，避免换曲瞬间闪一下
            _musicTimer.Stop();
            _musicTimer.Interval = MusicHideGrace;
            _musicTimer.Start();
        }

        UpdateMusicGroup();
        Evaluate();
    }

    /// <summary>全屏状态变化（观影/游戏全屏 → 整岛隐藏，退出 → 恢复）。</summary>
    public void SetFullscreen(bool fullscreen)
    {
        _fullscreen = fullscreen;
        Evaluate();
    }

    /// <summary>配置变化（模式切换/保存）后调用：立即按新模式校正位置。</summary>
    public void ApplyModeChange()
    {
        var free = IsFreeMode();
        Pill.Cursor = free ? Cursors.SizeAll : Cursors.Arrow;

        if (!free)
        {
            // 切回固定模式：清掉吸附/细条状态，回到顶部居中
            _snapped = false;
            _stripMode = false;
            _strip?.Collapse();
            _snapEdge = Edge.None;
            _autoTimer.Stop();
        }

        if (!_hidden && IsVisible && !_dragging && !_sliding)
        {
            PositionIsland();
        }

        if (!_hidden)
        {
            RestartAutoCollapse();
        }
    }

    // ---------------- 状态文字 ----------------

    private void SetStatus(string text, bool done, string? tag = null, bool transient = false)
    {
        var fallback = done ? "完成" : "正在执行任务…";
        var display = string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

        // 只许一行：任何换行/连续空白都压平，超长交给跑马灯
        display = FlattenToSingleLine(display);

        // 去重（v0.2.8）：正文常以来源名开头（"WorkBuddy 正在梳理…"），而左侧标签已经写了
        // 来源，再重复一遍既占宽度又啰嗦 —— 剥掉，但剥完为空（正文就是来源名）则保留原样。
        var sourceTag = string.IsNullOrWhiteSpace(tag) ? "WorkBuddy" : tag.Trim();
        if (display.Length > sourceTag.Length + 1
            && display.StartsWith(sourceTag, StringComparison.OrdinalIgnoreCase))
        {
            var rest = display[sourceTag.Length..].TrimStart(' ', '-', '·', ':', '：', '｜', '|');
            if (rest.Length > 0)
            {
                display = rest;
            }
        }

        var changed = !string.Equals(StatusText.Text, display, StringComparison.Ordinal);
        var firstShow = _hidden || !_statusVisible;

        StatusText.Text = display;

        // 标签：执行中显示来源（WorkBuddy / 微信 …），完成态追加“· 完成”，配绿勾更易读
        var label = string.IsNullOrWhiteSpace(tag) ? "WorkBuddy" : tag.Trim();
        StatusTag.Text = done ? $"{label} · 完成" : label;
        StatusTag.Opacity = done ? 1 : 0.85;
        _statusTransient = transient;

        // 文字切换 120ms 淡入；首次出现交给整条展开动画，避免两层叠加发灰
        if (changed && !firstShow)
        {
            var fade = new DoubleAnimation(0.35, 1, TextSwapDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            StatusText.BeginAnimation(OpacityProperty, fade);
        }
        else if (firstShow)
        {
            StatusText.BeginAnimation(OpacityProperty, null);
            StatusText.Opacity = 1;
        }

        StatusDot.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        CheckGlyph.Visibility = done ? Visibility.Visible : Visibility.Collapsed;

        if (done)
        {
            StopBreathing();
        }
        else
        {
            StartBreathing();
        }

        _statusVisible = true;
        StatusGroup.Visibility = Visibility.Visible;
        UpdateChrome();

        // 完成：停留 600ms →（提示音）→ 220ms 收缩；执行中：5 分钟兜底
        _statusTimer.Stop();
        _statusTimer.Interval = done ? DoneHold : RunTimeout;
        _statusTimer.Start();

        if (done)
        {
            TaskDoneShown?.Invoke(this, EventArgs.Empty);
        }

        Evaluate();

        // v0.2.7 需求 2：无论有没有任务在跑，都按用户设定的时长统一收纳
        RestartAutoCollapse();
    }

    private static string FlattenToSingleLine(string text)
    {
        var single = text.Replace("\r", " ").Replace('\n', ' ').Trim();

        while (single.Contains("  ", StringComparison.Ordinal))
        {
            single = single.Replace("  ", " ", StringComparison.Ordinal);
        }

        return single;
    }

    private void UpdateMusicGroup()
    {
        var enabled = MusicEnabledProvider?.Invoke() ?? true;

        // v0.2.14 需求 3/4：**只有"正在播放"才算信息展示内容**（场景 A）。
        // 播放器进程挂着但没在放歌 = 场景 B，超时后一样收纳回待机样式。
        _musicVisible = enabled && (_musicPlaying || _musicTimer.IsEnabled);

        // 场景 B 的左栏形态（v0.2.12 引入、v0.2.14 保留为"待机展示"）：
        // 播放器进程在跑但没在放歌时，左栏仍然显示「图标 + 空歌名区 + 三个播放键」，
        // 用户一眼知道是哪个播放器、并且能直接点播放；但它**不算内容**，
        // 不会阻止岛超时收纳，也不会让岛退出待机模式。
        _standbyMusicShown = enabled && !_musicVisible && _presenceMusic;

        MusicGroup.Visibility = _musicVisible || _standbyMusicShown ? Visibility.Visible : Visibility.Collapsed;

        // 音乐收起时歌词必须一起收起（否则会留两行空行把岛撑高）；
        // 恢复播放时按开关 + 是否真的有词重新判定
        var keepLyrics = _musicVisible
                         && (LyricsEnabledProvider?.Invoke() ?? true)
                         && LyricLine1.Text.Trim().Length > 0;

        LyricsPanel.Visibility = keepLyrics ? Visibility.Visible : Visibility.Collapsed;

        // 没有歌词但左栏在（播放中或待机展示）：留一条空白占位，"歌词区域留空" + 左右等高
        LyricSpacer.Visibility = (_musicVisible || _standbyMusicShown) && !keepLyrics
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_media is not null)
        {
            var title = string.IsNullOrWhiteSpace(_media.Title) ? "未知曲目" : _media.Title;
            var artist = string.IsNullOrWhiteSpace(_media.Artist) ? _media.AppName : _media.Artist;
            MusicText.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";
            PlayPauseGlyph.Data = (Geometry)FindResource(_media.IsPlaying ? "GlyphPause" : "GlyphPlay");
            UpdateMusicIcon(_media);
        }
        else if (_musicVisible || _standbyMusicShown)
        {
            // 待机：歌名/歌手**留空**，图标仍然显示 - 用户一眼能认出是哪个播放器
            MusicText.Text = string.Empty;
            MusicText.Width = MusicTitleMinWidth;
            PlayPauseGlyph.Data = (Geometry)FindResource("GlyphPlay");
            UpdateMusicIcon(null);
        }

        UpdateChrome();
    }

    /// <summary>
    /// 岛最左侧的当前播放 App 图标（需求 5）：从进程 exe 提取透明图标，
    /// 圆角由外层 Border 裁剪；提不到图标时回落到 ♪ 字符，无播放时整组收起。
    /// </summary>
    private void UpdateMusicIcon(MediaInfo? media)
    {
        try
        {
            // 没有播放会话时用 presence 巡检到的进程名（需求 3：待机也要保留播放器图标）
            var process = media?.ProcessName;
            if (string.IsNullOrWhiteSpace(process))
            {
                process = _presenceMusicProcess;
            }

            var icon = string.IsNullOrWhiteSpace(process) ? null : AppIconExtractor.GetIcon(process!);
            if (icon is not null)
            {
                MusicAppIcon.Source = icon;
                MusicIconBox.Visibility = Visibility.Visible;
                MusicGlyph.Visibility = Visibility.Collapsed;
                return;
            }
        }
        catch
        {
            // 提图标失败不影响音乐行
        }

        MusicAppIcon.Source = null;
        MusicIconBox.Visibility = Visibility.Collapsed;
        MusicGlyph.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 刷新音乐组可见性：设置页「音乐控制模块」开关切换后立即生效
    /// （需求 8：勾选→显示、取消→隐藏，此前只有保存后不刷新才"不消失"）。
    /// </summary>
    public void RefreshMedia()
    {
        MusicText.Speed = MarqueeSpeedProvider?.Invoke() ?? MarqueeText.DefaultSpeed;
        StatusText.Speed = MarqueeSpeedProvider?.Invoke() ?? MarqueeText.DefaultSpeed;
        UpdateMusicGroup();
        Evaluate();
    }

    /// <summary>分隔线只在音乐与任务两组同时可见时出现；歌词随音乐组一起出现。</summary>
    private void UpdateChrome()
    {
        // 真实任务/通知出现 → 进程在线占位行让位（v0.2.11 需求 2）
        UpdatePresenceRows();

        // 左栏（播放器）与右栏（任务/通知/WorkBuddy 在线）同时有东西 → 画竖分隔线。
        // 待机展示态也算：需求 3 要的是"左右分裂"，有分界线一眼就看得出两栏
        var leftShown = _musicVisible || _standbyMusicShown;
        var rightHasContent = _statusVisible || _noticeVisible || _presenceVisible;
        Divider.Visibility = leftShown && rightHasContent
            ? Visibility.Visible
            : Visibility.Collapsed;
        BalanceWidths();
        SyncStripState();

        // 内容变了 → 岛宽会在随后的布局趟里变 → 必须在布局落定后重新居中一次，
        // 否则窗口会从旧的 Left 往右/往左长，看起来就是"内容偏右/偏左"（v0.2.10 需求 1）。
        ScheduleRecenter();
    }

    /// <summary>
    /// 合并式重新居中（v0.2.10 需求 1）：同一个布局周期里可能被连调多次
    /// （ShowNotice → UpdateChrome → Evaluate → …），只排一次就够。
    ///
    /// 为什么不能用 SizeChanged 兜住：ExpandFromStrip / ShowIsland 期间 `_hidden` 还是 true，
    /// SizeChanged 会早退；等 `_hidden` 变 false 时尺寸已经稳定，不再派发 SizeChanged ——
    /// 于是位置永远停在展开前的旧 Left 上，岛越宽偏得越多。
    /// 这里用 Loaded 优先级（布局跑完之后）补一刀，并且只在真的偏了才写 Left。
    /// </summary>
    private void ScheduleRecenter()
    {
        if (_recenterScheduled)
        {
            return;
        }

        _recenterScheduled = true;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _recenterScheduled = false;

                if (_hidden || _dragging || _sliding || !IsLoaded || _stripMode)
                {
                    return;
                }

                if (IsFreeMode())
                {
                    if (_snapped)
                    {
                        ReapplySnap();
                    }

                    return;
                }

                var work = WorkArea();
                var target = CenteredLeft(work, ActualWidth > 1 ? ActualWidth : 420);
                if (double.IsNaN(Left) || Math.Abs(Left - target) > 0.5)
                {
                    Left = target;
                }
            }),
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 收纳细条当前应该显示哪种形态（v0.2.8 需求 4 + v0.2.9 需求 3）：
    /// 仅音乐 / 仅任务 / 音乐+任务 靠轨道里光块的颜色与运动方式区分；
    /// 岛内有通知时优先显示「通知」形态，光块用该来源的主题色。
    /// </summary>
    private StripMode CurrentStripMode()
    {
        if (_noticeVisible)
        {
            return StripMode.Notice;
        }

        if (_musicVisible && _statusVisible)
        {
            return StripMode.Both;
        }

        if (_musicVisible)
        {
            return StripMode.Music;
        }

        return _statusVisible ? StripMode.Task : StripMode.None;
    }

    /// <summary>细条要用的强调色（通知的来源色；没有通知返回 null 用内置色）。</summary>
    private Color? CurrentAccentColor()
        => _noticeVisible && _notice is not null ? SourceColors.AccentOf(_notice) : null;

    /// <summary>细条已显示时，随内容变化刷新指示器（音乐停、任务开始/完成…）。</summary>
    private void SyncStripState()
    {
        if (!_stripMode || _strip is null)
        {
            return;
        }

        _strip.UpdateState(CurrentStripMode(), IsDoneState, CurrentAccentColor());

        // 待机快捷模式 vs 信息展示模式（v0.2.11 需求 2/4）：
        // 只有待机空闲时才有"左右分区配色 + 双击唤起"，其余情况窄条只负责展示状态。
        SyncStripMode();
    }

    /// <summary>把当前的"待机 / 信息"模式推给窄条（窄条可能刚创建，需要主动纠一次）。</summary>
    private void SyncStripMode() => _strip?.SetStandbyMode(IsIdleStandby);

    /// <summary>
    /// 左右两栏宽度配平 + 自适应岛宽（v0.2.8 需求 5 的核心）。
    ///
    /// 旧版是把歌词写死 270、任务写死 262/360：歌词短的时候右侧留一大片黑，
    /// 岛却按「270 + 分隔线 + 262」的固定宽度居中，观感就是**明显靠左、右边空**。
    ///
    /// 现在一切按**文本实测宽度**推：
    ///   · 歌名区：clamp(实测, 104, 有任务 200 / 无任务 280)
    ///   · 歌词区：clamp(三行里最宽的一行, 150, 有任务 330 / 无任务 560)
    ///   · 任务区：clamp(实测, 160, 有音乐 330 / 无音乐 460)
    /// 内容短 → 岛自然收窄（居中后两侧留白对称）；歌词长 → 岛适度拉长（上限 560）。
    /// 只有任务没有音乐时不再预留左侧空位（左栏直接 Collapsed），所以任务区独占整条。
    /// </summary>
    private void BalanceWidths()
    {
        // v0.2.9 需求 6 + v0.2.10 需求 1：
        //   widthFactor：整体宽度上限的乘数（60%~160%）
        //   left/rightFactor：按左右分配比例放大或收缩两侧的上限 —— **只在左右两侧都有内容时生效**。
        // 需求 1 明确：岛内只有一项内容时"整体自动居中"，不能被比例滑块把可用宽度砍掉一半
        // （比例 25 时把通知区上限压到 50%，长消息会被莫名其妙截断，观感还是"偏一边"）。
        var widthFactor = Math.Clamp((IslandWidthPercentProvider?.Invoke() ?? 100) / 100.0, 0.6, 1.6);
        var hasLeft = _musicVisible;
        var hasRight = _statusVisible || _noticeVisible;
        var bothSides = hasLeft && hasRight;
        var (leftFactor, rightFactor) = SplitFactors(IslandSplitRatioProvider?.Invoke() ?? 50, bothSides);

        // 1) 任务正文 + 通知正文（共享同一列宽，视觉上左边界才齐）
        //    宽度一律**向上取整**：FormattedText 的自然宽带小数（172.2），若四舍五入成 172
        //    就比自然宽还窄 0.2px，会被跑马灯判成"超宽"从而把开头几个字滚掉（v0.2.8 实测坑）。
        var statusCap = (_musicVisible ? MaxStatusWidthWithMusic : MaxStatusWidthSolo)
                        * widthFactor * rightFactor;
        var statusNatural = Math.Max(
            StatusText.NaturalWidth,
            Math.Max(NoticeText.NaturalWidth, PresenceWbText.NaturalWidth));

        // 单内容时不要 160 的下限：短消息被撑到 160 会在右侧留下一片空白，
        // 岛本身是居中的，那片空白就把可见文字"推"到左边去（需求 1 的观感来源之一）。
        var statusFloor = bothSides ? MinStatusWidth : 96;

        var statusWidth = Math.Ceiling(Math.Clamp(
            statusNatural <= 0 ? statusFloor : statusNatural,
            statusFloor, Math.Max(statusFloor, statusCap)));

        if (double.IsNaN(StatusText.Width) || Math.Abs(StatusText.Width - statusWidth) > 0.5)
        {
            StatusText.Width = statusWidth;
        }

        if (double.IsNaN(NoticeText.Width) || Math.Abs(NoticeText.Width - statusWidth) > 0.5)
        {
            NoticeText.Width = statusWidth;
        }

        // WorkBuddy 在线行同样按这一列宽（否则固定 200 会把岛撑成别的宽度）
        if (double.IsNaN(PresenceWbText.Width) || Math.Abs(PresenceWbText.Width - statusWidth) > 0.5)
        {
            PresenceWbText.Width = statusWidth;
        }

        if (!_musicVisible)
        {
            // 无音乐：左栏整体收起，任务区独占（不预留任何音乐/歌词空位）
            LeftPane.Visibility = Visibility.Collapsed;
            return;
        }

        LeftPane.Visibility = Visibility.Visible;

        // 2) 歌名区
        var titleCap = (_statusVisible ? MaxTitleWidthWithTask : MaxTitleWidthSolo)
                       * widthFactor * leftFactor;
        var titleNatural = MusicText.NaturalWidth;
        var titleWidth = Math.Ceiling(Math.Clamp(
            titleNatural <= 0 ? MinTitleWidth : titleNatural,
            MinTitleWidth, titleCap));

        if (double.IsNaN(MusicText.Width) || Math.Abs(MusicText.Width - titleWidth) > 0.5)
        {
            MusicText.Width = titleWidth;
        }

        // 3) 歌词区：取三行里最宽的一行（译文/下一句通常更短，不参与决定）
        if (LyricsPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        AlignLyricIndent();

        var lyricCap = (_statusVisible ? MaxLyricWidthWithTask : MaxLyricWidthSolo)
                       * widthFactor * leftFactor;
        var lyricNatural = Math.Max(
            LyricLine1.NaturalWidth,
            Math.Max(LyricTrans.NaturalWidth, LyricLine2.NaturalWidth));

        var lyricWidth = Math.Ceiling(Math.Clamp(
            lyricNatural <= 0 ? MinLyricWidth : lyricNatural,
            MinLyricWidth, lyricCap));

        if (double.IsNaN(LyricLine1.Width) || Math.Abs(LyricLine1.Width - lyricWidth) > 0.5)
        {
            LyricLine1.Width = lyricWidth;
            LyricTrans.Width = lyricWidth;
            LyricLine2.Width = lyricWidth;
        }
    }

    /// <summary>
    /// 左右两侧的宽度系数（v0.2.10 需求 1）。
    ///
    /// 规则只有一句话：**两侧都有内容时才按"区域比例"分配，只有一侧时两边系数都是 1**
    /// ——单内容要把可用宽度全部给那一侧，岛整体才是居中的；如果单内容也套比例，
    /// 比例 25% 时通知区上限会被砍掉一半，长消息被截断，观感就是"内容偏在一边"。
    ///
    /// 抽成纯函数是为了让自检能直接断言这条规则，不用把窗口跑起来。
    /// </summary>
    public static (double Left, double Right) SplitFactors(int splitPercent, bool bothSides)
    {
        if (!bothSides)
        {
            return (1.0, 1.0);
        }

        var split = Math.Clamp(splitPercent / 100.0, 0.25, 0.75);

        // 50 → 1.0 / 1.0；75 → 1.5 / 0.5；25 → 0.5 / 1.5
        return (Math.Clamp(split / 0.5, 0.55, 1.7), Math.Clamp((1 - split) / 0.5, 0.55, 1.7));
    }

    /// <summary>
    /// 歌词块左缩进 = 歌名正文相对内容列的偏移（v0.2.8：让"当前歌词"和"歌名"共用一条左边界）。
    ///
    /// 为什么运行时算：歌名左边的装饰是「App 图标 17+7」或「♪ 字形 + 间距」，两者宽度不同；
    /// 写死一个数值在换用 ♪ 兜底时会差 10px，看起来就是"歌词缩进错了"。
    /// 带 0.5px 容差，不会因为反复布局而抖动。
    /// </summary>
    private void AlignLyricIndent()
    {
        if (MusicGroup.ActualWidth <= 1 || MusicText.ActualWidth <= 1)
        {
            return;
        }

        try
        {
            var paneLeft = MusicGroup.TranslatePoint(new Point(0, 0), LeftPane).X;
            var titleLeft = MusicText.TranslatePoint(new Point(0, 0), LeftPane).X;
            var indent = Math.Max(0, Math.Round(titleLeft - paneLeft));

            if (indent > 0 && Math.Abs(LyricsPanel.Margin.Left - indent) > 0.5)
            {
                LyricsPanel.Margin = new Thickness(indent, LyricsPanel.Margin.Top, 0, LyricsPanel.Margin.Bottom);
            }
        }
        catch
        {
            // 布局尚未完成时 TranslatePoint 可能抛，忽略即可（下一帧会再对齐）
        }
    }

    private void OnMusicGraceElapsed()
    {
        _musicTimer.Stop();
        UpdateMusicGroup();
        Evaluate();
        RestartAutoCollapse();
    }

    private void OnStatusHoldElapsed()
    {
        _statusTimer.Stop();

        if (!_statusVisible)
        {
            return;
        }

        _statusVisible = false;
        _statusTransient = false;
        StopBreathing();

        // 内容到期后重新计时（此时多半已经没有内容，计时器会在 Evaluate 后自行停掉）
        RestartAutoCollapse();

        if (_musicVisible)
        {
            // 还有音乐：只把任务组淡出（220ms），整岛继续保留
            var fade = new DoubleAnimation(1, 0, CollapseDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            fade.Completed += (_, _) =>
            {
                StatusGroup.Visibility = Visibility.Collapsed;
                StatusGroup.BeginAnimation(OpacityProperty, null);
                StatusGroup.Opacity = 1;
                UpdateChrome();
            };
            StatusGroup.BeginAnimation(OpacityProperty, fade);
            UpdateChrome();
            return;
        }

        StatusGroup.Visibility = Visibility.Collapsed;
        UpdateChrome();
        Evaluate();
    }

    // ---------------- 显隐调度 ----------------

    /// <summary>
    /// 显隐调度（v0.2.9 需求 2 改造）：**岛常驻**。
    ///
    /// 旧逻辑：没有任务也没有音乐 → <see cref="CollapseIsland"/> 把窗口整个 Hide()，
    /// 桌面上什么都不剩，用户以为程序没跑。
    /// 现在：只要不在全屏，任何状态下都至少留一条顶部窄条；
    /// 有音乐 / 任务 / 通知 / 手动展开时展开成完整岛，超时再收成窄条。
    /// </summary>
    private void Evaluate()
    {
        // 全屏（观影/游戏）：整岛与细条一起隐藏，退出全屏自动回来
        if (_fullscreen)
        {
            HideEverything();
            return;
        }

        // v0.2.10 需求 2：**不允许"没有任何内容却展开"**。
        // 旧版把 _idleExpanded 也算成内容，于是空岛被 Show 出来：所有栏都 Collapsed，
        // 宽度塌到 40 左右、Pill 圆角 22 → 屏幕上就是一个竖立的白色/浅灰椭圆（用户截图里的那个）。
        // 待机态的正确形态只有一种：顶部窄条。
        var hasContent = _statusVisible || _musicVisible || _noticeVisible || _presenceVisible
                         || IsLaunchHoldActive;

        if (hasContent)
        {
            CancelCollapse();
            ShowIsland();
            return;
        }

        CollapseToStrip();
    }

    /// <summary>全屏时把岛和细条都收掉（不销毁，退出全屏能立刻回来）。</summary>
    private void HideEverything()
    {
        _autoTimer.Stop();

        if (_stripMode)
        {
            _stripMode = false;
            _strip?.Collapse();
        }

        if (!_hidden && !_dragging)
        {
            CollapseIsland();
        }
    }

    /// <summary>
    /// 收成顶部窄条（v0.2.9 需求 2 的常驻形态）。
    /// 三种入口：岛还隐着（启动/全屏恢复）→ 直接显示细条；
    /// 已经是细条 → 只刷新指示器；岛正展开 → 滑到顶再切换。
    /// </summary>
    private void CollapseToStrip()
    {
        if (_hidden || !IsVisible)
        {
            _autoTimer.Stop();
            _stripMode = true;
            _stripShownAtMs = Environment.TickCount64;
            EnsureStrip().ShowFor(CurrentStripMode(), IsDoneState, CurrentAccentColor());

            // 刚创建出来的细条默认是"待机快捷模式"，必须立刻按当前状态纠正，
            // 否则信息展示模式下也会出现左右分区的待机配色（实测踩到）
            SyncStripMode();
            return;
        }

        if (_stripMode)
        {
            _strip?.UpdateState(CurrentStripMode(), IsDoneState);
            return;
        }

        if (_dragging || _sliding)
        {
            return;
        }

        SnapToNearestEdge();
    }

    /// <summary>展开：180ms ease-out（淡入 + 自上而下落位）。已显示则只做内容更新。</summary>
    private void ShowIsland()
    {
        // 吸附细条形态下来了新内容：先展开成完整岛
        if (_stripMode)
        {
            ExpandFromStrip();
            return;
        }

        var token = ++_animToken;
        _collapsing = false;

        var wasHidden = _hidden || !IsVisible;
        _hidden = false;

        if (wasHidden)
        {
            _snapped = false;
            PositionIsland();
            Show();
            UpdateLayout();

            if (!IsFreeMode())
            {
                // SizeToContent 后宽度可能变化，固定模式再校正一次居中
                PositionIsland();
            }

            // 从隐藏态出现：让外部重定歌词行 / 重启滚动（v0.2.10 需求 4）
            RaiseIslandBecameVisible();
            SetHoverWatch(true);
        }

        // 内容变化引起的宽度微调交给 SizeChanged 保持中心，这里不再干预

        // 淡入走「窗口级 Opacity」（系统分层窗口 alpha，不重绘内容），
        // 比动画 Pill 子树快得多——弹出掉帧就是子树透明度逐帧全量重排导致的
        BeginAnimation(OpacityProperty, null);
        IslandShift.BeginAnimation(TranslateTransform.YProperty, null);

        var startOpacity = wasHidden ? 0d : Opacity;
        // 从隐藏态出现时清掉残留位移；已显示时保持当前位置
        var startY = wasHidden ? -10d : IslandShift.Y;

        var fade = new DoubleAnimation(startOpacity, 1, ExpandDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        BeginAnimation(OpacityProperty, fade);

        var drop = new DoubleAnimation(startY, 0, ExpandDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        drop.Completed += (_, _) =>
        {
            if (token != _animToken)
            {
                return;
            }

            IslandShift.BeginAnimation(TranslateTransform.YProperty, null);
            IslandShift.Y = 0;
        };
        IslandShift.BeginAnimation(TranslateTransform.YProperty, drop);

        // v0.2.7 需求 2：出现即开始收纳倒计时（固定/自由模式一视同仁）
        RestartAutoCollapse();
    }

    /// <summary>收缩消失：220ms ease-in（淡出 + 微微上飘），动画确实结束后才 Hide。</summary>
    private void CollapseIsland()
    {
        if (_hidden || _collapsing)
        {
            return;
        }

        _collapsing = true;
        _autoTimer.Stop();
        _stripMode = false;
        _strip?.Collapse();
        StopBreathing();
        StopSlide();

        var token = ++_animToken;

        BeginAnimation(OpacityProperty, null);
        IslandShift.BeginAnimation(TranslateTransform.YProperty, null);

        var fade = new DoubleAnimation(Opacity, 0, CollapseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };

        var lift = new DoubleAnimation(IslandShift.Y, -8, CollapseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };

        fade.Completed += (_, _) =>
        {
            // 期间又来新状态（token 变了）→ 不能把窗口藏掉，否则就是"闪一下就没了"
            if (token != _animToken || !_collapsing)
            {
                return;
            }

            _collapsing = false;
            _hidden = true;
            _snapped = false;
            _snapEdge = Edge.None;

            BeginAnimation(OpacityProperty, null);
            IslandShift.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1;
            IslandShift.Y = -10;

            if (!_statusVisible)
            {
                StatusGroup.Visibility = Visibility.Collapsed;
            }

            if (!_musicVisible)
            {
                MusicGroup.Visibility = Visibility.Collapsed;
            }

            if (!_noticeVisible)
            {
                NoticeButton.Visibility = Visibility.Collapsed;
            }

            Hide();
            Hidden?.Invoke(this, EventArgs.Empty);
        };

        BeginAnimation(OpacityProperty, fade);
        IslandShift.BeginAnimation(TranslateTransform.YProperty, lift);
    }

    /// <summary>收起动画跑到一半又要显示：把动画抽走，停在当前可见度继续展开，不留半透明残影。</summary>
    private void CancelCollapse()
    {
        if (!_collapsing)
        {
            return;
        }

        _collapsing = false;
        _animToken++;

        var opacity = Opacity;
        BeginAnimation(OpacityProperty, null);
        Opacity = Math.Max(opacity, 0.15);

        IslandShift.BeginAnimation(TranslateTransform.YProperty, null);
    }

    // ---------------- 位置 / 尺寸 ----------------

    private bool IsFreeMode() => ModeProvider?.Invoke() == "Free";

    private double PillPad => Pill.Margin.Left;

    private void OnIslandSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hidden || !IsLoaded || _dragging || _sliding)
        {
            return;
        }

        // 首次布局拿到真实宽度后，重算左右两栏配平（内部有差值判断，不会反复触发布局）
        BalanceWidths();

        if (!IsFreeMode())
        {
            RecenterFixed();
            return;
        }

        // 自由模式：宽度/高度变化时保持视觉中心，别让整条向右下"窜"
        var deltaW = ActualWidth - e.PreviousSize.Width;
        var deltaH = ActualHeight - e.PreviousSize.Height;

        if (double.IsNaN(deltaW) || double.IsNaN(deltaH) || e.PreviousSize.Width < 1)
        {
            return;
        }

        // 吸附态：文字变长会把条体撑出屏幕，必须重新贴回那条边
        if (_snapped)
        {
            ReapplySnap();
            return;
        }

        if (Math.Abs(deltaW) > 0.5)
        {
            Left -= deltaW / 2;
        }

        if (Math.Abs(deltaH) > 0.5)
        {
            Top -= deltaH / 2;
        }

        // v0.2.7：宽度变化（歌词展开/任务文字变长）后仍要保证整条留在屏内
        var (clampedLeft, clampedTop) = ClampToScreen(Left, Top);
        Left = clampedLeft;
        Top = clampedTop;
    }

    /// <summary>保持吸附边不变，按新的条体尺寸重新贴边（文字变长/变短时用）。</summary>
    private void ReapplySnap()
    {
        // v0.2.3 起只吸附顶部：水平位置按中间 3/5 重新收敛，垂直贴顶
        var (left, _) = ClampToScreen(Left, Top);
        var work = WorkArea();
        Left = left;
        Top = work.Top + EdgeGap - PillPad;
    }

    /// <summary>
    /// 固定模式：**按最终尺寸真居中**、贴顶（视觉缝隙 = EdgeGap）。
    ///
    /// v0.2.8 需求 5：以前这里读 SystemParameters.WorkArea（可能是缓存的旧 DPI），
    /// 且只在 Show 前后各调一次——SizeToContent 让宽度在随后几帧才稳定，
    /// 结果就是「只有播放器+歌词时岛偏左、右边一大片空白」。
    /// 现在：工作区实时取（ScreenMetrics），并且 SizeChanged 每帧都会再纠一次偏。
    /// </summary>
    private void RecenterFixed()
    {
        var work = WorkArea();
        var w = ActualWidth > 1 ? ActualWidth : 420;
        Left = CenteredLeft(work, w);
        Top = Math.Round(work.Top + EdgeGap - PillPad);
    }

    /// <summary>
    /// 固定模式水平居中的窗口左边界（DIP）。抽成纯函数便于自检：
    /// 断言「左右两侧留白相等」，这是需求 5 里"不能明显靠左"的硬指标。
    /// </summary>
    public static double CenteredLeft(Rect work, double width)
        => Math.Round(work.Left + ((work.Width - width) / 2));

    /// <summary>主屏工作区（DIP，实时 DPI；不再用会过期的 SystemParameters.WorkArea）。</summary>
    private Rect WorkArea() => ScreenMetrics.PrimaryWorkAreaDip(this);

    /// <summary>按模式定位：Free=已保存位置（有效时），否则顶部居中。</summary>
    private void PositionIsland()
    {
        if (IsFreeMode())
        {
            var saved = SavedPositionProvider?.Invoke();

            if (saved is { } pos)
            {
                var (left, top) = ClampToScreen(pos.Left, pos.Top);
                Left = left;
                Top = top;
                return;
            }
        }

        RecenterFixed();
    }

    /// <summary>
    /// 把位置限制在允许的停靠区内（需求 4）：
    ///   垂直：只允许顶部一带（阴影留白可轻微出屏，不允许拖到屏幕中下部）；
    ///   水平：仅限中间 3/5，最左 1/5 与最右 1/5 禁停；
    ///   以"岛中心点"判定分区，长短不一的岛行为一致。
    /// v0.2.7 附加硬约束：条体必须完整留在屏内（只有阴影留白允许出界）——
    /// 两栏布局让岛变宽后，光靠"中心点分区"会让右侧伸出屏幕外。
    /// 固定模式钉死顶部正中间；自由模式保存的位置同样按此收敛。
    /// </summary>
    private (double Left, double Top) ClampToScreen(double left, double top)
    {
        var area = WorkArea();
        var w = ActualWidth > 1 ? ActualWidth : Width;
        var h = ActualHeight > 1 ? ActualHeight : 76;
        var slack = PillPad + 2;

        var zoneInset = area.Width * 0.2;
        var zoneMin = area.Left + zoneInset - (w / 2);
        var zoneMax = area.Right - zoneInset - (w / 2);

        // 整条可见：左边缘最多到屏幕左沿（可再出一点点留给阴影），右边缘不越过屏幕右沿
        var fitMin = area.Left - slack;
        var fitMax = Math.Max(fitMin, area.Right - w + slack);

        var minLeft = Math.Max(zoneMin, fitMin);
        var maxLeft = Math.Min(zoneMax, fitMax);

        if (maxLeft < minLeft)
        {
            // 屏太窄、两条约束打架时，以"完整可见"为准
            minLeft = fitMin;
            maxLeft = fitMax;
        }

        var minTop = area.Top - slack;
        var maxTop = area.Top + 120;

        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }

    // ---------------- 超时自动收纳（v0.2.7 需求 2：单一计时器） ----------------

    /// <summary>
    /// 重置收纳倒计时。凡是"有新东西"的时机都调它：新任务、状态更新、新消息、音乐变化、
    /// 从细条展开、鼠标离开、拖拽结束、配置变更。
    /// 总开关关闭时直接不启动（岛会一直展开，直到内容自己过期）。
    /// </summary>
    private void RestartAutoCollapse()
    {
        _autoTimer.Stop();

        if (!AutoCollapseEnabled())
        {
            return;
        }

        // 已经整岛收起（既没有岛也没有细条）：没有可收纳的东西
        if (_hidden && !_stripMode)
        {
            return;
        }

        // 拖拽中不打扰，松手后 EndDrag 会重新计时
        if (_dragging)
        {
            return;
        }

        // 通知行在岛上时至少给够它自己的停留时长（默认 6s），
        // 否则用户把收纳时长设成 1~3 秒时，通知刚弹出来就跟着岛一起没了，根本来不及点。
        var seconds = (double)AutoCollapseSeconds();
        if (_noticeVisible)
        {
            seconds = Math.Max(seconds, NoticeHoldMs / 1000.0);
        }

        _autoTimer.Interval = TimeSpan.FromSeconds(seconds);
        _autoTimer.Start();

        // 这是**最典型的高频流水**：媒体轮询/指针巡检每秒都会走到这里。
        // 走 Routine，默认不落盘（v0.2.12 需求 5）。
        LogService.Shared.Routine(
            "IslandAuto",
            $"收纳倒计时重置 {seconds:F0}s",
            $"hidden={_hidden} strip={_stripMode} hover={_pointerOverIsland} music={_musicPlaying} status={_statusVisible} notice={_noticeVisible}");
    }

    /// <summary>
    /// 倒计时结束：统一收成屏幕顶部细条。
    /// 三条豁免——鼠标停在岛上（用户正在看）、完成态（有自己 600ms 的收尾节奏）、
    /// 音乐正在播放且用户开启了"播放时保持展开"（否则永远看不到歌词）。
    /// </summary>
    private void OnAutoCollapseElapsed()
    {
        _autoTimer.Stop();

        LogService.Shared.Routine(
            "IslandAuto", "收纳计时到点",
            $"hidden={_hidden} drag={_dragging} sliding={_sliding} strip={_stripMode} " +
            $"hover={Pill.IsMouseOver} done={IsDoneState} transient={_statusTransient} " +
            $"music={_musicPlaying} keepOpen={KeepOpenWhilePlaying()}");

        if (_hidden || _dragging || _sliding || _stripMode)
        {
            return;
        }

        if (_pointerOverIsland || Pill.IsMouseOver)
        {
            RestartAutoCollapse();
            return;
        }

        // 用户刚双击唤起了播放器/WorkBuddy：这段期间保持展开，别让按钮一闪就没
        if (IsLaunchHoldActive)
        {
            RestartAutoCollapse();
            return;
        }

        if (IsDoneState)
        {
            return;
        }

        // 临时的新消息提示：超时后先撤掉提示文字（有音乐则继续显示音乐，不留残影）
        if (_statusTransient)
        {
            _statusTransient = false;
            _statusVisible = false;
            _statusTimer.Stop();
            StopBreathing();
            StatusGroup.Visibility = Visibility.Collapsed;
            UpdateChrome();
            Evaluate();
            RestartAutoCollapse();
            return;
        }

        // 通知行到点先收通知行（音乐/任务还在时岛继续留着）
        if (_noticeVisible)
        {
            HideNotice();
            return;
        }

        // 音乐正在播且允许保持展开 → 一直显示歌词
        if (_musicPlaying && _musicVisible && KeepOpenWhilePlaying())
        {
            return;
        }

        // 空闲手动展开的时间到了：撤销撑住标志，正常收成细条

        CollapseToStrip();
    }

    private void OnPillMouseEnter(object sender, MouseEventArgs e)
    {
        // 鼠标进入：暂停收纳倒计时（正在看歌词 / 准备点按钮）
        _autoTimer.Stop();

        // v0.2.10 需求 3：悬停反馈只做"整岛轻微放大"（≈2.5%，回弹），不再叠任何遮罩/变色。
        // 放大走 RenderTransform（不参与布局），窗口尺寸与居中都不受影响；
        // Pill 四周有 14px 留白，放大后也不会被窗口边界裁掉。
        AnimateHoverScale(1.025, spring: true);
    }

    private void OnPillMouseLeave(object sender, MouseEventArgs e)
    {
        AnimateHoverScale(1.0, spring: false);

        if (_dragging || _sliding)
        {
            return;
        }

        RestartAutoCollapse();
    }

    /// <summary>开始/停止指针巡检（只在岛可见时跑，避免常驻空转）。</summary>
    private void SetHoverWatch(bool on)
    {
        if (on)
        {
            _hoverWatch ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            if (_hoverWatch.IsEnabled)
            {
                return;
            }

            _hoverWatch.Tick -= OnHoverWatchTick;
            _hoverWatch.Tick += OnHoverWatchTick;
            _hoverWatch.Start();
            return;
        }

        if (_hoverWatch is { IsEnabled: true })
        {
            _hoverWatch.Stop();
        }

        if (_pointerOverIsland)
        {
            _pointerOverIsland = false;
            AnimateHoverScale(1.0, spring: false);
        }
    }

    private void OnHoverWatchTick(object? sender, EventArgs e)
    {
        if (_hidden || _stripMode)
        {
            SetHoverWatch(false);
            return;
        }

        var inside = PointerInsideIsland();
        if (inside == _pointerOverIsland)
        {
            return;
        }

        _pointerOverIsland = inside;

        // 进入时暂停收纳倒计时（用户正在看），离开时重新计时
        if (inside)
        {
            _autoTimer.Stop();
        }
        else if (!_dragging && !_sliding)
        {
            RestartAutoCollapse();
        }

        AnimateHoverScale(inside ? 1.025 : 1.0, spring: inside);
    }

    /// <summary>光标是否落在岛窗口矩形内（物理像素比较，避免 DPI 换算误差）。</summary>
    private bool PointerInsideIsland()
    {
        try
        {
            if (!IsLoaded || ActualWidth < 1 || ActualHeight < 1)
            {
                return false;
            }

            if (!NativeMethods.GetCursorPos(out var point))
            {
                return false;
            }

            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0.1)
            {
                scale = 1;
            }

            var left = Left * scale;
            var top = Top * scale;
            var right = left + (ActualWidth * scale);
            var bottom = top + (ActualHeight * scale);

            return point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>整岛悬停缩放（v0.2.10 需求 3）：进入带回弹、离开平稳收，幅度 1.025。</summary>
    private void AnimateHoverScale(double target, bool spring)
    {
        var duration = TimeSpan.FromMilliseconds(spring ? 190 : 160);
        var ease = spring
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
            : (IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseOut };

        var sx = new DoubleAnimation(target, duration) { EasingFunction = ease };
        var sy = new DoubleAnimation(target, duration) { EasingFunction = ease };

        PillHoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        PillHoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    /// <summary>
    /// 吸附到顶部边缘（v0.2.3：只允许顶部停靠）。
    /// 水平位置保留用户拖到的落点（收敛进中间 3/5），垂直贴顶，落定后收成细条。
    /// </summary>
    private void SnapToNearestEdge()
    {
        var area = WorkArea();
        var pad = PillPad;

        var (targetLeft, _) = ClampToScreen(Left, Top);
        var targetTop = area.Top + EdgeGap - pad;

        _snapEdge = Edge.Top;
        _snapped = true;

        // 先顺滑滑到顶部，落定后整岛收成细条
        SlideTo(targetLeft, targetTop, SlideDurationMs, SwitchToStrip);
    }

    /// <summary>滑到顶后：岛快速淡出，屏幕顶部只留一条横细条。</summary>
    private void SwitchToStrip()
    {
        if (_stripMode || _dragging)
        {
            return;
        }

        _stripMode = true;
        _stripShownAtMs = Environment.TickCount64;
        EnsureStrip().ShowFor(CurrentStripMode(), IsDoneState, CurrentAccentColor());
        SyncStripMode();

        var token = ++_animToken;
        BeginAnimation(OpacityProperty, null);

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if (token != _animToken || !_stripMode)
            {
                return;
            }

            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Hide();
            Opacity = 1; // 下次展开从 0 起手（窗口级淡入）
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>细条被移入/点击（或吸附期间来了新状态）：整岛重新展开，超时后再次收纳。</summary>
    private void ExpandFromStrip()
    {
        if (!_stripMode)
        {
            return;
        }

        // 刚收起来就被"展开"（细条恰好落在指针下、或吸附动画尾帧的用户输入）会变成抖动，
        // 400ms 内直接忽略。
        if (Environment.TickCount64 - _stripShownAtMs < 400)
        {
            return;
        }

        // 没有任何内容就不展开（v0.2.10 需求 2）：待机态只有窄条，
        // 展开一个空壳只会在屏幕上画出一个竖椭圆。
        if (!_statusVisible && !_musicVisible && !_noticeVisible && !_presenceVisible)
        {
            LogService.Shared.Routine("IslandAuto", "无内容，忽略展开请求", "只保留窄条");
            return;
        }

        LogService.Shared.Routine("IslandAuto", "从细条展开", $"status={_statusVisible} music={_musicPlaying} presence={_presenceVisible}");

        var token = ++_animToken;
        _stripMode = false;
        _strip?.Collapse();
        _snapped = false;

        // 先落 _hidden，让随后的 SizeChanged / 布局回调能正常参与居中
        _hidden = false;

        // 贴顶：水平交给 RecenterFixed（此刻宽度还没定，先按现有宽度居中，布局完再纠一次）
        Top = WorkArea().Top + EdgeGap - PillPad;
        RecenterFixed();

        Show();
        UpdateLayout();
        RecenterFixed();
        ScheduleRecenter();

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        IslandShift.BeginAnimation(TranslateTransform.YProperty, null);
        IslandShift.Y = 0;

        var fade = new DoubleAnimation(0, 1, ExpandDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fade);

        // 从窄条展开：同样是"刚刚可见"，重定歌词行 + 重启滚动（v0.2.10 需求 4）
        RaiseIslandBecameVisible();

        // 展开态才需要指针巡检（悬停放大 / 悬停暂停计时）
        SetHoverWatch(true);

        // 展开后按统一规则重新计时：鼠标不进来就等超时再收成细条
        _autoTimer.Stop();
        RestartAutoCollapse();
    }

    private SnapStripWindow EnsureStrip()
    {
        if (_strip is null)
        {
            _strip = new SnapStripWindow
            {
                // 左/右分段比例跟设置里的"区域比例"滑块一致（需求 2）
                SplitRatioProvider = () => IslandSplitRatioProvider?.Invoke() ?? 50,
            };

            _strip.ZoneDoubleClicked += OnStripZoneDoubleClicked;

            // 信息展示模式下，悬停窄条 260ms 就展开岛把内容显示出来（需求 2 补充规则）
            _strip.HoverExpandRequested += (_, _) =>
            {
                if (_stripMode && !_dragging && !_sliding && !IsIdleStandby)
                {
                    ExpandFromStrip();
                }
            };
        }

        return _strip;
    }

    /// <summary>
    /// 待机窄条双击分区（v0.2.10 需求 2）：左段打开音乐播放器、右段打开 WorkBuddy。
    /// 只用双击，单击不做事——否则双击的第一下就会被单击逻辑吃掉（比如把岛展开、细条消失）。
    /// </summary>
    private void OnStripZoneDoubleClicked(object? sender, StripZone zone)
    {
        try
        {
            // 需求 2：双击快捷唤起**只在待机空闲模式**生效；信息展示模式下不抢占双击
            if (!IsIdleStandby)
            {
                LogService.Shared.Routine(
                    "IslandStrip",
                    "非待机模式，忽略双击",
                    $"zone={zone} standby={IsIdleStandby}");
                return;
            }

            if (zone == StripZone.Left)
            {
                // 优先用当前在播的播放器；没有就按常见播放器名单找（进程在跑就直接激活，绝不重复启动）
                var running = _media?.ProcessName;
                var outcome = string.IsNullOrWhiteSpace(running)
                    ? OpenOutcome.Failed
                    : AppLauncher.Open(running!);

                if (outcome is OpenOutcome.Failed)
                {
                    outcome = AppLauncher.Open("Folia", AppLauncher.MusicAppCandidates());
                }

                LogService.Shared.Write(
                    outcome == OpenOutcome.Failed ? LogLevel.Warn : LogLevel.Info,
                    "IslandStrip",
                    "双击左段：打开音乐播放器",
                    $"outcome={outcome} process={running}");

                // 真的是"冷启动拉起"的：保持岛展开一会儿，让用户能马上点播放键
                // （需求 2：通过灵动岛按钮控制刚拉起的播放器）
                if (outcome == OpenOutcome.Launched)
                {
                    NoteUserLaunch();
                }

                return;
            }

            var wb = AppLauncher.Open("WorkBuddy", AppLauncher.WorkBuddyCandidates());
            LogService.Shared.Write(
                wb == OpenOutcome.Failed ? LogLevel.Warn : LogLevel.Info,
                "IslandStrip",
                "双击右段：打开 WorkBuddy",
                $"outcome={wb}");

            if (wb == OpenOutcome.Launched)
            {
                NoteUserLaunch();
            }
        }
        catch (Exception ex)
        {
            LogService.Shared.Warn("IslandStrip", "双击分区处理失败", ex.Message);
        }
    }

    private void RaiseIslandBecameVisible()
    {
        try
        {
            IslandBecameVisible?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogService.Shared.Warn("Island", "岛可见回调失败", ex.Message);
        }
    }

    /// <summary>细条上的状态点颜色：完成态绿点，其余黄点。</summary>
    private bool IsDoneState
        => _statusVisible && CheckGlyph.Visibility == Visibility.Visible;

    /// <summary>用 SetWindowPos 逐帧滑动（比动画 Window.Left 稳，不受 WPF 布局重算干扰）。</summary>
    private void SlideTo(double targetLeft, double targetTop, int durationMs, Action? done)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var current))
        {
            Left = targetLeft;
            Top = targetTop;
            done?.Invoke();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        _slideFromX = current.Left;
        _slideFromY = current.Top;
        _slideToX = (int)Math.Round(targetLeft * scaleX);
        _slideToY = (int)Math.Round(targetTop * scaleY);
        // 系统关掉了"动画效果"（Win10 设置→轻松使用 / Win11 设置→辅助功能）：直接落位，
        // 否则用户会看到一段"没播完就停住"的残影。两代系统都会读同一个 SystemParameters 开关。
        if (!Services.SystemCapabilities.Current.AnimationsEnabled)
        {
            _slideDone = null;
            NativeMethods.MoveWindowTo(_hwnd, _slideToX, _slideToY);
            SyncWpfPositionFromHwnd();
            done?.Invoke();
            return;
        }

        _slideDuration = Math.Max(60, durationMs);
        _slideDone = done;

        if (_slideFromX == _slideToX && _slideFromY == _slideToY)
        {
            _slideDone = null;
            done?.Invoke();
            return;
        }

        _sliding = true;
        _slideWatch.Restart();
        HookSlideFrame();
    }

    /// <summary>
    /// 把滑动动画挂到渲染帧回调上（每帧一次，和显示器刷新同步）。
    /// 旧版用 15ms 的 DispatcherTimer：既不是 16.67ms，也不跟垂直同步，
    /// 每帧位置会抖 1px 左右，肉眼看就是"滑得不顺"。帧回调没有这个问题。
    /// </summary>
    private void HookSlideFrame()
    {
        if (_slideFrameHooked)
        {
            return;
        }

        _slideFrameHooked = true;
        CompositionTarget.Rendering += OnSlideTick;
    }

    private void UnhookSlideFrame()
    {
        if (!_slideFrameHooked)
        {
            return;
        }

        _slideFrameHooked = false;
        CompositionTarget.Rendering -= OnSlideTick;
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        if (!_sliding)
        {
            UnhookSlideFrame();
            return;
        }

        // 一帧只移动一次（v0.2.9 需求 1）：同一帧重复 SetWindowPos 会触发多余的 DWM 提交
        if (!UI.FrameClock.ShouldTick(ref _slideFrameTime, e))
        {
            return;
        }

        var elapsed = _slideWatch.ElapsedMilliseconds;
        var t = Math.Clamp(elapsed / (double)_slideDuration, 0, 1);

        // ease-out cubic：起步快、收尾稳，看起来"顺滑地贴上去"
        var eased = 1 - Math.Pow(1 - t, 3);

        var x = (int)Math.Round(_slideFromX + ((_slideToX - _slideFromX) * eased));
        var y = (int)Math.Round(_slideFromY + ((_slideToY - _slideFromY) * eased));

        NativeMethods.MoveWindowTo(_hwnd, x, y);

        if (t < 1)
        {
            return;
        }

        UnhookSlideFrame();
        _slideWatch.Stop();
        _sliding = false;
        SyncWpfPositionFromHwnd();

        var done = _slideDone;
        _slideDone = null;
        done?.Invoke();
    }

    private void StopSlide()
    {
        UnhookSlideFrame();
        _slideWatch.Stop();
        _sliding = false;
        _slideDone = null;
    }

    /// <summary>把 HWND 的真实像素位置写回 WPF 的 Left/Top（SetWindowPos 之后必须同步一次）。</summary>
    private void SyncWpfPositionFromHwnd()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = Math.Round(rect.Left / scaleX, 1);
        Top = Math.Round(rect.Top / scaleY, 1);
    }

    // ---------------- 执行态呼吸点 ----------------

    private void StartBreathing()
    {
        var breathe = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        StatusDot.BeginAnimation(OpacityProperty, breathe);
    }

    private void StopBreathing()
    {
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;
    }

    // ---------------- 音乐快捷控制 ----------------

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        RestartAutoCollapse();
        PlayPauseRequested?.Invoke();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        RestartAutoCollapse();
        NextRequested?.Invoke();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        RestartAutoCollapse();
        PreviousRequested?.Invoke();
    }

    // ---------------- Win32 消息：拖拽 + 透明留白穿透 ----------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                // 关键：WS_EX_NOACTIVATE 的窗口如果按默认流程走"激活"，系统会把这一下点击
                // 当成激活点击吞掉 —— 表现就是"点了但什么都没发生"。明确回 MA_NOACTIVATE
                // 才能既保持不抢焦点，又把点击完整地继续投递进来。
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);

            case NativeMethods.WM_NCHITTEST:
            {
                var screenPoint = ScreenPointOf(lParam);

                // 阴影留白（Pill 以外的透明区）一律放行给下层窗口，
                // 否则一条 14px 的隐形边框会挡住桌面/其他程序的点击。
                if (!IsInsidePill(screenPoint))
                {
                    handled = true;
                    return new IntPtr(NativeMethods.HTTRANSPARENT);
                }

                // v0.2.6 需求 2：固定岛只让播放三键可点，其余区域点按穿透——
                // 用户能直接透过岛点到下层的 App 按钮，不用先等倒计时/拖动。
                // 自由模式保留整条可拖（否则没法移动），所以只在固定模式下穿透。
                if (!IsFreeMode() && !IsOverButton(screenPoint))
                {
                    handled = true;
                    return new IntPtr(NativeMethods.HTTRANSPARENT);
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WM_LBUTTONDOWN:
            case NativeMethods.WM_RBUTTONDOWN:
                if (!CanStartDrag(lParam))
                {
                    // 命中播放按钮或固定模式：这一下交给 WPF 自己处理点击
                    LogService.Shared.Routine(
                        "IslandDrag",
                        "按下被忽略",
                        $"free={IsFreeMode()} hidden={_hidden} interactive={HitIsInteractive(CursorX(lParam), CursorY(lParam))}");
                    return IntPtr.Zero;
                }

                BeginDrag(hwnd);
                handled = true;
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEMOVE:
                if (_dragging)
                {
                    MoveDrag();
                    handled = true;
                }

                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONUP:
            case NativeMethods.WM_RBUTTONUP:
                if (_dragging)
                {
                    EndDrag();
                    handled = true;
                }

                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private bool CanStartDrag(IntPtr lParam)
    {
        if (!IsFreeMode() || _hidden)
        {
            return false;
        }

        // 点在播放按钮上 → 不拖，让按钮吃这一下
        return !HitIsInteractive(CursorX(lParam), CursorY(lParam));
    }

    private void BeginDrag(IntPtr hwnd)
    {
        if (_dragging)
        {
            return;
        }

        NativeMethods.GetCursorPos(out _dragStartCursor);
        NativeMethods.GetWindowRect(hwnd, out _dragStartWindow);

        _dragging = true;
        _dragMoved = false;
        _autoTimer.Stop();
        StopSlide();

        // 吸附/细条态直接拖：先清状态，落手位置由用户决定
        _snapped = false;
        _stripMode = false;
        _strip?.Collapse();
        _snapEdge = Edge.None;

        NativeMethods.SetCapture(hwnd);
    }

    private void MoveDrag()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.GetCursorPos(out var cursor);

        var x = _dragStartWindow.Left + (cursor.X - _dragStartCursor.X);
        var y = _dragStartWindow.Top + (cursor.Y - _dragStartCursor.Y);

        // 别让用户把条拖出允许停靠区（需求 4）：水平限中间 3/5，垂直限顶部一带
        var area = WorkArea();
        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        var width = _dragStartWindow.Right - _dragStartWindow.Left;
        var height = _dragStartWindow.Bottom - _dragStartWindow.Top;

        var zoneInset = (int)(area.Width * 0.2 * sx);
        var zoneMin = (int)(area.Left * sx) + zoneInset - (width / 2);
        var zoneMax = (int)(area.Right * sx) - zoneInset - (width / 2);

        // 与 ClampToScreen 同款硬约束：整条必须留在屏内（只允许阴影留白出界）
        var fitMin = (int)(area.Left * sx) - 20;
        var fitMax = (int)(area.Right * sx) - width + 20;

        var minX = Math.Max(zoneMin, fitMin);
        var maxX = Math.Min(zoneMax, fitMax);
        if (maxX < minX)
        {
            minX = fitMin;
            maxX = fitMax;
        }

        var minY = (int)(area.Top * sy) - 40;
        var maxY = (int)((area.Top + 120) * sy);

        _dragMoved = true;
        NativeMethods.MoveWindowTo(_hwnd, Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
    }

    private void EndDrag()
    {
        _dragging = false;
        NativeMethods.ReleaseCapture();

        if (!_dragMoved)
        {
            // 只是点了一下（没真正移动）：不改位置、不写配置，避免把脏坐标存进去
            RestartAutoCollapse();
            return;
        }

        _dragMoved = false;
        SyncWpfPositionFromHwnd();

        var (left, top) = ClampToScreen(Left, Top);
        Left = left;
        Top = top;

        PositionCommitted?.Invoke(left, top);
        RestartAutoCollapse();
    }

    private static int CursorX(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);

    private static int CursorY(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    /// <summary>WM_NCHITTEST 的 lParam 是屏幕坐标（像素）。</summary>
    private static Point ScreenPointOf(IntPtr lParam) => new(CursorX(lParam), CursorY(lParam));

    private bool IsInsidePill(Point screenPoint)
    {
        if (!IsLoaded || Pill.ActualWidth <= 0)
        {
            return false;
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var local = new Point(screenPoint.X / dpi.DpiScaleX, screenPoint.Y / dpi.DpiScaleY);
            var origin = PointToScreen(new Point(0, 0));
            var ox = origin.X / dpi.DpiScaleX;
            var oy = origin.Y / dpi.DpiScaleY;

            var pad = Pill.Margin;
            var rect = new Rect(
                ox + pad.Left,
                oy + pad.Top,
                Math.Max(1, Pill.ActualWidth),
                Math.Max(1, Pill.ActualHeight));

            return rect.Contains(local);
        }
        catch
        {
            return true; // 算不出来时宁可让事件进来，也不要让整条岛失去交互
        }
    }

    /// <summary>
    /// 本地坐标（DIP）是否落在某个 Button 上——点按穿透判定的唯一事实来源。
    /// WM_NCHITTEST、拖拽判定、自检都走这里，避免三处逻辑漂移。
    /// </summary>
    internal bool HitTestInteractive(Point localDip)
    {
        try
        {
            var hit = InputHitTest(localDip) as DependencyObject;
            while (hit is not null)
            {
                if (hit is Button)
                {
                    return true;
                }

                hit = hit is Visual or Visual3D
                    ? VisualTreeHelper.GetParent(hit)
                    : LogicalTreeHelper.GetParent(hit);
            }
        }
        catch
        {
            // 忽略：拿不到就当成空白区
        }

        return false;
    }

    /// <summary>命中测试（拖拽用）：是否落在播放按钮等交互控件上（这些位置不参与拖拽）。</summary>
    private bool HitIsInteractive(double clientX, double clientY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return HitTestInteractive(new Point(clientX / dpi.DpiScaleX, clientY / dpi.DpiScaleY));
    }

    /// <summary>
    /// 屏幕坐标（物理像素）是否落在某个 Button 上。
    /// 与 <see cref="HitIsInteractive"/> 的区别：这里做完整的 屏幕→窗口→DIP 换算，
    /// 供 WM_NCHITTEST 判定“该不该让鼠标穿透”使用。
    /// 取不到布局时返回 true（宁可让点击进来，也不要让按钮失灵）。
    /// </summary>
    private bool IsOverButton(Point screenPoint)
    {
        if (!IsLoaded)
        {
            return true;
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var origin = PointToScreen(new Point(0, 0));
            var local = new Point(
                (screenPoint.X - origin.X) / dpi.DpiScaleX,
                (screenPoint.Y - origin.Y) / dpi.DpiScaleY);

            return HitTestInteractive(local);
        }
        catch
        {
            return true;
        }
    }

    private void EnsureCreated() => Current ??= this;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // 与通知卡片一致：不激活、不进 Alt-Tab，绝不抢键盘焦点
        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        // 有了真实 HWND 之后 DPI 才准确：再算一次缩放，并按新尺寸重新定位
        ApplyUiScale();

        ApplyModeChange();
    }

    /// <summary>
    /// 显示器 DPI / 分辨率变化（拖到另一块屏、改系统缩放）：
    /// 重算界面缩放 → 岛内尺寸变了 → 重新居中/贴边（v0.2.9 需求 1）。
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        try
        {
            ApplyUiScale();
            UpdateLayout();

            if (!_hidden && IsVisible && !_dragging && !_sliding)
            {
                if (IsFreeMode())
                {
                    var (left, top) = ClampToScreen(Left, Top);
                    Left = left;
                    Top = top;
                }
                else
                {
                    RecenterFixed();
                }
            }

            SyncStripState();
        }
        catch
        {
            // 切换显示器瞬间布局可能还没就绪，下一帧会自愈
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        _source = null;
        _statusTimer.Stop();
        _musicTimer.Stop();
        _autoTimer.Stop();
        UnhookSlideFrame();
        _slideWatch.Stop();
        _sliding = false;
        _strip?.Close();
        _strip = null;

        if (Current == this)
        {
            Current = null;
        }

        base.OnClosed(e);
    }
}
