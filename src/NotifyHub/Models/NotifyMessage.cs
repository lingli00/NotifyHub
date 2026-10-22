namespace NotifyHub.Models;

/// <summary>消息来源。用于徽标颜色、去重键、点击定位策略。</summary>
public enum MessageSource
{
    WeChat,
    QQ,
    Ntfy,
}

/// <summary>一条待通知的消息（不可变，可跨线程传递）。</summary>
public sealed class NotifyMessage
{
    public MessageSource Source { get; init; }

    /// <summary>发件人 / 联系人 / 群名；ntfy 取 title。</summary>
    public string Sender { get; init; } = string.Empty;

    /// <summary>正文 / 最后一条消息摘要。</summary>
    public string Content { get; init; } = string.Empty;

    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>
    /// true 表示只能确认“有新消息”而读不到具体内容（QQ 无障碍树不可读时的降级路径）。
    /// 降级消息使用更短的去重窗口，避免把连续多条真实消息吞掉。
    /// </summary>
    public bool LowFidelity { get; init; }

    /// <summary>
    /// 点击通知要打开的地址（目前仅 ntfy：该 topic 的 Web UI 页面，需求 3）。
    /// 空表示没有关联地址（微信/QQ 走“定位到聊天”逻辑）。
    /// </summary>
    public string LinkUrl { get; init; } = string.Empty;

    public string SourceText => Source switch
    {
        MessageSource.WeChat => "微信",
        MessageSource.QQ => "QQ",
        MessageSource.Ntfy => "ntfy",
        _ => "未知",
    };

    /// <summary>去重键：(来源, 发件人, 内容)。</summary>
    public string DedupKey => $"{Source}\u0001{Sender}\u0001{Content}";

    /// <summary>
    /// 来源展示名覆盖（v0.2.4 多 ntfy 订阅）：如“ntfy · 家庭”。
    /// 空则用 <see cref="SourceText"/>。
    /// </summary>
    public string SourceLabel { get; init; } = string.Empty;

    /// <summary>
    /// 强调色索引（v0.2.4 需求 2）：ntfy 订阅通知按订阅取色板颜色（低饱和度微调）。
    /// -1 = 用来源默认色（微信绿/QQ蓝/ntfy橙）；≥0 时取 <see cref="AppConfig.NtfyPalette"/>。
    /// </summary>
    public int AccentIndex { get; init; } = -1;
}
