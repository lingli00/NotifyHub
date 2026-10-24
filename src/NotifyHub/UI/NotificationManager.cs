using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using NotifyHub.Models;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 通知堆叠管理（v0.2.8 需求 1 重写）。
///
/// 位置：
///   * 四角锚点由 <see cref="NotificationLayout"/> 以**卡片本体**几何算出，
///     工作区每次实时向 Win32 取（<see cref="ScreenMetrics"/>），不再依赖会被
///     缓存的 <c>SystemParameters.WorkArea</c>——改缩放/换分辨率都不会错位；
///   * 底部两角以卡片底边为基准，卡片完整落在任务栏之上（旧版会被压下去 8px）。
///
/// 堆叠顺序：
///   * 上角：最新在最上方，旧消息向下；
///   * 下角：最新在最下方，旧消息向上。
///   实现上 index 0 永远是「贴着锚定边的最新一张」，ComputePosition 负责把它映射到上或下。
///
/// 动画：
///   旧版给每张卡片的 Window.Top 挂 DoubleAnimation，多张卡在同一帧重排时
///   动画互相打断，实测出现 118/54/94 这种不等步进（卡片重叠）。
///   现在由一个帧驱动的小引擎统一推进（CompositionTarget.Rendering），
///   位移是集合级计算，步进恒定、不会互相踩。
/// </summary>
public sealed class NotificationManager
{
    /// <summary>窗口宽度（基准 1080p，含左右阴影留白）。实际尺寸见 <see cref="CurrentScale"/>。</summary>
    public const double CardWidth = NotificationLayout.WindowWidth;

    /// <summary>卡片本体高度（基准 1080p），决定纵向堆叠步进。</summary>
    public const double CardHeight = NotificationLayout.CardBodyHeight;

    private static readonly TimeSpan PopInterval = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan MoveDuration = TimeSpan.FromMilliseconds(190);

    /// <summary>
    /// 四角弹窗的固定停留时长（v0.2.17 工单B 规则 8）：**10 秒，写死**。
    /// 岛内已有两项以上内容（音乐 + 任务同时在跑）时新通知改走四角，
    /// 这条路径不跟随"自动收纳时长"配置——那个时长管的是灵动岛，不是卡片。
    /// （旧的"通知时长"配置项 NotificationDurationMs 自本版起不再读取，仅为兼容旧配置保留。）
    /// </summary>
    public const int CornerNoticeDurationMs = 10_000;

    private readonly List<NotificationWindow> _windows = new();
    private readonly Queue<NotifyMessage> _pending = new();
    private readonly System.Windows.Threading.DispatcherTimer _pump = new() { Interval = PopInterval };
    private readonly LogService _log;
    private readonly Func<AppConfig> _configProvider;
    private readonly SoundService? _sound;

    // ---- 帧驱动重排 ----
    private readonly Dictionary<NotificationWindow, MoveState> _moves = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>上一帧的合成时间戳（FrameClock 去重用，v0.2.9 需求 1）。</summary>
    private TimeSpan _lastFrameTime = TimeSpan.MinValue;
    private bool _ticking;

    private sealed class MoveState
    {
        public double FromLeft;
        public double FromTop;
        public double ToLeft;
        public double ToTop;
        public double StartMs;
    }

    public NotificationManager(LogService log, Func<AppConfig> configProvider, SoundService? sound = null)
    {
        _log = log;
        _configProvider = configProvider;
        _sound = sound;
        _pump.Tick += (_, _) => PumpNext();
    }

    /// <summary>演示模式：点击只记日志，不做窗口定位（避免自测时乱操作其他程序）。</summary>
    public bool DemoMode { get; set; }

    public int ActiveCount => _windows.Count;

    public int PendingCount => _pending.Count;

    public IReadOnlyList<NotificationWindow> ActiveWindows => _windows;

