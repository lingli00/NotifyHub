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
/// v0.2.6 新增：
///   1) <b>滑动取消</b>：按住卡片上下/左右拖动，超过阈值松手即带惯性飞出并关闭，
///      未过阈值则弹回原位；拖动过程中倒计时暂停。位移作用在窗口本身
///      （SetWindowPos），所以卡片能真正滑出窗口边界，不会被裁剪。
///   2) <b>主题</b>：背景与文字颜色由 <see cref="ThemePalette"/> 注入
///      （纯色暗黑 / 深色毛玻璃 / 浅色毛玻璃）。
/// </summary>
public partial class NotificationWindow : Window
{
    private const double SwipeDistanceX = 88;      // 横向飞出阈值（DIP）
    private const double SwipeDistanceUp = 46;     // 上滑收起阈值（DIP）
    private const double SwipeVelocity = 620;      // 快速甩动阈值（DIP/秒）

    /// <summary>入场时的水平滑入距离（DIP），方向由所在角落决定。</summary>
    private const double EntrySlide = 22;

    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _swipeTimer = new();
    private readonly int _durationMs;
    private readonly double _marqueeSpeed;
    private readonly AppConfig _config;

    private double _remainingMs;
    private DateTime _segmentStart;
    private bool _closing;
    private double _entryOffset = EntrySlide;

    // ---- 滑动取消状态 ----
    private bool _pressed;
    private bool _swiping;
    private double _startLeft;
    private double _startTop;
    private int _startCursorX;
    private int _startCursorY;
    private double _lastLeft;
    private double _lastTop;
    private DateTime _lastMoveAt;
    private double _velocityX;      // DIP/秒
    private double _velocityY;

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
        _swipeTimer.Interval = TimeSpan.FromMilliseconds(16);
        _swipeTimer.Tick += OnSwipeTick;

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

    /// <summary>用户点击卡片。</summary>
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
        _swipeTimer.Stop();

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
        _swipeTimer.Stop();
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

        // 拖动中鼠标会“离开”卡片（窗口跟着手走，但离开事件仍会触发），此时不能恢复计时
        if (_closing || _pressed)
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

    /// <summary>左键抬起：区分「滑动取消」与「点击」。</summary>
    private void OnCardLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed)
        {
            FinishSwipe(e);
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

    // ---------------- 滑动取消（上滑 / 侧滑，带惯性） ----------------

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        e.Handled = true;

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        _pressed = true;
        _swiping = false;
        _startCursorX = cursor.X;
        _startCursorY = cursor.Y;
        _startLeft = Left;
        _startTop = Top;
        _lastLeft = Left;
        _lastTop = Top;
        _lastMoveAt = DateTime.UtcNow;
        _velocityX = 0;
        _velocityY = 0;

        // 拖动期间不计时（用户正在操作，不能被倒计时关掉）
        _timer.Stop();
        _remainingMs = Math.Max(400, _remainingMs - (DateTime.UtcNow - _segmentStart).TotalMilliseconds);

        Card.CaptureMouse();
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || _closing || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var dx = (cursor.X - _startCursorX) / dpi.DpiScaleX;
        var dy = (cursor.Y - _startCursorY) / dpi.DpiScaleY;

        // 8px 以内当点击，不算滑动（避免手抖把点击吃掉）
        if (!_swiping)
        {
            if (Math.Abs(dx) < 8 && Math.Abs(dy) < 8)
            {
                return;
            }

            _swiping = true;
        }

        var left = _startLeft + dx;
        var top = _startTop + dy;

        // 速度采样（DIP/秒），松手时用于惯性初速度
        var now = DateTime.UtcNow;
        var dt = Math.Max(0.004, (now - _lastMoveAt).TotalSeconds);
        _velocityX = (left - _lastLeft) / dt;
        _velocityY = (top - _lastTop) / dt;
        _lastLeft = left;
        _lastTop = top;
        _lastMoveAt = now;

        MoveWindow(left, top);

        // 拖得越远越透，形成“甩出去”的手感（最多减到 0.45）
        var distance = Math.Sqrt(dx * dx + dy * dy);
        Card.Opacity = Math.Clamp(1 - distance / 520, 0.45, 1);
    }

    private void FinishSwipe(MouseButtonEventArgs e)
    {
        _pressed = false;
        e.Handled = true;
        Card.ReleaseMouseCapture();

        if (!_swiping)
        {
            // 位移不足 8px：当作点击
            Clicked?.Invoke(this, EventArgs.Empty);
            Dismiss();
            return;
        }

        var dx = Left - _startLeft;
        var dy = Top - _startTop;
        var speed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);

        var dismiss = Math.Abs(dx) > SwipeDistanceX
                      || dy < -SwipeDistanceUp
                      || speed > SwipeVelocity;

        if (dismiss)
        {
            StartInertiaFlyOut();
            return;
        }

        SpringBack();
    }

    /// <summary>松手后按当前速度继续滑出，同时淡出到 0（iOS 通知的上滑收起手感）。</summary>
    private void StartInertiaFlyOut()
    {
        _closing = true;
        _timer.Stop();

        // 惯性初速度：取拖动速度，给一个下限保证“甩出去”的观感
        var speed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
        var scale = speed < 260 ? 260 / Math.Max(1, speed) : 1;
        _velocityX *= scale;
        _velocityY *= scale;

        _swipeTimer.Start();
    }

    private void OnSwipeTick(object? sender, EventArgs e)
    {
        const double dt = 0.016;

        // 衰减：每帧 0.90（≈ 60 帧后速度剩 0.2%）
        _velocityX *= 0.90;
        _velocityY *= 0.90;

        var left = Left + _velocityX * dt;
        var top = Top + _velocityY * dt;
        MoveWindow(left, top);

        Card.Opacity = Math.Max(0, Card.Opacity - 0.062);

        if (Card.Opacity <= 0.02 || Math.Abs(_velocityX) + Math.Abs(_velocityY) < 6)
        {
            _swipeTimer.Stop();
            Close();
        }
    }

    /// <summary>未过阈值：弹回原位并恢复不透明度与倒计时。</summary>
    private void SpringBack()
    {
        var fromLeft = Left;
        var fromTop = Top;
        var fromOpacity = Card.Opacity;

        var start = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(180);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / duration.TotalMilliseconds);
            var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic

            MoveWindow(fromLeft + (_startLeft - fromLeft) * eased, fromTop + (_startTop - fromTop) * eased);
            Card.Opacity = fromOpacity + (1 - fromOpacity) * eased;

            if (t >= 1)
            {
                timer.Stop();
                Card.Opacity = 1;
                StartCountdown(_remainingMs);
            }
        };

        timer.Start();
    }

    /// <summary>移动窗口（DIP）。走 SetWindowPos 而不是 Window.Left/Top，拖动更跟手。</summary>
    private void MoveWindow(double left, double top)
    {
        Left = left;
        Top = top;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(left * dpi.DpiScaleX),
            (int)Math.Round(top * dpi.DpiScaleY),
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}
