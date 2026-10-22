using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 监听总控：托管微信 / QQ / ntfy 三个来源，统一做去重与事件记录，
/// 再把“要弹的通知”抛给 UI 层。任何一个来源挂掉都不影响其他来源。
/// </summary>
public sealed class MonitorController : IDisposable
{
    /// <summary>
    /// 结构化消息的去重窗口。只用于拦截“同源瞬间重复”（如双通道同时到达），
    /// 之前设 10 分钟过长：同一个人 10 分钟内重发同样内容会被当重复静默吞掉，造成丢包。
    /// 现缩到 2 分钟，跨来源重放场景由消息 Id 级去重兜底。
    /// </summary>
    private static readonly TimeSpan StructuredDedupWindow = TimeSpan.FromMinutes(2);

    /// <summary>降级消息（读不到内容）只做短暂去重，避免吞掉连续多条真实消息。</summary>
    private static readonly TimeSpan LowFidelityDedupWindow = TimeSpan.FromSeconds(4);

    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly DedupCache _dedup;
    private readonly MessageCursorStore _cursor;
    private readonly WeChatWatcher _wechat;
    private readonly QQWatcher _qq;
    private readonly NtfySubscriber _ntfy;

    private string _ntfyFingerprint = string.Empty;
    private bool _started;

    public MonitorController(LogService log, ConfigService config)
    {
        _log = log;
        _config = config;

        // 去重记录落盘：重启后也不会把已推送过的消息再弹一遍
        _dedup = new DedupCache(AppPaths.DedupFile)
        {
            Retention = TimeSpan.FromHours(config.Current.DedupHours),
        };

        // 消息游标落盘：重启后只弹游标之后的新消息，不回溯历史（需求 7）
        _cursor = new MessageCursorStore(AppPaths.CursorFile);

        _wechat = new WeChatWatcher(log, config, _cursor);
        _qq = new QQWatcher(log, config, _cursor);
        _ntfy = new NtfySubscriber(log, config);

        _wechat.MessageDetected += OnMessage;
        _qq.MessageDetected += OnMessage;
        _ntfy.MessageReceived += OnMessage;
    }

    /// <summary>需要弹通知（后台线程回调，订阅方负责切 UI 线程）。</summary>
    public event Action<NotifyMessage>? NotificationReady;

    public bool IsPaused { get; private set; }

    public string NtfyStatus => _ntfy.Status;

    public string WeChatStatus => _wechat.IsRunning ? "运行中" : "已停止";

    public string QQStatus => _qq.IsRunning ? "运行中" : "已停止";

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _wechat.Start();
        _qq.Start();
        _ntfyFingerprint = BuildNtfyFingerprint(_config.Current);
        _ntfy.Start();
        _log.Info("Monitor", "监听已启动");
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        _wechat.Paused = paused;
        _qq.Paused = paused;
        _ntfy.Paused = paused;
        _log.Info("Monitor", paused ? "已暂停全部监听" : "已恢复监听");
    }

    /// <summary>配置变更后调用：ntfy 地址/开关变化时重连，其余参数下一轮自动生效。</summary>
    public void ApplyConfig()
    {
        var config = _config.Current;
        _dedup.Retention = TimeSpan.FromHours(config.DedupHours);

        var fingerprint = BuildNtfyFingerprint(config);

        if (fingerprint == _ntfyFingerprint)
        {
            return;
        }

        _ntfyFingerprint = fingerprint;
        _log.Info("Monitor", "ntfy 配置变化，重建订阅");
        _ntfy.Stop();
        _ntfy.Start();
    }

    /// <summary>把去重/游标缓存落盘（由 UI 定时器周期调用）。</summary>
    public void FlushCaches()
    {
        _dedup.Flush();
        _cursor.Flush();
    }

    public void Dispose()
    {
        _dedup.Flush();
        _cursor.Flush();
        _wechat.Stop();
        _qq.Stop();
        _ntfy.Dispose();
        _wechat.MessageDetected -= OnMessage;
        _qq.MessageDetected -= OnMessage;
        _ntfy.MessageReceived -= OnMessage;
        _started = false;
    }

    private static string BuildNtfyFingerprint(AppConfig config) => $"{config.NtfyEnabled}|{config.NtfyUrl}";

    private void OnMessage(object? sender, NotifyMessage message)
    {
        if (IsPaused)
        {
            return;
        }

        var window = message.LowFidelity ? LowFidelityDedupWindow : StructuredDedupWindow;

        if (!_dedup.TryAccept(message, window, out var hash))
        {
            _log.AddEvent(message, EventStatus.Duplicate, hash);
            return;
        }

        try
        {
            _log.AddEvent(message, EventStatus.Notified, hash);
            NotificationReady?.Invoke(message);
        }
        catch (Exception ex)
        {
            _log.Error("Monitor", "派发通知失败", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
