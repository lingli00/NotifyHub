using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotifyHub.Interop;
using NotifyHub.Models;

namespace NotifyHub.UI;

/// <summary>
/// 单条通知卡片：无边框透明、四角定位（由 NotificationManager 决定）、Topmost、
/// 不抢焦点（WS_EX_NOACTIVATE）。180ms 柔和淡入 → 显示 durationMs → 180ms 淡出；
/// 鼠标悬停暂停倒计时。
/// </summary>
public partial class NotificationWindow : Window
{
    private readonly DispatcherTimer _timer = new();
    private readonly int _durationMs;
    private readonly double _marqueeSpeed;

    private double _remainingMs;
    private DateTime _segmentStart;
    private bool _closing;

    public NotificationWindow(NotifyMessage message, int durationMs, double marqueeSpeed = MarqueeText.DefaultSpeed)
    {
        InitializeComponent();

        Message = message;
        _durationMs = Math.Clamp(durationMs, AppConfig.MinDurationMs, AppConfig.MaxDurationMs);
        _marqueeSpeed = marqueeSpeed;

        Fill(message);

        _timer.Tick += (_, _) => Dismiss();

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
    }

    public NotifyMessage Message { get; }

    /// <summary>卡片当前不透明度（诊断用）。</summary>
    public double CardOpacity => Card.Opacity;

    /// <summary>窗口关闭后触发（管理器据此重排剩余卡片）。</summary>
    public event EventHandler? Dismissed;

    /// <summary>用户点击卡片。</summary>
    public event EventHandler? Clicked;

    /// <summary>定位到目标位置并显示；animate=false 时直接显示最终状态。</summary>
    public void ShowAt(double left, double top, bool animate)
    {
        Left = left;
        Top = top;

        if (!animate)
        {
            Card.Opacity = 1;
            CardTransform.X = 0;
        }

        Show();
    }

    /// <summary>重排时移动到新的纵向位置。</summary>
    public void MoveTo(double left, double top, bool animate)
    {
        // 注意：Window.Left/Top 初始是 NaN，NaN 的比较恒为 false，必须先判 NaN
        if (double.IsNaN(Left) || Math.Abs(Left - left) > 0.5)
        {
            Left = left;
        }

        if (double.IsNaN(Top) || Math.Abs(Top - top) < 0.5)
        {
            Top = top;
            return;
        }

        if (!animate || !IsVisible)
        {
            Top = top;
            return;
        }

        var animation = new DoubleAnimation(Top, top, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

        animation.Completed += (_, _) =>
        {
            Top = top;
            BeginAnimation(TopProperty, null);
        };

        BeginAnimation(TopProperty, animation);
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
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void Fill(NotifyMessage message)
    {
        // 深色四段式：顶部来源、中间会话名、主体预览、底部时间
        AppNameText.Text = message.SourceText;
        TimeText.Text = message.Time.ToString("HH:mm");
        SenderText.Text = string.IsNullOrWhiteSpace(message.Sender) ? message.SourceText : message.Sender;
        ContentText.Text = string.IsNullOrWhiteSpace(message.Content) ? "（无内容）" : message.Content;
        ContentText.Speed = _marqueeSpeed;

        Color iconColor;
        string glyph;
        double glyphSize;

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

        var slide = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(180))
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
        if (_closing)
        {
            return;
        }

        StartCountdown(_remainingMs);
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Clicked?.Invoke(this, EventArgs.Empty);
        Dismiss();
    }

    private void OnCardRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }
}
