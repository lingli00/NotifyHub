using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using NotifyHub.Interop;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 灵动岛 / 悬浮条：单一深色毛玻璃圆角长条，同一面板内横排展示
///   [♪] 歌名-歌手 | [上一首][播放/暂停][下一首] | WorkBuddy 单行实时动作文字。
/// 固定模式锁定屏幕顶部居中；自由模式下按住空白区域可拖到任意位置（贴边吸附、不越界）。
/// 动效参数（严格要求）：
///   展开 180ms ease-out｜状态文字切换 120ms 淡入｜完成停留 600ms｜收缩 220ms ease-in。
/// 全屏观影/游戏时整套自动隐藏；无音乐时音乐组隐藏；无任务时任务组隐藏。
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

    private readonly DispatcherTimer _statusTimer = new();
    private readonly DispatcherTimer _musicTimer = new();

    private bool _statusVisible;
    private bool _musicVisible;
    private bool _musicPlaying;
    private bool _fullscreen;
    private bool _hidden = true;
    private bool _dragging;
    private MediaInfo? _media;

    // 拖拽状态（NOACTIVATE 窗口不能用 DragMove，手动按光标位移换算 DIP）
    private double _dragStartCursorX;
    private double _dragStartCursorY;
    private double _dragStartLeft;
    private double _dragStartTop;
    private double _dpiScaleX = 1;
    private double _dpiScaleY = 1;

    public IslandWindow()
    {
        InitializeComponent();

        _statusTimer.Tick += (_, _) => OnStatusHoldElapsed();
        _musicTimer.Tick += (_, _) => OnMusicGraceElapsed();

        // 窗口是 SizeToContent：文字变长/变短会改变宽度。
        // 固定模式下必须跟着重量位置，否则岛会越来越偏（不再居中）。
        SizeChanged += (_, _) =>
        {
            if (ModeProvider?.Invoke() != "Free" && IsLoaded)
            {
                RecenterFixed();
            }
        };
    }

    /// <summary>固定模式：水平居中、贴顶 10px。可重复调用（尺寸变化后纠偏）。</summary>
    private void RecenterFixed()
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 1 ? ActualWidth : 420;
        Left = Math.Round(area.Left + ((area.Width - w) / 2));
        Top = Math.Round(area.Top + 10);
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

    /// <summary>小岛当前是否可见（诊断用）。</summary>
    public bool IsShowing => !_hidden && IsVisible;

    /// <summary>显示“任务执行中”状态（单行实时动作文字）。</summary>
    public void ShowRunning(string text)
    {
        EnsureCreated();
        SetStatus(text, done: false);
    }

    /// <summary>显示“任务完成”：对勾 + 完成文字，600ms 后 220ms 收缩。</summary>
    public void ShowDone(string text)
    {
        EnsureCreated();
        SetStatus(text, done: true);
    }

    /// <summary>供外部（命令行/管道）调用的统一入口。</summary>
    public static void ShowTask(string text, bool done)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("应用未初始化");

        dispatcher.Invoke(() =>
        {
            if (Current is null)
            {
                Current = new IslandWindow();
            }

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
        Pill.Cursor = ModeProvider?.Invoke() == "Free" ? Cursors.SizeAll : Cursors.Arrow;

        if (!_hidden && IsVisible && !_dragging)
        {
            PositionIsland();
        }
    }

    private void EnsureCreated()
    {
        if (Current is null)
        {
            Current = this;
        }
    }

    private void SetStatus(string text, bool done)
    {
        var display = string.IsNullOrWhiteSpace(text) ? (done ? "任务完成" : "正在执行任务…") : text.Trim();
        var changed = !string.Equals(StatusText.Text, display, StringComparison.Ordinal);
        StatusText.Text = display;

        // 状态文字切换：120ms 平滑淡入，面板高度固定不变
        if (changed)
        {
            var fade = new DoubleAnimation(0.25, 1, TextSwapDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            StatusText.BeginAnimation(OpacityProperty, fade);
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
    }

    private void UpdateMusicGroup()
    {
        var enabled = MusicEnabledProvider?.Invoke() ?? true;
        _musicVisible = enabled && (_musicPlaying || _musicTimer.IsEnabled);

        MusicGroup.Visibility = _musicVisible ? Visibility.Visible : Visibility.Collapsed;

        if (_media is not null)
        {
            var title = string.IsNullOrWhiteSpace(_media.Title) ? "未知曲目" : _media.Title;
            var artist = string.IsNullOrWhiteSpace(_media.Artist) ? _media.AppName : _media.Artist;
            MusicText.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";
            PlayPauseGlyph.Data = (Geometry)FindResource(_media.IsPlaying ? "GlyphPause" : "GlyphPlay");
        }

        UpdateChrome();
    }

    /// <summary>分隔线只在音乐与任务两组同时可见时出现。</summary>
    private void UpdateChrome()
    {
        Divider.Visibility = _musicVisible && _statusVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnMusicGraceElapsed()
    {
        _musicTimer.Stop();
        UpdateMusicGroup();
        Evaluate();
    }

    private void OnStatusHoldElapsed()
    {
        _statusTimer.Stop();

        if (!_statusVisible)
        {
            return;
        }

        _statusVisible = false;
        StopBreathing();

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

    /// <summary>显隐调度：全屏强制隐藏；有任务或音乐才显示。</summary>
    private void Evaluate()
    {
        var shouldShow = !_fullscreen && (_statusVisible || _musicVisible);

        if (shouldShow)
        {
            if (_hidden || !IsVisible)
            {
                ShowIsland();
            }
        }
        else if (!_hidden && !_dragging)
        {
            CollapseIsland();
        }
    }

    /// <summary>展开：180ms ease-out（透明度 + 轻微放大）。</summary>
    private void ShowIsland()
    {
        _hidden = false;

        PositionIsland();
        Show();
        UpdateLayout();

        if (ModeProvider?.Invoke() != "Free")
        {
            // SizeToContent 后宽度可能变化，固定模式再校正一次居中
            PositionIsland();
        }

        Pill.Opacity = 0;
        IslandScale.ScaleX = 0.9;
        IslandScale.ScaleY = 0.9;

        var fade = new DoubleAnimation(0, 1, ExpandDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Pill.BeginAnimation(OpacityProperty, fade);

        var scale = new DoubleAnimation(0.9, 1, ExpandDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    /// <summary>收缩消失：220ms ease-in，之后回到隐藏态。</summary>
    private void CollapseIsland()
    {
        if (_hidden)
        {
            return;
        }

        StopBreathing();

        var fade = new DoubleAnimation(Pill.Opacity, 0, CollapseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            _hidden = true;
            Hide();
            ResetVisual();
            Hidden?.Invoke(this, EventArgs.Empty);
        };
        Pill.BeginAnimation(OpacityProperty, fade);

        var scale = new DoubleAnimation(1, 0.94, CollapseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void ResetVisual()
    {
        Pill.BeginAnimation(OpacityProperty, null);
        Pill.Opacity = 1;
        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        IslandScale.ScaleX = 0.9;
        IslandScale.ScaleY = 0.9;

        if (!_statusVisible)
        {
            StatusGroup.Visibility = Visibility.Collapsed;
        }

        if (!_musicVisible)
        {
            MusicGroup.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>按模式定位：Free=已保存位置（有效时），否则顶部居中。</summary>
    private void PositionIsland()
    {
        var free = ModeProvider?.Invoke() == "Free";

        if (free)
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

    /// <summary>把位置限制在可视区域内（悬浮条不能完全被拖出屏幕）。</summary>
    private (double Left, double Top) ClampToScreen(double left, double top)
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 1 ? ActualWidth : Width;
        var h = ActualHeight > 1 ? ActualHeight : 60;

        var minLeft = area.Left - (w * 0.35);            // 允许少量出界，但不至于消失
        var maxLeft = area.Right - (w * 0.65);
        var minTop = area.Top - 4;
        var maxTop = area.Bottom - h - 2;

        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }

    /// <summary>贴边吸附：距离左右/顶部边缘 24px 内自动吸附。</summary>
    private (double Left, double Top) SnapToEdges(double left, double top)
    {
        const double snapDistance = 24;
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 1 ? ActualWidth : Width;
        var h = ActualHeight > 1 ? ActualHeight : 60;

        if (Math.Abs(left - area.Left) < snapDistance)
        {
            left = area.Left + 8;
        }
        else if (Math.Abs(area.Right - (left + w)) < snapDistance)
        {
            left = area.Right - w - 8;
        }

        if (Math.Abs(top - area.Top) < snapDistance)
        {
            top = area.Top + 8;
        }
        else if (Math.Abs(area.Bottom - (top + h)) < snapDistance)
        {
            top = area.Bottom - h - 8;
        }

        return ClampToScreen(left, top);
    }

    /// <summary>执行态呼吸点：透明度 0.35 ↔ 1 循环。</summary>
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

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();

    private void OnNextClick(object sender, RoutedEventArgs e) => NextRequested?.Invoke();

    private void OnPrevClick(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke();

    // ---------------- 自由模式拖拽（手动，不依赖窗口激活） ----------------

    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ModeProvider?.Invoke() != "Free")
        {
            return; // 固定模式：锁定顶部居中，不可拖动
        }

        // 点按钮区域触发功能，只有空白区域才拖拽
        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var cursor = GetCursorPos();
        var source = PresentationSource.FromVisual(this);
        var dpi = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        _dpiScaleX = dpi.M11 <= 0 ? 1 : dpi.M11;
        _dpiScaleY = dpi.M22 <= 0 ? 1 : dpi.M22;

        _dragging = true;
        _dragStartCursorX = cursor.X;
        _dragStartCursorY = cursor.Y;
        _dragStartLeft = Left;
        _dragStartTop = Top;

        Pill.CaptureMouse();
        e.Handled = true;
    }

    private void OnPillMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var cursor = GetCursorPos();
        Left = _dragStartLeft + ((cursor.X - _dragStartCursorX) / _dpiScaleX);
        Top = _dragStartTop + ((cursor.Y - _dragStartCursorY) / _dpiScaleY);
        e.Handled = true;
    }

    private void OnPillMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        try
        {
            Pill.ReleaseMouseCapture();
        }
        catch
        {
            // 忽略
        }

        var (left, top) = SnapToEdges(Left, Top);
        Left = left;
        Top = top;

        PositionCommitted?.Invoke(Left, Top);
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button)
            {
                return true;
            }

            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private static Point GetCursorPos()
    {
        if (NativeMethods.GetCursorPos(out var pt))
        {
            return new Point(pt.X, pt.Y);
        }

        return new Point(0, 0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与通知卡片一致：不激活、不进 Alt-Tab，绝不抢键盘焦点
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        ApplyModeChange();
    }
}
