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
    /// 歌词正文相对音乐行左边缘的缩进（DIP）：
    /// 面板左距 2 + 竖条 2 + 竖条右边距 23 = 27，正好和歌名文字起点对齐。
    /// </summary>
    private const double LyricTextIndent = 27;

    /// <summary>歌词切换的淡入时长（v0.2.7：新行出现时轻微上浮 + 淡入）。</summary>
    private static readonly TimeSpan LyricSwapDuration = TimeSpan.FromMilliseconds(170);

    private readonly DispatcherTimer _statusTimer = new();
    private readonly DispatcherTimer _musicTimer = new();
    private readonly DispatcherTimer _slideTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };

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

    /// <summary>当前状态行是否是“新消息提示”的临时内容（超时后整条收起，不留在细条里）。</summary>
    private bool _statusTransient;

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
        LyricLine2.Anchor = TextAnchor.Left;
        StatusText.Anchor = TextAnchor.Left;
        LyricsPanel.RenderTransform = _lyricShift;

        _statusTimer.Tick += (_, _) => OnStatusHoldElapsed();
        _musicTimer.Tick += (_, _) => OnMusicGraceElapsed();
        _autoTimer.Tick += (_, _) => OnAutoCollapseElapsed();
        _slideTimer.Tick += (_, _) => OnSlideTick();

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
    }

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

    /// <summary>歌词区当前是否展开（诊断用）。</summary>
    public bool IsLyricsShown => LyricsPanel.Visibility == Visibility.Visible;

    /// <summary>当前歌词字号（诊断用，DIP）。</summary>
    public double LyricsFontSize => LyricLine1.FontSize;

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

        LyricLine1.FontSize = size + 1.5;
        LyricLine1.FontWeight = FontWeights.SemiBold;
        LyricLine2.FontSize = Math.Max(9, size - 1.5);
        LyricLine2.FontWeight = FontWeights.Normal;

        UpdateChrome();
    }

    /// <summary>左栏（音乐 + 歌词）实际宽度，诊断/自检用。</summary>
    public double LeftPaneWidth => LeftPane.ActualWidth;

    /// <summary>右栏（任务）实际宽度，诊断/自检用。</summary>
    public double RightPaneWidth => StatusGroup.ActualWidth;

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
    /// 更新双行歌词（v0.2.7）：<paramref name="current"/> 为当前行，
    /// <paramref name="next"/> 为下一行；都为 null 表示无歌词（收起歌词区）。
    /// 行内容变化时做 170ms 淡入 + 轻微上浮，让"换句"看得见而不是硬切。
    /// </summary>
    public void ShowLyrics(string? current, string? next)
    {
        var line1 = current ?? string.Empty;
        var line2 = next ?? string.Empty;
        var changed = !string.Equals(LyricLine1.Text, line1, StringComparison.Ordinal);
        var wasVisible = LyricsPanel.Visibility == Visibility.Visible;

        LyricLine1.Text = line1;
        LyricLine2.Text = line2;

        // 没词 / 关了开关 / 没音乐：一律收起（空面板会把岛撑高一小截）
        var hasLyric = (LyricsEnabledProvider?.Invoke() ?? true)
                       && _musicVisible
                       && line1.Trim().Length > 0;

        LyricsPanel.Visibility = hasLyric ? Visibility.Visible : Visibility.Collapsed;

        if (hasLyric != wasVisible)
        {
            UpdateChrome();
            Evaluate();
        }
        else if (hasLyric && changed)
        {
            // 只在"本来就显示、内容变了"时做动画：首次展开交给整岛动画，避免叠加发灰
            AnimateLyricSwap();
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

    /// <summary>
    /// 预热：离屏把 HWND 建出来并跑完一次布局/渲染。
    /// 不预热的话第一条 --island 命令要多等 150~300ms 的窗口创建 + 首帧渲染，
    /// 直接就把"状态更新 &lt;100ms"吃掉；预热之后 Show 基本只是一次合成。
    /// </summary>
    public void WarmUp()
    {
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
        _musicVisible = enabled && (_musicPlaying || _musicTimer.IsEnabled);

        MusicGroup.Visibility = _musicVisible ? Visibility.Visible : Visibility.Collapsed;

        // 音乐收起时歌词必须一起收起（否则会留两行空行把岛撑高）；
        // 恢复播放时按开关 + 是否真的有词重新判定
        var keepLyrics = _musicVisible
                         && (LyricsEnabledProvider?.Invoke() ?? true)
                         && LyricLine1.Text.Trim().Length > 0;

        LyricsPanel.Visibility = keepLyrics ? Visibility.Visible : Visibility.Collapsed;

        if (_media is not null)
        {
            var title = string.IsNullOrWhiteSpace(_media.Title) ? "未知曲目" : _media.Title;
            var artist = string.IsNullOrWhiteSpace(_media.Artist) ? _media.AppName : _media.Artist;
            MusicText.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";
            PlayPauseGlyph.Data = (Geometry)FindResource(_media.IsPlaying ? "GlyphPause" : "GlyphPlay");
            UpdateMusicIcon(_media);
        }

        UpdateChrome();
    }

    /// <summary>
    /// 岛最左侧的当前播放 App 图标（需求 5）：从进程 exe 提取透明图标，
    /// 圆角由外层 Border 裁剪；提不到图标时回落到 ♪ 字符，无播放时整组收起。
    /// </summary>
    private void UpdateMusicIcon(MediaInfo media)
    {
        try
        {
            var icon = AppIconExtractor.GetIcon(media.ProcessName);
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
        Divider.Visibility = _musicVisible && _statusVisible ? Visibility.Visible : Visibility.Collapsed;
        BalanceWidths();
    }

    /// <summary>
    /// 左右两栏宽度配平（v0.2.7 需求 1 的核心）。
    /// v0.2.6 的毛病：右侧任务文字固定 440px、左侧歌词固定 286px，一有任务岛就撑到 810px，
    /// 左挤右空。现在两侧都按「标签 + 正文」组织，正文宽度按对方是否存在来配：
    ///   有音乐 → 右侧 292（与左栏 ≈320 接近，视觉配平）
    ///   无音乐 → 右侧 380（独占整条，避免又窄又空）
    /// 歌词宽度则跟随左栏实际宽度，让两行的左右边界对齐。
    /// </summary>
    private void BalanceWidths()
    {
        var statusWidth = _musicVisible ? 262d : 360d;
        if (double.IsNaN(StatusText.Width) || Math.Abs(StatusText.Width - statusWidth) > 0.5)
        {
            StatusText.Width = statusWidth;
        }

        if (!_musicVisible || MusicGroup.ActualWidth <= 60)
        {
            return;
        }

        // 歌词正文宽度 = 音乐行宽度 − 竖条与左边距（右边界与播放三键对齐，左边界与歌名对齐）
        var lyricWidth = MusicGroup.ActualWidth - LyricTextIndent;
        if (lyricWidth < 80)
        {
            return;
        }

        if (Math.Abs(LyricLine1.Width - lyricWidth) > 0.5)
        {
            LyricLine1.Width = lyricWidth;
            LyricLine2.Width = lyricWidth;
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

    /// <summary>显隐调度：全屏强制隐藏；有任务或音乐才显示。</summary>
    private void Evaluate()
    {
        var shouldShow = !_fullscreen && (_statusVisible || _musicVisible);

        if (shouldShow)
        {
            CancelCollapse();
            ShowIsland();
        }
        else if (!_hidden && !_dragging)
        {
            CollapseIsland();
        }
        else if (_stripMode)
        {
            // 细条形态下内容全部消失（任务过期/音乐停止）：细条一并收掉
            _stripMode = false;
            _strip?.Collapse();
            _autoTimer.Stop();
        }
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
        Left = left;
        Top = SystemParameters.WorkArea.Top + EdgeGap - PillPad;
    }

    /// <summary>固定模式：水平居中、贴顶（视觉缝隙 = EdgeGap）。可重复调用（尺寸变化后纠偏）。</summary>
    private void RecenterFixed()
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 1 ? ActualWidth : 420;
        Left = Math.Round(area.Left + ((area.Width - w) / 2));
        Top = Math.Round(area.Top + EdgeGap - PillPad);
    }

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
        var area = SystemParameters.WorkArea;
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

        _autoTimer.Interval = TimeSpan.FromSeconds(AutoCollapseSeconds());
        _autoTimer.Start();

        LogService.Shared.Write(
            LogLevel.Debug, "IslandAuto",
            $"收纳倒计时重置 {AutoCollapseSeconds()}s",
            $"hidden={_hidden} strip={_stripMode} hover={Pill.IsMouseOver} music={_musicPlaying} status={_statusVisible}");
    }

    /// <summary>
    /// 倒计时结束：统一收成屏幕顶部细条。
    /// 三条豁免——鼠标停在岛上（用户正在看）、完成态（有自己 600ms 的收尾节奏）、
    /// 音乐正在播放且用户开启了"播放时保持展开"（否则永远看不到歌词）。
    /// </summary>
    private void OnAutoCollapseElapsed()
    {
        _autoTimer.Stop();

        LogService.Shared.Write(
            LogLevel.Debug, "IslandAuto", "收纳计时到点",
            $"hidden={_hidden} drag={_dragging} sliding={_sliding} strip={_stripMode} " +
            $"hover={Pill.IsMouseOver} done={IsDoneState} transient={_statusTransient} " +
            $"music={_musicPlaying} keepOpen={KeepOpenWhilePlaying()}");

        if (_hidden || _dragging || _sliding || _stripMode)
        {
            return;
        }

        if (Pill.IsMouseOver)
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

        // 音乐正在播且允许保持展开 → 一直显示歌词
        if (_musicPlaying && _musicVisible && KeepOpenWhilePlaying())
        {
            return;
        }

        SnapToNearestEdge();
    }

    private void OnPillMouseEnter(object sender, MouseEventArgs e)
    {
        // 鼠标进入：暂停收纳倒计时（正在看歌词 / 准备点按钮）
        _autoTimer.Stop();
    }

    private void OnPillMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging || _sliding)
        {
            return;
        }

        RestartAutoCollapse();
    }

    /// <summary>
    /// 吸附到顶部边缘（v0.2.3：只允许顶部停靠）。
    /// 水平位置保留用户拖到的落点（收敛进中间 3/5），垂直贴顶，落定后收成细条。
    /// </summary>
    private void SnapToNearestEdge()
    {
        var area = SystemParameters.WorkArea;
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
        EnsureStrip().ShowFor("Top", IsDoneState);

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

        LogService.Shared.Write(LogLevel.Debug, "IslandAuto", "从细条展开", $"status={_statusVisible} music={_musicPlaying}");

        var token = ++_animToken;
        _stripMode = false;
        _strip?.Collapse();
        _snapped = false;

        // 贴顶全展开：水平收敛进中间 3/5，垂直留出与吸附同款缝隙
        var (left, _) = ClampToScreen(Left, Top);
        Left = left;
        Top = SystemParameters.WorkArea.Top + EdgeGap - PillPad;

        Show();
        UpdateLayout();

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        IslandShift.BeginAnimation(TranslateTransform.YProperty, null);
        IslandShift.Y = 0;

        var fade = new DoubleAnimation(0, 1, ExpandDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fade);

        // 展开后按统一规则重新计时：鼠标不进来就等超时再收成细条
        _autoTimer.Stop();
        RestartAutoCollapse();
    }

    private SnapStripWindow EnsureStrip()
    {
        if (_strip is null)
        {
            _strip = new SnapStripWindow();
            _strip.ExpandRequested += (_, _) =>
            {
                if (_stripMode && !_dragging && !_sliding)
                {
                    ExpandFromStrip();
                }
            };
        }

        return _strip;
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
        _slideTimer.Start();
    }

    private void OnSlideTick()
    {
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

        _slideTimer.Stop();
        _slideWatch.Stop();
        _sliding = false;
        SyncWpfPositionFromHwnd();

        var done = _slideDone;
        _slideDone = null;
        done?.Invoke();
    }

    private void StopSlide()
    {
        _slideTimer.Stop();
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
                    LogService.Shared.Write(
                        LogLevel.Debug,
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
        var area = SystemParameters.WorkArea;
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

        ApplyModeChange();
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        _source = null;
        _statusTimer.Stop();
        _musicTimer.Stop();
        _autoTimer.Stop();
        _slideTimer.Stop();
        _strip?.Close();
        _strip = null;

        if (Current == this)
        {
            Current = null;
        }

        base.OnClosed(e);
    }
}
