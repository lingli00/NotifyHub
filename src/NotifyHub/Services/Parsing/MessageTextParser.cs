using System.Text.RegularExpressions;

namespace NotifyHub.Services.Parsing;

/// <summary>
/// 一条会话行：联系人/群名 + 最后一条消息摘要。
/// <paramref name="Muted"/> 来自 UIA 图标/说明里的“消息免打扰”信号；
/// <paramref name="Signals"/> 是该会话项里所有元素名称/说明的拼接，供过滤规则做关键词判定；
/// <paramref name="HasUnread"/> 表示摘要里带未读证据（微信 4.x 的“[N条]”前缀或“有人@我”），
/// 已读会话没有该前缀——需求 7 用它区分“用户已看过的老消息”与“真正的新消息”。
/// <paramref name="UnreadCount"/> 是 [N条] 里的 N（无前缀为 0）——别人再发一条即使文本与
/// 上一条完全相同，未读数也会 +1，差分据此识别“同文本新消息”（修复同文本被吞）。
/// <paramref name="FromSelf"/> 表示最后一条消息是**用户自己发的**：微信 4.x 实测，
/// 别人发来的消息摘要带“发言人: ”前缀（未读另带“[N条]”），自己发的只有裸内容且不带未读证据
/// （自己发的消息不会进未读数）。官方账号推送也是裸内容，但带未读数——由过滤层按
/// “无发言人结构的未读推送”拦截，不算自己发送。
/// 需求 4：自己发的消息一律不推送。
/// </summary>
public sealed record SessionRow(string Contact, string Summary, bool Muted = false, string Signals = "", bool HasUnread = false, int UnreadCount = 0, bool FromSelf = false);

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

    /// <summary>微信 4.x 摘要开头的未读数前缀，例如 "[63条] 软件室: 内容" / "[99+条] …"。</summary>
    [GeneratedRegex(@"^\[(\d+)\+?条\]\s*")]
    private static partial Regex UnreadPrefixRegex();

    /// <summary>@我的前缀，例如 "[有人@我] 张三: 内容"。</summary>
    [GeneratedRegex(@"^\[?有人@我\]?\s*")]
    private static partial Regex AtMePrefixRegex();

    /// <summary>
    /// “发言人: 内容”结构：别人发来的消息摘要带头像/气泡里的发言人名。
    /// 微信 4.x 实测（uia-sessions 探针）：
    ///   [14条] 亿新线报专员一不加好友: [图片]    ← 别人发来的未读
    ///   From CloudCone: $5.10 on you…            ← 别人发来的已读
    ///   的撒旦不会寂寞最新版·                     ← 自己发的（无发言人结构）
    /// 发言人名一般 ≤ 24 字符；含 URL 特征的冒号前导（http: //www. .com）不算发言人。
    /// </summary>
    [GeneratedRegex(@"^(?<who>[^:\n]{1,24}):[ \u3000]")]
    private static partial Regex SpeakerPrefixRegex();

    /// <summary>冒号前导带 URL 特征时不是发言人名（如 "https://xxx"、"www.a.com"）。</summary>
    [GeneratedRegex(@"https?[:：]|www\.|\.(com|cn|net|org|io)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLikeRegex();

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
        var unreadCount = 0;

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

            // 纯数字既可能是“未读红点数字”，也可能是消息内容本身（“1”“666”）。
            // 区分依据（v0.2.4 修正，实测 ListItem.Name 序列 [联系人, …, 摘要, 时间, 信号]）：
            // 未读 badge 后面紧跟的是摘要行；消息内容后面紧跟的是时间行。
            // 因此：纯数字不是最后一项、且下一项不是时间样文本时，才按未读数丢弃。
            // （旧规则无条件丢纯数字，导致好友发来的“1”被吞掉——消息识别不到。）
            var nextIsTime = i + 1 < texts.Count && TimeLikeRegex().IsMatch(texts[i + 1]);
            if (BadgeLikeRegex().IsMatch(text) && texts.Count > 2 && i < texts.Count - 1 && !nextIsTime)
            {
                continue;
            }

            var unreadMatch = UnreadPrefixRegex().Match(text);
            if (unreadMatch.Success)
            {
                hasUnread = true;
                var digits = new string(unreadMatch.Value.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var n))
                {
                    unreadCount = Math.Max(unreadCount, n);
                }
            }

            if (text.Contains("有人@我", StringComparison.Ordinal))
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
        // 自己发送 = 裸内容且无未读证据（自己发的消息不进未读数）；
        // 带未读证据的裸内容是官方账号推送，交给过滤层拦，不算自己发送。
        var fromSelf = summary.Length > 0 && !HasSpeakerPrefix(summary) && !hasUnread;
        return new SessionRow(contact, summary, HasUnread: hasUnread, UnreadCount: unreadCount, FromSelf: fromSelf);
    }

    /// <summary>
    /// 摘要是否带“发言人: ”前缀（别人发来的消息特征）。
    /// 先剥掉 [N条] / 有人@我 前缀再判定；URL 特征（http:、www.、.com）不算发言人。
    /// </summary>
    private static bool HasSpeakerPrefix(string summary)
    {
        var text = UnreadPrefixRegex().Replace(summary, string.Empty);
        text = AtMePrefixRegex().Replace(text, string.Empty).Trim();

        var match = SpeakerPrefixRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var who = match.Groups["who"].Value.Trim();
        return who.Length > 0 && !UrlLikeRegex().IsMatch(who);
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
