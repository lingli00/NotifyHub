using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotifyHub.Interop;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>细条的左/右分区（v0.2.10 需求 2）：左 = 音乐播放器，右 = WorkBuddy。</summary>
public enum StripZone
{
    None,
    Left,
    Right,
}

/// <summary>
/// 待机窄条（v0.2.10 需求 2 重做）。
///
/// 交互规则（这次按需求逐条对齐）：
///   · 悬停：整条**轻微放大**（1.06 倍 + 回弹），不展开、不变色遮罩；
///   · 按「左右分配比例」把窄条分成两段：左段 = 音乐播放器，右段 = WorkBuddy；
///   · 悬停左段 → 指示器换成音乐配色；悬停右段 → 换成 WorkBuddy 琥珀；
///   · 双击左段 → 打开音乐播放器；双击右段 → 打开 WorkBuddy。
///
/// 注意：这里**不再"鼠标移入就展开岛"**。上一版这么做，而窄条正好常驻在屏幕顶部中央
/// （指针常年停在那），于是展开 → 超时收起 → 又落回指针下，每 N 秒自己开合一次；
/// 而且没有内容时展开出来的是个空壳（宽度塌缩 + 圆角 → 屏幕上出现一个竖立白色椭圆）。
/// 现在只有"确实有内容"时岛才会自己展开，窄条只负责这两件事：悬停反馈 + 双击开应用。
/// </summary>
public sealed class SnapStripWindow : Window
{
    /// <summary>双击某一段（左=音乐 / 右=WorkBuddy）。仅待机空闲模式会触发。</summary>
    public event EventHandler<StripZone>? ZoneDoubleClicked;

    /// <summary>悬停一段时间后请求展开岛（信息展示模式用，v0.2.11 需求 2 的补充规则）。</summary>
    public event EventHandler? HoverExpandRequested;

    /// <summary>是否处于「待机快捷模式」：true=左右分区配色 + 双击唤起；false=纯信息态。</summary>
    private bool _standbyMode = true;

    /// <summary>悬停展开的延时（2023 手感的"意图停留"，避免路过就弹开）。</summary>
    private const int HoverExpandDelayMs = 260;

    /// <summary>左右分段比例（百分比，来自设置里的"区域比例"滑块；50 = 对半）。</summary>
    public Func<int>? SplitRatioProvider { get; set; }

    /// <summary>可视条厚度（DIP）。</summary>
    public const double BarThickness = 12;

    /// <summary>可视条长度（DIP）。</summary>
    public const double BarLength = 124;

    /// <summary>窗口四边命中余量（DIP）。</summary>
    private const double HitPadding = 8;

    /// <summary>悬停放大倍数（需求要求"轻微放大"，1.01~1.03 太弱、>1.1 会顶出窗口）。</summary>
    private const double HoverScale = 1.06;

    private readonly Border _bar;
    private readonly StripIndicator _indicator;
    private readonly ScaleTransform _hoverScale = new(1, 1);

    public SnapStripWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "NotifyHub 吸附条";

