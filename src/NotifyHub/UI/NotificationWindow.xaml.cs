using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotifyHub.Interop;
using NotifyHub.Models;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 单条通知卡片：无边框透明、四角定位（由 NotificationManager 决定）、Topmost、
/// 不抢焦点（WS_EX_NOACTIVATE）。180ms 柔和淡入 → 显示 durationMs → 180ms 淡出；
/// 鼠标悬停暂停倒计时。
///
/// v0.2.18 需求4（交互改版）：
///   1) **移除滑动删除**——旧版"按住拖动越过阈值松手飞出"实测不好用
///      （拖动与点击抢手势、惯性飞出动效在四角堆叠场景下还会误伤相邻卡片）；
///   2) **左上角关闭叉号**：每张卡片左上角一个 20×20 的 ✕ 按钮，点一下即关闭移除，
///      与"点击卡片正文打开消息"分工明确，互不误触。
///
/// 主题：背景与文字颜色由 <see cref="ThemePalette"/> 注入
/// （纯色暗黑 / 深色毛玻璃 / 浅色毛玻璃）。
/// </summary>
public partial class NotificationWindow : Window
{
    /// <summary>入场时的水平滑入距离（DIP），方向由所在角落决定。</summary>
    private const double EntrySlide = 22;

    private readonly DispatcherTimer _timer = new();

    /// <summary>
    /// 卡死兜底（v0.2.15 需求 3）：卡片最迟在"显示时长 + 3 秒"后强制关闭。
    ///
    /// 正常路径是 <c>ContentRendered</c> → 启动倒计时 → 到点 Dismiss。
    /// 万一窗口没有真正渲染（ContentRendered 不派发）、或淡出动画的 Completed 丢了，
    /// 卡片就会永久留在屏幕上 —— 用户看到的就是"桌面角落多出一个白色/黑色方框，
    /// 退出 NotifyHub 才消失"。这里用一条独立计时器兜住所有异常路径，
    /// 悬停/按下时自动顺延（用户正在看，不能抢走）。
    /// </summary>
    private readonly DispatcherTimer _guard = new();

    /// <summary>已关闭（避免兜底路径重复 Close；自检也用它判断卡片是否真的收掉了）。</summary>
    private bool _closed;

    /// <summary>卡片是否已关闭（自检用）。</summary>
    public bool IsClosed => _closed;

    /// <summary>兜底余量（毫秒）：显示时长之外最多再留这么久（自检断言用）。</summary>
    public const int StuckGuardExtraMs = 3000;
    private readonly int _durationMs;
    private readonly double _marqueeSpeed;
    private readonly AppConfig _config;

    private double _remainingMs;
    private DateTime _segmentStart;
    private bool _closing;
    private double _entryOffset = EntrySlide;

    public NotificationWindow(NotifyMessage message, int durationMs, double marqueeSpeed = MarqueeText.DefaultSpeed,
        AppConfig? config = null, double uiScale = 1.0)
    {
        InitializeComponent();

        Message = message;
        _durationMs = Math.Clamp(durationMs, AppConfig.MinDurationMs, AppConfig.MaxDurationMs);
        _marqueeSpeed = marqueeSpeed;
        _config = config ?? new AppConfig();

        // v0.2.9 需求 1：整张卡片按屏幕比例等比缩放（字号/圆角/图标/间距一起变），
        // 窗口尺寸同步放大，阴影留白保持固定。这样 1080p / 2.5K / 4K 上
        // 卡片的"占屏比例"一致，不会出现某块屏幕小到看不清。
        UiScale = Math.Clamp(uiScale, 0.5, 3.0);
        Width = NotificationLayout.WindowWidthFor(UiScale);
        Height = NotificationLayout.WindowHeightFor(UiScale);
        Card.LayoutTransform = new ScaleTransform(UiScale, UiScale);
        _entryOffset = EntrySlide * UiScale;

        ApplyTheme(ThemePalette.Create(_config));
        Fill(message);

        _timer.Tick += (_, _) => Dismiss();
        _guard.Tick += (_, _) => OnGuardElapsed();

        Card.MouseEnter += OnCardMouseEnter;
        Card.MouseLeave += OnCardMouseLeave;

        // 倒计时从“第一帧真正画上屏”（ContentRendered）才启动，
        // 避免冷启动渲染耗时吃掉显示时长（用户感知就是“通知一闪而过”）。
        var started = false;
        ContentRendered += (_, _) =>
        {
            if (started)
            {
                return;
            }

            started = true;
            StartCountdown(_durationMs);
        };

        Loaded += (_, _) => StartFadeIn();

        // Per-Monitor V2：窗口跨屏或系统缩放变化时会走这里
        DpiChanged += (_, _) => DisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    public NotifyMessage Message { get; }

    /// <summary>本卡使用的界面缩放（v0.2.9 需求 1，诊断/自检用）。</summary>
    public double UiScale { get; }

    /// <summary>卡片当前不透明度（诊断用）。</summary>
    public double CardOpacity => Card.Opacity;

    /// <summary>当前窗口原点（DIP）；尚未定位（NaN）时返回屏幕外哨兵值。</summary>
    public (double Left, double Top) CurrentOrigin
        => (double.IsNaN(Left) ? -9999 : Left, double.IsNaN(Top) ? -9999 : Top);

    /// <summary>窗口关闭后触发（管理器据此重排剩余卡片）。</summary>
    public event EventHandler? Dismissed;

    /// <summary>用户点击卡片正文。</summary>
    public event EventHandler? Clicked;

    /// <summary>所在显示器 DPI 变化（拖动到另一屏、或系统改了缩放）——管理器据此重排。</summary>
    public event EventHandler? DisplayChanged;

    /// <summary>应用外观主题（毛玻璃/纯色 + 自动对比度文字）。</summary>
    public void ApplyTheme(ThemePalette palette)
    {
        Card.Background = palette.Background;
        Card.BorderBrush = ThemePalette.Brush(palette.Border);

        HighlightLayer.Background = new LinearGradientBrush(palette.Highlight, Colors.Transparent, 90)
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };

        AppNameText.Foreground = ThemePalette.Brush(palette.TextSecondary);
        SenderText.Foreground = ThemePalette.Brush(palette.TextPrimary);
        ContentText.Foreground = ThemePalette.Brush(palette.TextSecondary);
        TimeText.Foreground = ThemePalette.Brush(palette.TextTertiary);
    }