    public void Show(NotifyMessage message)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => Show(message)));
            return;
        }

        _pending.Enqueue(message);

        if (!_pump.IsEnabled)
        {
            PumpNext();
            _pump.Start();
        }
    }

    /// <summary>关闭全部卡片（退出或暂停时用）。</summary>
    public void Clear()
    {
        _pending.Clear();
        _pump.Stop();

        foreach (var window in _windows.ToList())
        {
            window.Dismiss(animate: false);
        }

        _windows.Clear();
        _moves.Clear();
        StopTicking();
    }

    /// <summary>按当前四角配置与屏幕重新摆放所有卡片（配置/DPI/分辨率变化时调用）。</summary>
    public void Relayout(bool animate = true)
    {
        if (_windows.Count == 0)
        {
            return;
        }

        var work = CurrentWorkArea();
        var corner = NotificationLayout.Parse(_configProvider().NotificationCorner);
        var scale = ScreenMetrics.UiScale(work);

        for (var i = 0; i < _windows.Count; i++)
        {
            var (left, top) = NotificationLayout.WindowOrigin(work, corner, i, scale);
            MoveTo(_windows[i], left, top, animate);
        }
    }

    /// <summary>当前所有卡片使用的界面缩放（诊断用）。</summary>
    public double CurrentScale => ScreenMetrics.UiScale(CurrentWorkArea());

    /// <summary>当前主屏工作区（DIP，实时 DPI）。</summary>
    private Rect CurrentWorkArea()
    {
        // 用已在屏上的窗口取 DPI 最准（Per-Monitor V2 下窗口自己知道所在屏的缩放）
        var reference = _windows.FirstOrDefault(w => w.IsLoaded) as Visual;
        return ScreenMetrics.PrimaryWorkAreaDip(reference);
    }

    private void MoveTo(NotificationWindow window, double left, double top, bool animate)
    {
        if (!animate || !window.IsVisible)
        {
            _moves.Remove(window);
            window.MoveTo(left, top, animate: false);
            return;
        }

        var current = window.CurrentOrigin;
        if (Math.Abs(current.Left - left) < 0.5 && Math.Abs(current.Top - top) < 0.5)
        {
            _moves.Remove(window);
            return;
        }

        _moves[window] = new MoveState
        {
            FromLeft = current.Left,
            FromTop = current.Top,
            ToLeft = left,
            ToTop = top,
            StartMs = _clock.Elapsed.TotalMilliseconds,
        };

        StartTicking();
    }

    private void StartTicking()
    {
        if (_ticking)
        {
            return;
        }

        _ticking = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopTicking()
    {
        if (!_ticking)
        {
            return;
        }

        _ticking = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>每帧推进所有待移动的卡片（缓动 190ms，ease-out cubic）。</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (_moves.Count == 0)
        {
            StopTicking();
            return;
        }

        // 一帧只推一次（v0.2.9 需求 1）：重排滑动在多窗口场景下会被同帧派发多次
        if (!FrameClock.ShouldTick(ref _lastFrameTime, e))
        {
            return;
        }

        var now = _clock.Elapsed.TotalMilliseconds;
        var duration = MoveDuration.TotalMilliseconds;
        List<NotificationWindow>? finished = null;

        foreach (var pair in _moves.ToList())
        {
            var window = pair.Key;
            var move = pair.Value;

            var t = Math.Clamp((now - move.StartMs) / duration, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);

            var left = move.FromLeft + ((move.ToLeft - move.FromLeft) * eased);
            var top = move.FromTop + ((move.ToTop - move.FromTop) * eased);

            if (t >= 1)
            {
                (finished ??= new List<NotificationWindow>()).Add(window);
                window.MoveTo(move.ToLeft, move.ToTop, animate: false);
                continue;
            }

            window.MoveTo(left, top, animate: false);
        }

        if (finished is not null)
        {
            foreach (var window in finished)
            {
                _moves.Remove(window);
            }
        }
    }

    private void PumpNext()
    {
        if (_pending.Count == 0)
        {
            _pump.Stop();
            return;
        }

        var config = _configProvider();
        var max = Math.Clamp(config.MaxStack, 1, AppConfig.MaxStackLimit);
        var work = CurrentWorkArea();
        var scale = ScreenMetrics.UiScale(work);
        max = Math.Min(max, NotificationLayout.MaxFit(work, scale));

        if (_windows.Count >= max)
        {
            // 位置已满，等卡片关闭时由 OnWindowDismissed 继续泵
            return;
        }

        var message = _pending.Dequeue();
        var window = new NotificationWindow(
            message,
            CornerNoticeDurationMs,
            config.MarqueeSpeed,
            config,
            scale);
        window.Dismissed += OnWindowDismissed;
        window.Clicked += OnWindowClicked;
        window.DisplayChanged += OnWindowDisplayChanged;

        var corner = NotificationLayout.Parse(config.NotificationCorner);

        // index 0 = 贴着锚定边的最新一张（上角=最上，下角=最下）
        _windows.Insert(0, window);
        Relayout(animate: true);

        var (left, top) = NotificationLayout.WindowOrigin(work, corner, 0, scale);
        window.ShowAt(left, top, animate: true, fromLeft: NotificationLayout.IsLeftAligned(corner));

        _log.Info("通知", $"弹出第 {_windows.Count} 张卡",
            $"corner={corner} pos={left:F0},{top:F0} scale={scale:F2} " +
            $"stack={(NotificationLayout.StacksUpward(corner) ? "向上" : "向下")}");

        // 消息通知提示音：真正上屏时才响，被过滤的消息不会走到这里
        _sound?.PlayMessage(config);
    }

    /// <summary>主题变更：把所有还在屏上的卡片一起换装（需求 3 全局生效）。</summary>
    public void ApplyTheme(ThemePalette palette)
    {
        foreach (var window in _windows)
        {
            try
            {
                window.ApplyTheme(palette);
            }
            catch
            {
                // 单张刷新失败不影响其他卡片
            }
        }
    }

    /// <summary>屏幕 DPI / 分辨率变化后按新几何重摆（Per-Monitor V2 会发 DpiChanged）。</summary>
    public void OnDisplayChanged()
    {
        Relayout(animate: false);
    }

    private void OnWindowDisplayChanged(object? sender, EventArgs e) => OnDisplayChanged();

    private void OnWindowDismissed(object? sender, EventArgs e)
    {
        if (sender is not NotificationWindow window)
        {
            return;
        }

        window.Dismissed -= OnWindowDismissed;
        window.Clicked -= OnWindowClicked;
        window.DisplayChanged -= OnWindowDisplayChanged;
        _windows.Remove(window);
        _moves.Remove(window);
        Relayout();

        if (_pending.Count > 0)
        {
            PumpNext();
        }
    }

    private void OnWindowClicked(object? sender, EventArgs e)
    {
        if (sender is not NotificationWindow window)
        {
            return;
        }

        var message = window.Message;

        if (DemoMode)
        {
            _log.Info("通知", "演示模式：点击不做窗口定位", message.Sender);
            return;
        }

        // 需求 3：ntfy 通知点击 → 唤起浏览器打开该 topic 的 Web UI 页面
        if (message.Source == MessageSource.Ntfy && !string.IsNullOrWhiteSpace(message.LinkUrl))
        {
            _log.AddEvent(message, EventStatus.Info, "打开 topic 页面");
            _log.Info("通知", "ntfy 点击：唤起浏览器", message.LinkUrl);
            BrowserLauncher.Open(_configProvider().BrowserPath, message.LinkUrl);
            return;
        }

        _log.AddEvent(message, EventStatus.Info, "点击定位");
        var log = _log;
        Task.Run(() => WindowActivator.TryOpenConversation(message, log));
    }
}
