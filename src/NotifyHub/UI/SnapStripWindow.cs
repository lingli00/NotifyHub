using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NotifyHub.Interop;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 吸附态的「细条」（v0.2.8 需求 4 改造）：灵动岛收纳后只留一条窄条，
/// 鼠标移入或点击立即展开完整岛。
///
/// 和旧版的差别：中间的静态小圆点换成了 <see cref="StripIndicator"/>（跑道指示器），
/// 能区分「仅音乐 / 仅任务 / 音乐+任务」三种状态；整条保留原有的呼吸效果
/// （整体透明度 1 ↔ 0.55 循环），不再是一个不动的点。
///
/// 窗口仍比可视条大一圈（左右各 8px 命中余量），"容易点到"靠它 + 128ms 快速淡入。
/// </summary>
public sealed class SnapStripWindow : Window
{
    /// <summary>鼠标移入细条（展开完整岛）。</summary>
    public event EventHandler? ExpandRequested;

    /// <summary>可视条厚度（DIP）。</summary>
    public const double BarThickness = 12;

    /// <summary>可视条长度（DIP）。</summary>
    public const double BarLength = 124;

    /// <summary>窗口四边命中余量（DIP）。</summary>
    private const double HitPadding = 8;

    private readonly Border _bar;
    private readonly StripIndicator _indicator;

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
        };

        Content = _bar;
        Opacity = 0;
        Cursor = Cursors.Hand;

        MouseEnter += (_, _) => ExpandRequested?.Invoke(this, EventArgs.Empty);
        MouseLeftButtonUp += (_, _) => ExpandRequested?.Invoke(this, EventArgs.Empty);
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
    public void ShowFor(StripMode mode, bool doneState)
    {
        // 横条：长 × 细。窗口外扩 HitPadding 扩大命中区。
        var winW = BarLength + (HitPadding * 2);
        var winH = BarThickness + (HitPadding * 2);

        Width = winW;
        Height = winH;

        _bar.Margin = new Thickness(HitPadding);

        _indicator.Mode = mode;
        _indicator.IsDone = doneState;

        var area = ScreenMetrics.PrimaryWorkAreaDip(this);
        var gap = 8; // 与岛吸附同款缝隙

        Left = Math.Round(area.Left + ((area.Width - winW) / 2));
        Top = Math.Round(area.Top + gap - HitPadding);

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
    public void UpdateState(StripMode mode, bool doneState)
    {
        if (!IsVisible)
        {
            return;
        }

        _indicator.Mode = mode;
        _indicator.IsDone = doneState;
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