    /// <summary>
    /// 定位到目标位置并显示；animate=false 时直接显示最终状态。
    /// <paramref name="fromLeft"/> 决定入场滑动方向：左角从左侧滑入、右角从右侧滑入
    /// （旧版一律从右侧滑入，左上/左下角看起来像"从屏幕中间飞出来"）。
    /// </summary>
    public void ShowAt(double left, double top, bool animate, bool fromLeft = false)
    {
        Left = left;
        Top = top;
        _entryOffset = fromLeft ? -EntrySlide : EntrySlide;

        // v0.2.6 需求 4：显示前对卡片背后的屏幕取样，按合成亮度重选文字色
        // （深色毛玻璃压在白色网页上会自动转黑字，反之转白字）。
        if (_config.TextTone.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            var backdrop = BackdropProbe.AverageLuminance(new Rect(left, top, Width, Height));
            if (backdrop is not null)
            {
                ApplyTheme(ThemePalette.Create(_config, backdrop));
            }
        }

        if (!animate)
        {
            Card.Opacity = 1;
            CardTransform.X = 0;
        }

        Show();

        // v0.2.15 需求 3：上屏即武装兜底计时器（比显示时长多 3 秒的余量）
        _guard.Interval = TimeSpan.FromMilliseconds(_durationMs + StuckGuardExtraMs);
        _guard.Start();
    }

    /// <summary>
    /// 直接设置位置（DIP），不做 WPF 属性动画。
    /// v0.2.8 起重排动画上移到 <see cref="NotificationManager"/>，用帧驱动统一推进：
    /// 多张卡片在同一帧重排时，各自的 DoubleAnimation 会互相打断，
    /// 实测出现 118 / 54 / 94 这种不等步进（卡片叠在一起）。
    /// </summary>
    public void MoveTo(double left, double top, bool animate)
    {
        if (double.IsNaN(Left) || Math.Abs(Left - left) > 0.05)
        {
            Left = left;
        }

        if (double.IsNaN(Top) || Math.Abs(Top - top) > 0.05)
        {
            Top = top;
        }
    }

    /// <summary>关闭卡片（默认 180ms 淡出）。</summary>
    public void Dismiss(bool animate = true)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _timer.Stop();
        _guard.Stop();

