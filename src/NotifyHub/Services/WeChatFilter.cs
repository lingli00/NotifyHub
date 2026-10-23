using NotifyHub.Models;
using NotifyHub.Services.Parsing;

namespace NotifyHub.Services;

/// <summary>
/// 微信消息过滤（在“接收阶段”直接拦截，不弹窗、不响铃、不进事件列表）。
///
/// v0.2.5 重写（需求：禁止用名称文字判断类型）：
/// 旧实现用一张关键词表对会话名/摘要做“包含匹配”，群名“阿里云”撞上关键词表里的
/// “阿里云”被误判成公众号直接跳过。现在判定只依据**真实信号**：
///   1) UIA 免打扰信号（会话项里的“消息免打扰”文字）——真实状态，不是猜的；
///   2) 微信内置系统/官方会话的**规范名**（服务号/服务通知/微信团队/公众号…），整名精确匹配，
///      绝不对用户自己的群名/好友名做包含匹配；
///   3) 官方推送通道特征（摘要带 mp.weixin.qq.com 文章链接）；
///   4) 消息结构：**私聊**的摘要是裸内容（如“谢谢你”），**群聊**带“成员: 内容”前缀；
///      自己发的消息同样是裸内容，但它**不进未读数**——靠未读证据区分：
///        裸内容 + 有未读 = 别人发来的（私聊）→ 放行
///        裸内容 + 无未读 = 自己发的       → 屏蔽
/// 用户自定义关键词仍是显式配置功能，但只在配置里追加、不参与类型判定。
/// </summary>
public static class WeChatFilter
{
    /// <summary>
    /// 微信内置系统/官方会话的规范名（整名精确匹配，大小写不敏感）。
    /// 这些是微信自己建出来的会话名，不是用户内容——匹配它们等于读会话真实身份。
    /// 注意不含“文件传输助手”：用户把它当自己的笔记本来回发消息，按自己发送处理。
    /// </summary>
    private static readonly HashSet<string> SystemSessionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "服务号", "服务通知", "微信团队", "微信支付", "订阅号消息", "公众号", "订阅号",
        "QQ邮箱提醒", "腾讯客服", "微信运动", "腾讯新闻", "微信读书", "理财通",
        "游戏中心", "搜一搜", "看一看", "微信广告", "广告通知", "小程序",
        "微信收款", "微信红包", "群发助手", "语音提醒", "充值中心",
    };

    /// <summary>UIA 里代表“免打扰”的信号词（图标 Name / HelpText / AutomationId 都算）。</summary>
    private static readonly string[] MuteSignals =
    {
        "免打扰", "消息免打扰", "静音", "muted", "mute", "donotdisturb", "do not disturb",
    };

    /// <summary>公众号文章链接特征（摘要里出现即拦截，来自官方推送通道）。</summary>
    private static readonly string[] OfficialLinkMarkers =
    {
        "mp.weixin.qq.com", "mp.weixinbridge.com",
    };

    /// <summary>
    /// 会话类型判定（只依据真实信号，供日志与过滤共用）。
    /// </summary>
    public enum WeChatSessionKind
    {
        /// 好友私聊 / 普通群聊（他人消息，放行）。</summary>
        Chat,

        /// 开启了免打扰的会话（群/人都拦）。</summary>
        Muted,

        /// 微信内置系统/官方会话（服务号/服务通知/微信团队…）。</summary>
        System,

        /// 用户自己发送的消息。</summary>
        FromSelf,
    }

    /// <summary>判定会话类型（真实信号，不做名称文字猜测）。</summary>
    public static WeChatSessionKind Classify(SessionRow row)
    {
        // 官方/系统会话的规范名优先于结构判定：微信团队/服务通知这类系统会话
        // 的消息也是裸内容，先按规范名归类，日志才准确。
        if (IsSystemSessionName(row.Contact))
        {
            return WeChatSessionKind.System;
        }

        // 自己发送 = 裸内容 + 无未读证据（自己发的消息不进未读数）。
        // 注意：私聊的摘要本来就没有“发言人: ”前缀，所以不能用“有没有发言人”判自己发的。
        if (row.FromSelf)
        {
            return WeChatSessionKind.FromSelf;
        }

        if (row.Muted || ContainsAny(row.Signals, MuteSignals))
        {
            return WeChatSessionKind.Muted;
        }

        if (ContainsAny(row.Summary, OfficialLinkMarkers))
        {
            return WeChatSessionKind.System;
        }

        return WeChatSessionKind.Chat;
    }

    /// <summary>类型名（日志用）。</summary>
    public static string KindName(WeChatSessionKind kind) => kind switch
    {
        WeChatSessionKind.Chat => "他人消息（私聊/群聊）",
        WeChatSessionKind.Muted => "免打扰会话",
        WeChatSessionKind.System => "官方/系统会话",
        WeChatSessionKind.FromSelf => "自己发送",
        _ => "未知",
    };

    /// <summary>判断是否拦截。reason 用于日志（说明拦截依据）。</summary>
    public static bool ShouldBlock(SessionRow row, AppConfig config, out string reason)
    {
        reason = string.Empty;

        if (!config.WeChatFilterEnabled)
        {
            return false;
        }

        var kind = Classify(row);
        switch (kind)
        {
            case WeChatSessionKind.Muted:
                reason = "免打扰会话";
                return true;
            case WeChatSessionKind.System:
                reason = "官方/系统会话";
                return true;
        }

        // 用户自定义拦截关键词（配置里逗号分隔）——显式配置功能，不参与上面的类型判定
        foreach (var keyword in SplitKeywords(config.WeChatBlockedKeywords))
        {
            if (row.Contact.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"命中自定义拦截词“{keyword}”";
                return true;
            }
        }

        return false;
    }

    /// <summary>会话名是否精确命中微信内置系统/官方会话规范名。</summary>
    private static bool IsSystemSessionName(string? contact)
    {
        if (string.IsNullOrWhiteSpace(contact))
        {
            return false;
        }

        return SystemSessionNames.Contains(contact.Trim());
    }

    private static IEnumerable<string> SplitKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        foreach (var piece in raw.Split(new[] { ',', '，', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var keyword = piece.Trim();
            if (keyword.Length > 0)
            {
                yield return keyword;
            }
        }
    }

    private static bool ContainsAny(string? text, string[] keywords)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
