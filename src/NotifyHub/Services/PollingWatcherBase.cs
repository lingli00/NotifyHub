using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 轮询式监听器基类：独占一条后台线程跑循环，UI 线程不参与。
/// 单次轮询异常只记日志，线程继续跑；停止时用事件唤醒，不会卡在 Sleep 上。
/// 该类不引用任何 WPF 类型，避免后台线程碰 UI。
/// </summary>
public abstract class PollingWatcherBase
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private string _lastThrottledMessage = string.Empty;
    private DateTime _lastThrottledAt = DateTime.MinValue;

    protected PollingWatcherBase(string name, LogService log, ConfigService config)
    {
        Name = name;
        _log = log;
        _config = config;
    }

    public string Name { get; }

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public bool IsRunning => _running;

    /// <summary>检测到新消息（后台线程回调，订阅方必须自行切 UI 线程）。</summary>
    public event EventHandler<NotifyMessage>? MessageDetected;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = $"NotifyHub.{Name}",
        };
        _thread.Start();
        _log.Info(Name, "监听线程已启动");
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _wake.Set();

        try
        {
            _thread?.Join(2000);
        }
        catch
        {
            // 忽略
        }

        _thread = null;
        _log.Info(Name, "监听线程已停止");
    }

    protected abstract bool IsEnabled(AppConfig config);

    protected abstract void PollOnce(AppConfig config);

    protected LogService Log => _log;

    protected AppConfig CurrentConfig => _config.Current;

    protected void Emit(NotifyMessage message) => MessageDetected?.Invoke(this, message);

    /// <summary>同一条提示按最小间隔节流，避免 800ms 一次把日志刷爆。</summary>
    protected void LogThrottled(LogLevel level, string message, TimeSpan minInterval)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(message, _lastThrottledMessage, StringComparison.Ordinal) && now - _lastThrottledAt < minInterval)
        {
            return;
        }

        _lastThrottledMessage = message;
        _lastThrottledAt = now;
        _log.Write(level, Name, message);
    }

    private void Loop()
    {
        _log.Info(Name, "轮询循环开始");

        while (_running)
        {
            var interval = 800;
            var polled = false;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var config = _config.Current;
                interval = Math.Clamp(config.PollIntervalMs, AppConfig.MinPollIntervalMs, AppConfig.MaxPollIntervalMs);

                if (!_paused && IsEnabled(config))
                {
                    PollOnce(config);
                    polled = true;

                    // 目标程序无响应时 UIA 调用可能卡 30 秒，记下来便于用户排查
                    if (stopwatch.ElapsedMilliseconds > 2000)
                    {
                        _log.Warn(Name, $"本轮轮询耗时 {stopwatch.ElapsedMilliseconds}ms（目标窗口响应慢）");
                    }
                }
            }
            catch (Exception ex)
            {
                // 单个模块失败不影响其他模块
                _log.Error(Name, "轮询异常", $"{ex.GetType().Name}: {ex.Message}");
            }

            // 本轮工作耗时要从等待时间里扣掉，否则实际周期 = interval + 轮询耗时，
            // 目标窗口响应慢时有效间隔会被拉长好几倍，放大漏检窗口
            var wait = polled
                ? (int)Math.Max(50, interval - stopwatch.ElapsedMilliseconds)
                : interval;

            _wake.Wait(wait);
            _wake.Reset();
        }

        _log.Info(Name, "轮询循环退出");
    }
}
