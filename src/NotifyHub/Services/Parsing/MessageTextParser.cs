using System.Text.RegularExpressions;

namespace NotifyHub.Services.Parsing;

/// <summary>
/// 一条会话行：联系人/群名 + 最后一条消息摘要。
/// <paramref name="Muted"/> 来自 UIA 图标/说明里的“消息免打扰”信号；
/// <paramref name="Signals"/> 是该会话项里所有元素名称/说明的拼接，供过滤规则做关键词判定；
/// <paramref name="HasUnread"/> 表示摘要里带未读证据（微信 4.x 的“[N条]”前缀或“有人@我”），
/// 已读会话没有该前缀——需求 7 用它区分“用户已看过的老消息”与“真正的新消息”。
/// </summary>
public sealed record SessionRow(string Contact, string Summary, bool Muted = false, string Signals = "", bool HasUnread = false);

/// <summary>
/// 把 UIA 抓到的文本片段还原成 (联系人, 摘要)。
/// 纯函数、无 UIA 依赖，便于离线回归测试。
/// </summary>
public static partial class MessageTextParser
{
    /// <summary>
    /// 纯时间戳，例如：10:23 / 下午 3:05 / 昨天 / 昨天 21:30 / 星期三 / 3月5日 / 2024-05-01 / 刚刚。
    /// </summary>
    [GeneratedRegex(@"^((上午|下午|中午|晚上|凌晨|早上)\s*)?\d{1,2}:\d{2}$|^\d{1,2}/\d{1,2}$|^\d{1,2}月\d{1,2}日$|^\d{4}[-/年]\d{1,2}[-/月]\d{1,2}日?$|^(昨天|前天|今天|刚刚|星期[一二三四五六日天]|周[一二三四五六日天])(\s*\d{1,2}:\d{2})?$")]
    private static partial Regex TimeLikeRegex();

    /// <summary>未读数目，例如 3 / 99+。</summary>
    [GeneratedRegex(@"^\d{1,3}\+?$")]
    private static partial Regex BadgeLikeRegex();

    /// <summary>微信 4.x 会话项摘要里的置顶标记前缀。</summary>
    [GeneratedRegex(@"^(已置顶|置顶)\s*")]
    private static partial Regex PinnedPrefixRegex();

    /// <summary>微信 4.x 摘要开头的未读数前缀，例如 "[63条] 软件室: 内容"。</summary>
    [GeneratedRegex(@"^\[\d+条\]\s*")]
    private static partial Regex UnreadPrefixRegex();

    private static readonly string[] NoiseTexts =
    {
        "已读", "未读", "免打扰", "草稿", "消息免打扰", "[草稿]", "有人@我", "[有人@我]",
        "已置顶", "置顶",
    };

    /// <summary>
    /// 解析一条会话项的文本序列。
    /// 约定：第一个有效文本是联系人；其余去掉时间/未读数/系统噪声后拼成摘要。
    /// 摘要为空时返回 Summary="" （表示该会话暂时没有可比对内容）。
    /// </summary>
    public static SessionRow? Parse(IEnumerable<string> rawTexts)
    {
        var texts = Normalize(rawTexts);
        if (texts.Count == 0)
        {
            return null;
        }

        // 未读证据：微信 4.x 未读会话摘要带“[N条]”前缀，@我的会话带“有人@我”。
        // 实测（本机 dump）：已读会话摘要为“发言人: 内容”，没有任何前缀。
        var hasUnread = false;

        var contact = texts[0];
        if (contact.Length > 80 || TimeLikeRegex().IsMatch(contact) || BadgeLikeRegex().IsMatch(contact))
        {
            return null;
        }

        var parts = new List<string>();
        for (var i = 1; i < texts.Count; i++)
        {
            var text = texts[i];
            if (string.Equals(text, contact, StringComparison.Ordinal))
            {
                continue;
            }

            if (TimeLikeRegex().IsMatch(text))
            {
                continue;
            }

            // 仅当还存在其他文本时才把纯数字当未读数丢掉，避免把“1”这种真实消息误删
            if (BadgeLikeRegex().IsMatch(text) && texts.Count > 2)
            {
                continue;
            }

            if (UnreadPrefixRegex().IsMatch(text) || text.Contains("有人@我", StringComparison.Ordinal))
            {
                hasUnread = true;
            }

            var cleaned = CleanSummaryText(text);
            if (cleaned.Length == 0)
            {
                continue;
            }

            if (cleaned.Length > 400)
            {
                cleaned = cleaned[..400];
            }

            parts.Add(cleaned);
        }

        var summary = string.Join(" ", Distinct(parts)).Trim();
        return new SessionRow(contact, summary, HasUnread: hasUnread);
    }

    /// <summary>清洗：按行拆分、去零宽字符、去空行、去相邻重复。</summary>
    public static List<string> Normalize(IEnumerable<string> rawTexts)
    {
        var result = new List<string>();

        foreach (var raw in rawTexts)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (var piece in raw.Split('\n'))
            {
                var text = piece.Replace("\r", string.Empty).Replace("\u200b", string.Empty).Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                if (result.Count > 0 && string.Equals(result[^1], text, StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(text);
            }
        }

        return result;
    }

    /// <summary>剥掉置顶标记、未读数前缀等系统噪声；纯噪声返回空串。</summary>
    private static string CleanSummaryText(string text)
    {
        if (NoiseTexts.Contains(text))
        {
            return string.Empty;
        }

        text = PinnedPrefixRegex().Replace(text, string.Empty);
        text = UnreadPrefixRegex().Replace(text, string.Empty).Trim();

        return NoiseTexts.Contains(text) ? string.Empty : text;
    }

    private static IEnumerable<string> Distinct(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (seen.Add(item))
            {
                yield return item;
            }
        }
    }
}