        if (!animate || !IsVisible)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(Card.Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        Card.BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(CardTransform.X, 18, TimeSpan.FromMilliseconds(180));
        CardTransform.BeginAnimation(TranslateTransform.XProperty, slide);

        // 动画 Completed 不是"一定会派发"（窗口被隐藏、合成器挂起时都可能丢）：
        // 400ms 后还没关就直接关，绝不留一具空壳在屏幕上。
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();

            if (!_closed)
            {
                Close();
            }
        };
        fallback.Start();
    }

    /// <summary>兜底到点：没被悬停就直接收起（异常路径的唯一出口）。</summary>
    private void OnGuardElapsed()
    {
        if (_closing || _closed)
        {
            _guard.Stop();
            return;
        }

        // 用户正在看：顺延一拍，不抢
        if (Card.IsMouseOver)
        {
            return;
        }

        LogService.Shared.Warn("通知", "卡片超时未关闭，强制收起", Title);
        Dismiss(animate: false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 关键：不激活 + 不进 Alt-Tab，保证弹通知时键盘焦点留在原窗口
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true;
        _guard.Stop();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void Fill(NotifyMessage message)
    {
        // 四段式：顶部来源、中间会话名、主体预览、底部时间
        AppNameText.Text = message.SourceLabel is { Length: > 0 } label ? label : message.SourceText;
        TimeText.Text = message.Time.ToString("HH:mm");
        SenderText.Text = string.IsNullOrWhiteSpace(message.Sender) ? message.SourceText : message.Sender;
        ContentText.Text = string.IsNullOrWhiteSpace(message.Content) ? "（无内容）" : message.Content;
        ContentText.Speed = _marqueeSpeed;

        Color iconColor;
        string glyph;
        double glyphSize;

        if (message.Source == MessageSource.Ntfy && message.AccentIndex >= 0
            && message.AccentIndex < AppConfig.NtfyPalette.Count)
        {
            // 多 ntfy 订阅按订阅取色板颜色（低饱和度微调，与微信/QQ/ntfy 同风格）
            var rgb = AppConfig.NtfyPalette[message.AccentIndex];
            iconColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            glyph = "N";
            glyphSize = 10;
        }
        else
        {
            switch (message.Source)
            {
                case MessageSource.WeChat:
                    iconColor = Color.FromRgb(0x07, 0xC1, 0x60);
                    glyph = "微";
                    glyphSize = 10.5;
                    break;
                case MessageSource.QQ:
                    iconColor = Color.FromRgb(0x12, 0xB7, 0xF5);
                    glyph = "Q";
                    glyphSize = 11;
                    break;
                default:
                    iconColor = Color.FromRgb(0xF9, 0x73, 0x16);
                    glyph = "N";
                    glyphSize = 10;
                    break;
            }
        }

        AppIcon.Background = new LinearGradientBrush(
            Color.FromRgb(
                (byte)Math.Min(255, iconColor.R + 18),
                (byte)Math.Min(255, iconColor.G + 18),
                (byte)Math.Min(255, iconColor.B + 18)),
            iconColor,
            90);
        IconGlyph.Text = glyph;
        IconGlyph.FontSize = glyphSize;
    }

    /// <summary>入场：180ms 淡入 + 轻微右滑，缓动 ease-out。</summary>
    private void StartFadeIn()
    {
        Card.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Card.BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(_entryOffset, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        CardTransform.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void StartCountdown(double milliseconds)
    {
        _remainingMs = Math.Max(300, milliseconds);
        _segmentStart = DateTime.UtcNow;

        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(_remainingMs);
        _timer.Start();
    }

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        // v0.2.10 需求 3：悬停只做"整卡轻微放大"，不再叠加任何黑色遮罩/变色。
        AnimateHoverScale(1.02, spring: true);

        if (!_timer.IsEnabled)
        {
            return;
        }

        // 悬停暂停：扣掉已经消耗的时间，离开后只走剩余部分
        _timer.Stop();
        _remainingMs = Math.Max(400, _remainingMs - (DateTime.UtcNow - _segmentStart).TotalMilliseconds);
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        AnimateHoverScale(1.0, spring: false);

        if (_closing)
        {
            return;
        }

        StartCountdown(_remainingMs);
    }

    /// <summary>整卡悬停缩放：进入用回弹（柔和弹性）、离开平稳收，幅度 2%。</summary>
    private void AnimateHoverScale(double target, bool spring)
    {
        var duration = TimeSpan.FromMilliseconds(spring ? 200 : 160);
        var ease = spring
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
            : (IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseOut };

        CardHoverScale.BeginAnimation(
            ScaleTransform.ScaleXProperty, new DoubleAnimation(target, duration) { EasingFunction = ease });
        CardHoverScale.BeginAnimation(
            ScaleTransform.ScaleYProperty, new DoubleAnimation(target, duration) { EasingFunction = ease });
    }

    /// <summary>左键抬起（卡片正文）：点击 = 打开这条消息 + 收起卡片。</summary>
    private void OnCardLeftUp(object sender, MouseButtonEventArgs e)
    {
        // 叉号按钮的点击不冒泡成"打开消息"（它自己只负责关闭）
        if (e.OriginalSource is DependencyObject source
            && CloseButton is not null
            && (ReferenceEquals(source, CloseButton) || CloseButton.IsAncestorOf(source)))
        {
            return;
        }

        e.Handled = true;
        Clicked?.Invoke(this, EventArgs.Empty);
        Dismiss();
    }

    private void OnCardRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    /// <summary>左上角关闭叉号（v0.2.18 需求4）：只关闭，不触发"打开消息"。</summary>
    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }
}