        _indicator = new StripIndicator
        {
            Mode = StripMode.Music,
            Margin = new Thickness(5, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _bar = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xF0, 0x3A, 0x3A, 0x40), 0),
                    new GradientStop(Color.FromArgb(0xE6, 0x14, 0x14, 0x18), 1),
                },
            },
            Child = _indicator,
            RenderTransform = _hoverScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        Content = _bar;
        Opacity = 0;
        Cursor = Cursors.Hand;

        MouseEnter += (_, _) =>
        {
            ApplyHover(true);
            ArmHoverExpand();
        };

        MouseLeave += (_, _) =>
        {
            ApplyHover(false);
            DisarmHoverExpand();
        };

        MouseMove += (_, e) =>
        {
            // 需求 4：分区配色只在待机空闲模式生效
            if (_standbyMode)
            {
                UpdateHoverZone(e.GetPosition(this).X);
            }
        };

        // 双击分区打开对应应用（单击不再承担"展开"职责，否则双击会被第一下打断）
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2)
            {
                return;
            }

            if (!_standbyMode)
            {
                // 信息展示模式不接管双击（需求 2：快捷唤起只在待机空闲模式生效）
                LogService.Shared.Write(LogLevel.Debug, "IslandStrip", "信息模式忽略双击", "待机快捷唤起已关闭");
                return;
            }

            var zone = ZoneAt(e.GetPosition(this).X);
            if (zone == StripZone.None)
            {
                return;
            }

            e.Handled = true;
            ZoneDoubleClicked?.Invoke(this, zone);
        };
    }

    /// <summary>当前悬停分区（诊断用）。</summary>
    public StripZone HoverZone { get; private set; } = StripZone.None;

    /// <summary>当前是否处于待机快捷模式（诊断用）。</summary>
    public bool IsStandbyMode => _standbyMode;

    private DispatcherTimer? _hoverExpandTimer;

    /// <summary>
    /// 切"待机快捷模式 / 信息展示模式"（v0.2.11 需求 2/4）。
    /// 待机：悬停分区换色、双击唤起；信息：悬停只放大、停留片刻则展开看信息，不换待机配色。
    /// </summary>
    public void SetStandbyMode(bool standby)
    {
        if (_standbyMode == standby)
        {
            return;
        }

        _standbyMode = standby;

        // 切到信息模式时立刻清掉待机配色，避免残留黄/琥珀块
        if (!standby)
        {
            HoverZone = StripZone.None;
            _indicator.HoverZone = StripZone.None;
            _hoverExpandTimer?.Stop();
        }
    }

    /// <summary>信息模式下，悬停停留 260ms 就请求展开岛（把音乐控制台/歌词/任务显示出来）。</summary>
    private void ArmHoverExpand()
    {
        if (_standbyMode)
        {
            return;
        }

        _hoverExpandTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverExpandDelayMs) };
        _hoverExpandTimer.Stop();
        _hoverExpandTimer.Tick -= OnHoverExpandTick;
        _hoverExpandTimer.Tick += OnHoverExpandTick;
        _hoverExpandTimer.Start();
    }

    private void DisarmHoverExpand() => _hoverExpandTimer?.Stop();

    private void OnHoverExpandTick(object? sender, EventArgs e)
    {
        _hoverExpandTimer?.Stop();

        if (_standbyMode || !IsMouseOver)
        {
            return;
        }

        HoverExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把窗口内 x 换算成左/右分区。</summary>
    public StripZone ZoneAt(double x) => ZoneAt(x, Width, SplitRatioProvider?.Invoke() ?? 50);

    /// <summary>
    /// 分区判定的纯函数版（自检直接断言，不需要窗口）：
    /// 宽度 ≤1 视为无效；其余按比例切，x 落在切点左侧算左段。
    /// </summary>
    public static StripZone ZoneAt(double x, double width, int splitPercent)
    {
        if (width <= 1)
        {
            return StripZone.None;
        }

        var ratio = Math.Clamp(splitPercent, 25, 75) / 100.0;
        return x < width * ratio ? StripZone.Left : StripZone.Right;
    }

    /// <summary>悬停反馈：轻微放大 + 指示器换分区配色。</summary>
    private void ApplyHover(bool hovering)
    {
        var target = hovering ? HoverScale : 1.0;
        var scale = new DoubleAnimation(target, TimeSpan.FromMilliseconds(hovering ? 160 : 220))
        {
            // 放大用回弹（略冲过头再回来），收起用 ease-out：手感"柔软"而不是机械
            EasingFunction = hovering
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 }
                : new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        _hoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _hoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);

        if (!hovering)
        {
            HoverZone = StripZone.None;
            _indicator.HoverZone = StripZone.None;
        }
    }

    private void UpdateHoverZone(double x)
    {
        var zone = ZoneAt(x);
        if (zone == HoverZone)
        {
            return;
        }

        HoverZone = zone;
        _indicator.HoverZone = zone;
    }

    /// <summary>当前指示器形态（诊断用）。</summary>
    public StripMode Mode => _indicator.Mode;

    /// <summary>指示器诊断快照（--strip-shot / 自检用）。</summary>
    public string IndicatorDebug =>
        $"mode={_indicator.Mode} done={_indicator.IsDone} phase={_indicator.Phase:F2} " +
        $"bar={BarThickness}x{BarLength} draw={_indicator.ActualWidth:F0}x{_indicator.ActualHeight:F0} " +
        $"opacity={_bar.Opacity:F2}";

    /// <summary>当前可视条尺寸是否已经是 v0.2.8 的新规格（自检用）。</summary>
    public static bool UsesNewGeometry => BarThickness >= 12 && BarLength >= 120;

    /// <summary>
    /// 把细条摆到屏幕顶部中央并淡入（已可见时只刷新状态与位置）。
    /// <paramref name="mode"/> 决定跑道指示器的形态，<paramref name="doneState"/> 为完成态（整条转绿）。
    /// </summary>
    public void ShowFor(StripMode mode, bool doneState, Color? accent = null)
    {
        // v0.2.9 需求 1：细条长度按屏幕比例缩放（厚度等比但收敛在 11~18，太粗会像药丸）
        var scale = ScreenMetrics.UiScaleFor(this);
        var barLength = Math.Round(BarLength * scale);
        var barThickness = Math.Clamp(Math.Round(BarThickness * scale), 11, 18);

        // 横条：长 × 细。窗口外扩 HitPadding 扩大命中区（命中余量也按比例放大）。
        var pad = Math.Round(HitPadding * Math.Clamp(scale, 0.9, 1.4));
        var winW = barLength + (pad * 2);
        var winH = barThickness + (pad * 2);

        Width = winW;
        Height = winH;

        _bar.Margin = new Thickness(pad);

        _indicator.Mode = mode;
        _indicator.IsDone = doneState;
        _indicator.AccentColor = accent;

        var area = ScreenMetrics.PrimaryWorkAreaDip(this);
        var gap = 8; // 与岛吸附同款缝隙

        Left = Math.Round(area.Left + ((area.Width - winW) / 2));
        Top = Math.Round(area.Top + gap - pad);

        if (IsVisible)
        {
            // 已在屏上：只更新形态（音乐停/任务开始时不用重新淡入）
            StartBreathing(doneState);
            return;
        }

        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(128)));
        StartBreathing(doneState);
    }

    /// <summary>只更新指示器状态（细条已在屏上时用）。</summary>
    public void UpdateState(StripMode mode, bool doneState, Color? accent = null)
    {
        if (!IsVisible)
        {
            return;
        }

        _indicator.Mode = mode;
        _indicator.IsDone = doneState;
        _indicator.AccentColor = accent;
        StartBreathing(doneState);
    }

    /// <summary>
    /// 呼吸：整条透明度循环（旧版细条的呼吸灯效果保留）。
    /// 完成态呼吸更慢更浅，视觉上"安定下来"。
    /// </summary>
    private void StartBreathing(bool doneState)
    {
        var breathe = new DoubleAnimation(doneState ? 1 : 0.92, doneState ? 0.75 : 0.55,
            TimeSpan.FromMilliseconds(doneState ? 1500 : 900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        _bar.BeginAnimation(OpacityProperty, breathe);
    }

    /// <summary>淡出并隐藏。</summary>
    public void Collapse()
    {
        if (!IsVisible)
        {
            return;
        }

        _bar.BeginAnimation(OpacityProperty, null);
        _bar.Opacity = 1;

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        // 不抢焦点：第一下点击不能被系统当"激活点击"吞掉
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(StripHook);
    }

    /// <summary>WM_MOUSEACTIVATE → MA_NOACTIVATE，保持不抢焦点且点击可达。</summary>
    private IntPtr StripHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }
}
