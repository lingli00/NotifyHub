namespace NotifyHub.Models;

public enum EventStatus
{
    Notified,
    Duplicate,
    Error,
    Info,
}

/// <summary>事件日志条目，配置窗口里展示最近 50 条。</summary>
public sealed class EventLogEntry
{
    public DateTime Time { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Sender { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public EventStatus Status { get; set; }

    /// <summary>附加信息，例如去重哈希、定位结果。</summary>
    public string Note { get; set; } = string.Empty;

    public string TimeText => Time.ToString("HH:mm:ss");

    public string StatusText => Status switch
    {
        EventStatus.Notified => "已通知",
        EventStatus.Duplicate => "去重",
        EventStatus.Error => "异常",
        EventStatus.Info => "信息",
        _ => Status.ToString(),
    };
}
