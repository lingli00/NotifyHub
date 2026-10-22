using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NotifyHub.Interop;

namespace NotifyHub.UI;

/// <summary>
/// 吸附态的「细条」：灵动岛 10 秒无交互吸附到屏幕边后，只显示一条小细条
/// （贴左右边 = 竖条，贴上/下边 = 横条），不占地方；鼠标移入或点击立即展开完整悬浮条。
/// 窗口比可视条大一圈（留 8px 命中余量），"容易点到"就是靠这个 + 128ms 快速淡入。
/// </summary>
public sealed class SnapStripWindow : Window
{
    /// <summary>鼠标移入细条（展开完整岛）。</summary>
    public event EventHandler? ExpandRequested;

    /// <summary>可视条厚度（DIP）。</summary>
    public const double BarThickness = 8;

    /// <summary>可视条长度（DIP）。</summary>
    public const double BarLength = 110;

    /// <summary>窗口四边命中余量（DIP）。</summary>
    private const double HitPadding = 8;

    private bool _vertical;
    private Edge _edge = Edge.Top;

    private readonly Border _bar;
    private readonly Ellipse _dot;

    private enum Edge
    {
        Left,
        Right,
        Top,
        Bottom,
    }

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

        _dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)), // 与岛上状态点同款黄
            Opacity = 0.9,
        };

        _bar = new Border
        {
            CornerRadius = new CornerRadius(4),
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
            Child = _dot,
        };

        Content = _bar;
        Opacity = 0;
        Cursor = Cursors.Hand;

        MouseEnter += (_, _) => ExpandRequested?.Invoke(this, EventArgs.Empty);
        MouseLeftButtonUp += (_, _) => ExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按吸附边摆放细条并淡入（已可见时只刷新位置）。</summary>
    public void ShowFor(string edgeName, bool doneState)
    {
        _edge = edgeName switch
        {
            "Left" => Edge.Left,
            "Right" => Edge.Right,
            "Bottom" => Edge.Bottom,
            _ => Edge.Top,
        };
        _vertical = _edge is Edge.Left or Edge.Right;

        // 竖条：细 × 长；横条：长 × 细。窗口外扩 HitPadding 扩大命中区。
        var barLen = BarLength;
        var barThick = BarThickness;
        var winW = (_vertical ? barThick : barLen) + (HitPadding * 2);
        var winH = (_vertical ? barLen : barThick) + (HitPadding * 2);

        Width = winW;
        Height = winH;

        _bar.Margin = new Thickness(HitPadding);
        _bar.CornerRadius = new CornerRadius(4);

        var area = SystemParameters.WorkArea;
        var gap = 8; // 与岛吸附同款缝隙

        double left, top;

        switch (_edge)
        {
            case Edge.Left:
                left = area.Left + gap - HitPadding;
                top = area.Top + ((area.Height - winH) / 2);
                break;
            case Edge.Right:
                left = area.Right - gap - winW + HitPadding;
                top = area.Top + ((area.Height - winH) / 2);
                break;
            case Edge.Bottom:
                left = area.Left + ((area.Width - winW) / 2);
                top = area.Bottom - gap - winH + HitPadding;
                break;
            default:
                left = area.Left + ((area.Width - winW) / 2);
                top = area.Top + gap - HitPadding;
                break;
        }

        Left = Math.Round(left);
        Top = Math.Round(top);

        // 完成态显示绿点，其余时间黄点
        _dot.Fill = new SolidColorBrush(doneState
            ? Color.FromRgb(0x34, 0xC7, 0x59)
            : Color.FromRgb(0xFF, 0xD6, 0x0A));

        if (IsVisible)
        {
            return;
        }

        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(128)));
    }

    /// <summary>淡出并隐藏。</summary>
    public void Collapse()
    {
        if (!IsVisible)
        {
            return;
        }

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
