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
/// 收纳窄条（v0.2.10 需求 2 重做，v0.2.17 工单B 规则 6 定稿）。
///
/// 交互规则：
///   · 悬停：整条**轻微放大**（1.06 倍 + 回弹）+ 左右分区配色，**不展开岛**；
///   · 悬停 + **单击**：展开信息面板（含暂存的最新通知）；
///   · 悬停 + **双击**：唤起对应应用（左=音乐播放器 / 右=WorkBuddy）并置顶；
///   · 单击/双击用 260ms 定时器消歧：第一下排展开，第二下到达就改判双击。
///
/// 注意：**悬停本身绝不展开岛**。旧版"悬停 180ms 自动展开"正是"空闲常驻时岛无规律
/// 自动展开"的投诉来源——窄条常驻屏幕顶部中央，指针路过就弹。现在展开只能靠
/// 单击、新通知、开始播放、启动任务（或长按上滑前的主动展开）。
/// </summary>
public sealed class SnapStripWindow : Window
{
    /// <summary>双击某一段（左=音乐 / 右=WorkBuddy）：唤起对应应用并置顶。</summary>
    public event EventHandler<StripZone>? ZoneDoubleClicked;

    /// <summary>
    /// 单击窄条（v0.2.17 工单B 规则 6）：展开信息面板（含暂存的最新通知）。
    /// 与双击的区分靠 <see cref="ClickConfirmMs"/> 消歧：第一下只排定时器，
    /// 第二下在定时器内到达就改判双击（取消展开）。
    /// </summary>
    public event EventHandler? ZoneClicked;

    /// <summary>是否处于「待机快捷模式」：true=左右分区配色 + 双击唤起；false=纯信息态。</summary>
    private bool _standbyMode = true;

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

        MouseEnter += (_, _) => ApplyHover(true);

        // 有的机器上 MouseEnter 之后就不再派发 MouseMove（NOACTIVATE 分层窗），
        // 所以进入那一刻先把分区定下来，别等 MouseMove（需求 2 的分区配色靠它）。
        MouseEnter += (_, e) =>
        {
            if (_standbyMode)
            {
                UpdateHoverZone(e.GetPosition(this).X);
            }
        };

        MouseLeave += (_, _) =>
        {
            ApplyHover(false);
            DisarmClickExpand();
        };

        MouseMove += (_, e) =>
        {
            // 需求 4：分区配色只在待机空闲模式生效
            if (_standbyMode)
            {
                UpdateHoverZone(e.GetPosition(this).X);
            }
        };

        // v0.2.17 工单B 规则 6：单击 = 展开信息面板；双击 = 唤起对应应用。
        // 两段消歧：ClickCount==1 只排 260ms 确认定时器；ClickCount==2 取消定时器改判双击。
        // （旧版"单击不做事"是为了避免吃掉双击；现在有定时器，两者可以共存。）
        MouseLeftButtonDown += (_, e) =>
        {
            var zone = ZoneAt(e.GetPosition(this).X);
            if (zone == StripZone.None)
            {
                return;
            }

            if (e.ClickCount >= 2)
            {
                DisarmClickExpand();
                e.Handled = true;
                ZoneDoubleClicked?.Invoke(this, zone);
                return;
            }

            ArmClickExpand(zone);
        };
    }

    /// <summary>当前悬停分区（诊断用）。</summary>
    public StripZone HoverZone { get; private set; } = StripZone.None;

    /// <summary>当前是否处于待机快捷模式（诊断用）。</summary>
    public bool IsStandbyMode => _standbyMode;

    /// <summary>
    /// 单击→展开 的确认延时（v0.2.17 工单B 规则 6）：第一下点击后等这么久，
    /// 期间没有第二下就判"单击"并展开岛；来了第二下就改判"双击"唤起应用。
    /// </summary>
    private const int ClickConfirmMs = 260;

    private DispatcherTimer? _clickTimer;
    private StripZone _pendingZone = StripZone.None;

    /// <summary>
    /// 切"待机快捷模式 / 信息展示模式"（v0.2.11 需求 2/4）。
    /// 待机：悬停分区换色、双击唤起；信息：悬停只放大，不换待机配色。
    /// v0.2.17 起两种模式都响应单击展开与双击唤起（规则 6 不区分模式）。
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
        }
    }

    /// <summary>排一个"单击展开"确认；第二下点击到来时由 <see cref="OnMouseLeftButtonDown"/> 取消。</summary>
    private void ArmClickExpand(StripZone zone)
    {
        _pendingZone = zone;
        _clickTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ClickConfirmMs) };
        _clickTimer.Stop();
        _clickTimer.Tick -= OnClickConfirmTick;
        _clickTimer.Tick += OnClickConfirmTick;
        _clickTimer.Start();
    }

    private void DisarmClickExpand()
    {
        _clickTimer?.Stop();
        _pendingZone = StripZone.None;
    }

    private void OnClickConfirmTick(object? sender, EventArgs e)
    {
        _clickTimer?.Stop();

        if (_pendingZone == StripZone.None)
        {
            return;
        }

        // **不要用 IsMouseOver**（v0.2.12 需求 4 的根因）：
        // 窄条是 WS_EX_NOACTIVATE + AllowsTransparency 的分层窗，鼠标一走神
        // 系统就不再派发鼠标消息，IsMouseOver 随时可能是 false。
        // 这里直接比对光标与窗口矩形——手还压在窄条上才算数。
        if (!IsCursorInside())
        {
            LogService.Shared.Routine("IslandStrip", "单击展开取消", "光标已离开窄条");
            DisarmClickExpand();
            return;
        }

        LogService.Shared.Write(LogLevel.Debug, "IslandStrip", "单击窄条", $"zone={_pendingZone}");
        ZoneClicked?.Invoke(this, EventArgs.Empty);
        DisarmClickExpand();
    }

    /// <summary>光标是否落在窄条窗口矩形内（物理像素比较，避开 DPI 换算误差）。</summary>
    public bool IsCursorInside()
    {
        try
        {
            if (!IsLoaded || Width < 1 || Height < 1)
            {
                return false;
            }

            if (!NativeMethods.GetCursorPos(out var point))
            {
                return false;
            }

            var scale = ScreenMetrics.ScaleOf(this);
            if (scale <= 0.1)
            {
                scale = 1;
            }

            var left = Left * scale;
            var top = Top * scale;
            var right = left + (Width * scale);
            var bottom = top + (Height * scale);

            return point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
        }
        catch
        {
            return false;
        }
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
