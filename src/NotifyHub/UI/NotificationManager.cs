using System.Windows;
using NotifyHub.Models;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 通知堆叠管理：按配置的四角位置纵向排列，新通知在最上方；
/// 消息进入队列逐条弹出（间隔 320ms，不瞬间批量轰炸），
/// 超出堆叠上限时等待前面的卡片关闭后再放行。
/// 所有方法必须在 UI 线程调用。
/// </summary>
public sealed class NotificationManager
{
    public const double CardWidth = 380;
    public const double CardHeight = 110;

    private const double Gap = 8;
    private const double EdgeMargin = 12;
    private static readonly TimeSpan PopInterval = TimeSpan.FromMilliseconds(320);

    private readonly List<NotificationWindow> _windows = new();
    private readonly Queue<NotifyMessage> _pending = new();
    private readonly System.Windows.Threading.DispatcherTimer _pump = new() { Interval = PopInterval };
    private readonly LogService _log;
    private readonly Func<AppConfig> _configProvider;
    private readonly SoundService? _sound;

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
    }

    public void Relayout(bool animate = true)
    {
        for (var i = 0; i < _windows.Count; i++)
        {
            var (left, top) = ComputePosition(i);
            _windows[i].MoveTo(left, top, animate);
        }
    }

    /// <summary>按当前四角配置计算第 index 张卡片的位置。</summary>
    private (double Left, double Top) ComputePosition(int index)
    {
        var area = SystemParameters.WorkArea;
        var corner = AppConfig.NormalizeCorner(_configProvider().NotificationCorner);
        var leftAligned = corner is "TopLeft" or "BottomLeft";
        var bottomUp = corner is "BottomLeft" or "BottomRight";

        var left = leftAligned
            ? area.Left + EdgeMargin
            : area.Right - CardWidth - EdgeMargin;

        var top = bottomUp
            ? area.Bottom - EdgeMargin - CardHeight - (index * (CardHeight + Gap))
            : area.Top + EdgeMargin + (index * (CardHeight + Gap));

        return (left, top);
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

        if (_windows.Count >= max)
        {
            // 位置已满，等卡片关闭时由 OnWindowDismissed 继续泵
            return;
        }

        var message = _pending.Dequeue();
        var window = new NotificationWindow(message, config.NotificationDurationMs);
        window.Dismissed += OnWindowDismissed;
        window.Clicked += OnWindowClicked;

        // 新通知在最上方
        _windows.Insert(0, window);
        Relayout(animate: true);

        // 直接用计算坐标（Window.Left 未设置时是 NaN，不能拿来当参数）
        var (left, top) = ComputePosition(0);
        window.ShowAt(left, top, animate: true);

        _log.Info("通知", $"弹出第 {_windows.Count} 张卡", $"corner={config.NotificationCorner} pos={left:F0},{top:F0}");

        // 消息通知提示音：真正上屏时才响，被过滤的消息不会走到这里
        _sound?.PlayMessage(config);
    }

    private void OnWindowDismissed(object? sender, EventArgs e)
    {
        if (sender is not NotificationWindow window)
        {
            return;
        }

        window.Dismissed -= OnWindowDismissed;
        window.Clicked -= OnWindowClicked;
        _windows.Remove(window);
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

        _log.AddEvent(message, EventStatus.Info, "点击定位");
        var log = _log;
        Task.Run(() => WindowActivator.TryOpenConversation(message, log));
    }
}
