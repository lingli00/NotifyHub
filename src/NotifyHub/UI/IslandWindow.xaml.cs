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
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 灵动岛 / 悬浮条：单一深色圆角长条，同一面板内横排展示
///   [♪] 歌名-歌手 | [上一首][播放/暂停][下一首] | 单行实时动作。
/// 固定模式锁定屏幕顶部居中；自由模式下按住空白区可拖动（左键/右键都行），
/// 10 秒无交互自动吸附到最近的一条屏幕边（四边都算）：
/// 贴左右边收成竖细条、贴上/下边收成横细条（SnapStripWindow），
/// 鼠标移入细条立即展开完整悬浮条，离开 1.4s 再收回细条。
///
/// 动效参数（严格要求）：
///   展开 180ms ease-out｜状态文字切换 120ms 淡入｜完成停留 600ms｜收缩 220ms ease-in。
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

    /// <summary>无交互多久后自动吸附到最近边。</summary>
    private static readonly TimeSpan IdleSnapDelay = TimeSpan.FromSeconds(10);

    /// <summary>吸附时条体与屏幕边之间的视觉缝隙。</summary>
    private const double EdgeGap = 8;

    /// <summary>滑入/滑出动画时长。</summary>
    private const int SlideDurationMs = 240;

    private readonly DispatcherTimer _statusTimer = new();
    private readonly DispatcherTimer _musicTimer = new();
    private readonly DispatcherTimer _idleTimer = new();
    private readonly DispatcherTimer _slideTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly DispatcherTimer _returnTimer = new();

    private bool _statusVisible;
    private bool _musicVisible;
    private bool _musicPlaying;
    private bool _fullscreen;
    private bool _hidden = true;
    private bool _dragging;
    private bool _collapsing;
    private MediaInfo? _media;

    // 动画防粘连：每次显隐都递增令牌，过期动画的 Completed 不再动窗口状态
    private int _animToken;

    // 吸附状态
    private bool _snapped;
    private bool _peeked;
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
        Left,
        Right,
        Top,
        Bottom,
    }

    public IslandWindow()
    {
        InitializeComponent();

        // 两组内容默认都收起来：只有真正有任务/音乐时才显示，
        // 否则第一次弹出会带出 XAML 里的占位文字（"正在执行任务…"）
        StatusGroup.Visibility = Visibility.Collapsed;
        MusicGroup.Visibility = Visibility.Collapsed;
        Divider.Visibility = Visibility.Collapsed;

        _statusTimer.Tick += (_, _) => OnStatusHoldElapsed();
        _musicTimer.Tick += (_, _) => OnMusicGraceElapsed();
        _idleTimer.Interval = IdleSnapDelay;
        _idleTimer.Tick += (_, _) => OnIdleElapsed();
        _slideTimer.Tick += (_, _) => OnSlideTick();

        // 从细条展开后，鼠标离开 1.4s 自动收回细条（比 10 秒无交互更快回到"条状"）
        _returnTimer.Interval = TimeSpan.FromMilliseconds(1400);
        _returnTimer.Tick += (_, _) =>
        {
            _returnTimer.Stop();

            if (IsFreeMode() && !_hidden && !_dragging && !_sliding && _peeked && !_stripMode)
            {
                SnapToNearestEdge();
            }
        };

        Pill.MouseEnter += OnPillMouseEnter;
        Pill.MouseLeave += OnPillMouseLeave;

        // 窗口是 SizeToContent：文字变长/变短会改变宽度。
        // 固定模式必须跟着重量位置，否则岛会越来越偏（不再居中）；
        // 自由模式保持视觉中心不动，避免宽度变化时整条"往右窜"。
        SizeChanged += OnIslandSizeChanged;
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

    /// <summary>当前是否吸附在屏幕边缘（诊断用）。</summary>
    public bool IsSnapped => _snapped;

    /// <summary>当前吸附边（诊断用，None/Left/Right/Top/Bottom）。</summary>
    public string SnapEdgeName => _snapEdge.ToString();

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
            _peeked = false;
            _stripMode = false;
            _strip?.Collapse();
            _snapEdge = Edge.None;
            _idleTimer.Stop();
            _returnTimer.Stop();
        }

        if (!_hidden && IsVisible && !_dragging && !_sliding)
        {
            PositionIsland();
        }

        if (free && !_hidden)
        {
            RestartIdleTimer();
        }
    }

    // ---------------- 状态文字 ----------------

    private void SetStatus(string text, bool done)
    {
        var fallback = done ? "完成" : "正在执行任务…";
        var display = string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

        // 只许一行：任何换行/连续空白都压平，超长交给 TextTrimming 省略
        display = FlattenToSingleLine(display);

        var changed = !string.Equals(StatusText.Text, display, StringComparison.Ordinal);
        var firstShow = _hidden || !_statusVisible;

        StatusText.Text = display;

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

        // 有任务在跑时不应该吸附到边上（用户正盯着看）
        if (!done && IsFreeMode())
        {
            RestartIdleTimer();
        }
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
        => Divider.Visibility = _musicVisible && _statusVisible ? Visibility.Visible : Visibility.Collapsed;

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

        // 任务收尾后重新开始"无交互倒计时"，否则跑过一次任务就再也不吸附了
        if (IsFreeMode())
        {
            RestartIdleTimer();
        }

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
            _returnTimer.Stop();
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
            _peeked = false;
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

        if (IsFreeMode())
        {
            RestartIdleTimer();
        }
    }

    /// <summary>收缩消失：220ms ease-in（淡出 + 微微上飘），动画确实结束后才 Hide。</summary>
    private void CollapseIsland()
    {
        if (_hidden || _collapsing)
        {
            return;
        }

        _collapsing = true;
        _idleTimer.Stop();
        _returnTimer.Stop();
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
            _peeked = false;
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
    }

    /// <summary>保持当前吸附边不变，按新的条体尺寸重新贴边（文字变长/变短时用）。</summary>
    private void ReapplySnap()
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 1 ? ActualWidth : 400;
        var h = ActualHeight > 1 ? ActualHeight : 76;
        var pad = PillPad;

        switch (_snapEdge)
        {
            case Edge.Left:
                Left = area.Left + EdgeGap - pad;
                break;
            case Edge.Right:
                Left = area.Right - w - EdgeGap + pad;
                break;
            case Edge.Top:
                Top = area.Top + EdgeGap - pad;
                break;
            case Edge.Bottom:
                Top = area.Bottom - h - EdgeGap + pad;
                break;
        }
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
    /// 把位置限制在可视区域内。只允许"阴影留白"出屏，条体本体必须完整可见，
    /// 否则会出现"拖到边上被裁掉一半"的观感。
    /// </summary>
    private (double Left, double Top) ClampToScreen(double left, double top)
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 1 ? ActualWidth : Width;
        var h = ActualHeight > 1 ? ActualHeight : 76;
        var slack = PillPad + 2;

        var minLeft = area.Left - slack;
        var maxLeft = area.Right - w + slack;
        var minTop = area.Top - slack;
        var maxTop = area.Bottom - h + slack;

        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }

    // ---------------- 无交互自动吸附（四边） ----------------

    private void RestartIdleTimer()
    {
        _idleTimer.Stop();

        if (!IsFreeMode() || _hidden || _snapped)
        {
            return;
        }

        _idleTimer.Start();
    }

    private void OnIdleElapsed()
    {
        _idleTimer.Stop();

        if (!IsFreeMode() || _hidden || _dragging || _snapped || _peeked || _sliding)
        {
            return;
        }

        SnapToNearestEdge();
    }

    private void OnPillMouseEnter(object sender, MouseEventArgs e)
    {
        _idleTimer.Stop();
        _returnTimer.Stop();

        if (!IsFreeMode() || _dragging || _sliding)
        {
            return;
        }

        // 细条形态下鼠标其实落不到岛上（岛已隐藏），这里主要防滑动途中抖动
    }

    private void OnPillMouseLeave(object sender, MouseEventArgs e)
    {
        if (!IsFreeMode())
        {
            return;
        }

        if (_peeked && !_stripMode)
        {
            // 从细条展开出来的：离开后 1.4s 收回细条
            _returnTimer.Stop();
            _returnTimer.Start();
            return;
        }

        _peeked = false;
        RestartIdleTimer();
    }

    /// <summary>吸附到距离最近的那条屏幕边，贴边后整岛收成一条「细条」。</summary>
    private void SnapToNearestEdge()
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 1 ? ActualWidth : 400;
        var h = ActualHeight > 1 ? ActualHeight : 76;
        var pad = PillPad;

        var left = Left;
        var top = Top;

        var distLeft = left - area.Left;
        var distRight = area.Right - (left + w);
        var distTop = top - area.Top;
        var distBottom = area.Bottom - (top + h);

        var min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));

        double targetLeft = left;
        double targetTop = top;
        Edge edge;

        if (Math.Abs(min - distLeft) < 0.01)
        {
            targetLeft = area.Left + EdgeGap - pad;
            edge = Edge.Left;
        }
        else if (Math.Abs(min - distRight) < 0.01)
        {
            targetLeft = area.Right - w - EdgeGap + pad;
            edge = Edge.Right;
        }
        else if (Math.Abs(min - distTop) < 0.01)
        {
            targetTop = area.Top + EdgeGap - pad;
            edge = Edge.Top;
        }
        else
        {
            targetTop = area.Bottom - h - EdgeGap + pad;
            edge = Edge.Bottom;
        }

        _snapEdge = edge;
        _snapped = true;
        _peeked = false;

        // 先顺滑滑到边上，落定后整岛收成细条
        SlideTo(targetLeft, targetTop, SlideDurationMs, SwitchToStrip);
    }

    /// <summary>滑到边后：岛快速淡出，屏幕边只留一条细条（左右边 = 竖条）。</summary>
    private void SwitchToStrip()
    {
        if (_stripMode || _dragging)
        {
            return;
        }

        _stripMode = true;
        EnsureStrip().ShowFor(_snapEdge.ToString(), IsDoneState);

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

    /// <summary>细条被移入/点击（或吸附期间来了新状态）：整岛重新展开，1.4s 无鼠标再收回。</summary>
    private void ExpandFromStrip()
    {
        if (!_stripMode)
        {
            return;
        }

        var token = ++_animToken;
        _stripMode = false;
        _strip?.Collapse();
        _snapped = false;
        _peeked = true;

        // 贴边全展开：留出与吸附同款缝隙
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 1 ? ActualWidth : 400;
        var h = ActualHeight > 1 ? ActualHeight : 76;
        var pad = PillPad;

        switch (_snapEdge)
        {
            case Edge.Left:
                Left = area.Left + EdgeGap - pad;
                break;
            case Edge.Right:
                Left = area.Right - w - EdgeGap + pad;
                break;
            case Edge.Top:
                Top = area.Top + EdgeGap - pad;
                break;
            case Edge.Bottom:
                Top = area.Bottom - h - EdgeGap + pad;
                break;
        }

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

        // 执行中有实时状态 → 保持展开（10 秒无新状态由闲置计时器收条）；
        // 只有"无状态/完成态"的展开（比如用户点开看音乐）才 1.4s 自动收回
        _returnTimer.Stop();

        if (!_statusVisible || IsDoneState)
        {
            _returnTimer.Start();
        }
        else
        {
            RestartIdleTimer();
        }
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
        RestartIdleTimer();
        PlayPauseRequested?.Invoke();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        RestartIdleTimer();
        NextRequested?.Invoke();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        RestartIdleTimer();
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
                // 阴影留白（Pill 以外的透明区）一律放行给下层窗口，
                // 否则一条 14px 的隐形边框会挡住桌面/其他程序的点击。
                if (!IsInsidePill(ScreenPointOf(lParam)))
                {
                    handled = true;
                    return new IntPtr(NativeMethods.HTTRANSPARENT);
                }

                return IntPtr.Zero;

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
        _idleTimer.Stop();
        StopSlide();

        // 吸附/细条态直接拖：先清状态，落手位置由用户决定
        _snapped = false;
        _peeked = false;
        _stripMode = false;
        _strip?.Collapse();
        _snapEdge = Edge.None;
        _returnTimer.Stop();

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

        // 别让用户把条拖到屏幕外找不回来：至少留 80×40 像素可见
        var area = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        var width = _dragStartWindow.Right - _dragStartWindow.Left;
        var height = _dragStartWindow.Bottom - _dragStartWindow.Top;

        var minX = (int)(area.Left * sx) - width + 80;
        var maxX = (int)(area.Right * sx) - 80;
        var minY = (int)(area.Top * sy) - height + 40;
        var maxY = (int)(area.Bottom * sy) - 40;

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
            RestartIdleTimer();
            return;
        }

        _dragMoved = false;
        SyncWpfPositionFromHwnd();

        var (left, top) = ClampToScreen(Left, Top);
        Left = left;
        Top = top;

        PositionCommitted?.Invoke(left, top);
        RestartIdleTimer();
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

    /// <summary>命中测试：是否落在播放按钮等交互控件上（这些位置不参与拖拽）。</summary>
    private bool HitIsInteractive(double clientX, double clientY)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var dip = new Point(clientX / dpi.DpiScaleX, clientY / dpi.DpiScaleY);

            var hit = InputHitTest(dip) as DependencyObject;
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
            // 忽略：拿不到就当成空白区，允许拖
        }

        return false;
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
        _idleTimer.Stop();
        _slideTimer.Stop();
        _returnTimer.Stop();
        _strip?.Close();
        _strip = null;

        if (Current == this)
        {
            Current = null;
        }

        base.OnClosed(e);
    }
}
