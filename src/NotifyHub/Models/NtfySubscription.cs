namespace NotifyHub.Models;

/// <summary>
/// 一条 ntfy 订阅（v0.2.4，需求 1）：独立开关、展示名、通知颜色。
/// 不同订阅的通知弹窗用 <see cref="ColorIndex"/> 对应的低饱和度颜色区分（需求 2）。
/// </summary>
public sealed class NtfySubscription
{
    /// <summary>是否启用该订阅。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>展示名（通知卡顶部显示“ntfy · 名字”）；留空则只显示“ntfy”。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>订阅地址，如 https://ntfy.sh/mytopic（程序自动补 /sse）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>通知颜色，索引到 <see cref="AppConfig.NtfyPalette"/>（0=默认橙）。</summary>
    public int ColorIndex { get; set; }

    /// <summary>克隆（设置页编辑列表用）。</summary>
    public NtfySubscription Clone() => new()
    {
        Enabled = Enabled,
        Name = Name,
        Url = Url,
        ColorIndex = ColorIndex,
    };

    /// <summary>列表变化签名（MonitorController 判断是否需要重建订阅连接）。</summary>
    public string Fingerprint() => $"{(Enabled ? 1 : 0)}|{Name}|{Url}|{ColorIndex}";
}
