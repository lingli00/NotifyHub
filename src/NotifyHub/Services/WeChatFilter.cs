using NotifyHub.Models;
using NotifyHub.Services.Parsing;

namespace NotifyHub.Services;

/// <summary>
/// 微信消息过滤（在“接收阶段”直接拦截，不弹窗、不响铃、不进事件列表）：
///   放行：好友私聊、未开启免打扰的群聊；
///   拦截：公众号/订阅号推送、服务通知、广告营销，以及所有开启了免打扰的会话
///        （无论新消息还是回看历史消息）。
/// 判定依据来自 UIA 信号（会话项内图标/说明里的“消息免打扰”等文字）+ 可配置关键词。
/// </summary>
public static class WeChatFilter
{
    /// <summary>UIA 里代表“免打扰”的信号词（图标 Name / HelpText / AutomationId 都算）。</summary>
    private static readonly string[] MuteSignals =
    {
        "免打扰", "消息免打扰", "静音", "muted", "mute", "donotdisturb", "do not disturb",
    };

    /// <summary>内置的公众号/系统通知类会话关键词（命中即拦截，可在配置里追加）。</summary>
    private static readonly string[] OfficialKeywords =
    {
        "服务通知", "微信团队", "微信支付", "订阅号消息", "公众号", "订阅号",
        "服务号", "官方账号", "官方通知",
        "看一看", "搜一搜", "游戏中心", "微信运动", "腾讯新闻", "QQ邮箱提醒",
        "微信广告", "广告通知", "小程序",
        "微信读书", "腾讯客服", "理财通", "阿里云", "腾讯充值",
    };

    /// <summary>公众号文章链接特征（摘要里出现即拦截，来自官方推送通道）。</summary>
    private static readonly string[] OfficialLinkMarkers =
    {
        "mp.weixin.qq.com", "mp.weixinbridge.com",
    };

    /// <summary>判断是否拦截。reason 用于日志（说明拦截依据）。</summary>
    public static bool ShouldBlock(SessionRow row, AppConfig config, out string reason)
    {
        reason = string.Empty;

        if (!config.WeChatFilterEnabled)
        {
            return false;
        }

        // 1) 免打扰会话（群/人都拦，含历史消息回看场景）
        if (row.Muted || ContainsAny(row.Signals, MuteSignals))
        {
            reason = "免打扰会话";
            return true;
        }

        // 2) 公众号 / 服务号 / 服务通知 / 广告营销（名称或摘要命中）
        if (ContainsAny(row.Contact, OfficialKeywords))
        {
            reason = "公众号/服务通知";
            return true;
        }

        if (ContainsAny(row.Summary, OfficialKeywords))
        {
            reason = "含公众号/服务通知标识";
            return true;
        }

        // 2b) 官方推送通道特征：摘要带公众号文章链接（mp.weixin.qq.com …）
        if (ContainsAny(row.Summary, OfficialLinkMarkers))
        {
            reason = "公众号文章链接";
            return true;
        }

        // 2c) 会话项 UIA 信号里带“服务号/订阅号”标识（图标 Name / HelpText）
        if (ContainsAny(row.Signals, new[] { "服务号", "订阅号", "公众号", "official", "oa_" }))
        {
            reason = "UIA 信号：服务号/公众号";
            return true;
        }

        // 3) 用户自定义拦截关键词（配置里逗号分隔）
        foreach (var keyword in SplitKeywords(config.WeChatBlockedKeywords))
        {
            if (row.Contact.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"命中拦截关键词“{keyword}”";
                return true;
            }
        }

        return false;
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
