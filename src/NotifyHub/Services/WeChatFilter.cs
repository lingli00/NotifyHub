using NotifyHub.Models;
using NotifyHub.Services.Parsing;

namespace NotifyHub.Services;

/// <summary>
/// 消息内容类型（v0.2.17 工单A：把"这条消息是什么"从"这条消息来自哪"里拆出来）。
///
/// 旧逻辑只用**来源渠道**（官方会话 / 免打扰 / 好友群聊）做拦截判定，粒度粗：
/// 私聊里好友发来的图片因为"没有未读证据"被误判成自己发的而拦掉（工单A 的误伤现场）。
/// 现在内容类型是一等公民：图片 / 视频 / 动画表情 / 语音 / 文件在好友/群聊里**一律放行**，
/// 渠道标签（官方/免打扰）只作为风险权重参考，不再连带误伤正常多媒体。
/// </summary>
public enum MessageContentKind
{
    /// 纯文本（或无法识别的其他内容，按文本处理）。</summary>
    Text,

    /// 图片 [图片]。</summary>
    Image,

    /// 视频 [视频]。</summary>
    Video,

    /// 动画表情 / 表情 [动画表情] [表情] [表情包]。</summary>
    Sticker,

    /// 语音 [语音]。</summary>
    Voice,

    /// 文件 [文件]。</summary>
    File,

    /// 链接 / 公众号文章 [链接]。</summary>
    Link,

    /// 位置 [位置] [定位]。</summary>
    Location,

    /// 名片 [名片] [推荐名片]。</summary>
    Card,

    /// 红包 [红包]。</summary>
    RedPacket,

    /// 转账 [转账]。</summary>
    Transfer,

    /// 小程序 [小程序]。</summary>
    MiniProgram,

    /// 聊天记录 [聊天记录]。</summary>
    ChatHistory,

    /// 音乐 [音乐]。</summary>
    Music,

    /// 其他（合并转发的聊天/邮件/收藏等，按文本处理）。</summary>
    Other,
}

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
///
/// v0.2.17 工单A（多媒体风控过滤优化）——判定拆成两个维度：
///   · **来源渠道标签**（SourceTag）：官方/系统、免打扰、好友/群聊、自己发送。
///     它只是"风险权重"标签，记录进日志与误拦截反馈，**不再直接对内容做前置拦截**；
///   · **内容类型**（<see cref="MessageContentKind"/>）：文本 / 图片 / 视频 / 动画表情 / 语音 / 文件…
///     好友/群聊里的多媒体**一律放行**（<see cref="AppConfig.WeChatAllowChatMultimedia"/>，
///     默认开），修掉"私聊图片没有未读证据 → 被当成自己发的 → 拦掉"的误伤；
///     官方/免打扰会话按渠道标签拦截（那是用户显式要的静音/过滤，不是误伤）。
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
    /// 微信摘要里的多媒体占位符（v0.2.17 工单A：内容类型识别的依据）。
    /// 微信 4.x 会话列表对非文本消息显示这些方括号占位符，实测样本：
    ///   亿新线报专员一不加好友: [图片] / 小矮子: [视频] / [动画表情] / [语音] / [文件]
    /// 注意：**只认方括号占位符**，不认"图片"两个字——好友说"图片发你了"是文本不是图片。
    /// </summary>
    private static readonly (string Marker, MessageContentKind Kind)[] ContentMarkers =
    {
        ("[动画表情]", MessageContentKind.Sticker),
        ("[表情]", MessageContentKind.Sticker),
        ("[表情包]", MessageContentKind.Sticker),
        ("[图片]", MessageContentKind.Image),
        ("[视频]", MessageContentKind.Video),
        ("[语音]", MessageContentKind.Voice),
        ("[文件]", MessageContentKind.File),
        ("[链接]", MessageContentKind.Link),
        ("[位置]", MessageContentKind.Location),
        ("[定位]", MessageContentKind.Location),
        ("[名片]", MessageContentKind.Card),
        ("[推荐名片]", MessageContentKind.Card),
        ("[红包]", MessageContentKind.RedPacket),
        ("[转账]", MessageContentKind.Transfer),
        ("[小程序]", MessageContentKind.MiniProgram),
        ("[聊天记录]", MessageContentKind.ChatHistory),
        ("[音乐]", MessageContentKind.Music),
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

    /// <summary>
    /// 内容类型（v0.2.17 工单A）：从摘要里的方括号占位符识别。
    /// 一条消息只取第一个命中的多媒体类型；纯文本或没有占位符 → <see cref="MessageContentKind.Text"/>。
    /// </summary>
    public static MessageContentKind ClassifyContent(SessionRow row)
        => ClassifyContent(row.Summary);

    /// <summary>内容类型（摘要文本版，纯函数便于自检）。</summary>
    public static MessageContentKind ClassifyContent(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return MessageContentKind.Text;
        }

        foreach (var (marker, kind) in ContentMarkers)
        {
            if (summary.Contains(marker, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return MessageContentKind.Text;
    }

    /// <summary>是否多媒体内容（图片/视频/表情/语音/文件——好友群聊里一律放行的那批）。</summary>
    public static bool IsMultimedia(MessageContentKind kind)
        => kind is MessageContentKind.Image
            or MessageContentKind.Video
            or MessageContentKind.Sticker
            or MessageContentKind.Voice
            or MessageContentKind.File;

    /// <summary>内容类型名（日志/反馈用）。</summary>
    public static string ContentName(MessageContentKind kind) => kind switch
    {
        MessageContentKind.Text => "文本",
        MessageContentKind.Image => "图片",
        MessageContentKind.Video => "视频",
        MessageContentKind.Sticker => "表情包",
        MessageContentKind.Voice => "语音",
        MessageContentKind.File => "文件",
        MessageContentKind.Link => "链接",
        MessageContentKind.Location => "位置",
        MessageContentKind.Card => "名片",
        MessageContentKind.RedPacket => "红包",
        MessageContentKind.Transfer => "转账",
        MessageContentKind.MiniProgram => "小程序",
        MessageContentKind.ChatHistory => "聊天记录",
        MessageContentKind.Music => "音乐",
        _ => "其他",
    };

    /// <summary>
    /// 来源渠道标签（v0.2.17 工单A：渠道只作风险权重参考，不直接拦截内容）。
    /// 日志与误拦截反馈里都会带它，方便复盘"是哪条渠道规则拦的"。
    /// </summary>
    public static string SourceTag(WeChatSessionKind kind) => kind switch
    {
        WeChatSessionKind.Chat => "好友/群聊",
        WeChatSessionKind.Muted => "免打扰会话",
        WeChatSessionKind.System => "公众号/官方渠道",
        WeChatSessionKind.FromSelf => "自己发送",
        _ => "未知",
    };

    /// <summary>类型名（日志用）。</summary>
    public static string KindName(WeChatSessionKind kind) => kind switch
    {
        WeChatSessionKind.Chat => "他人消息（私聊/群聊）",
        WeChatSessionKind.Muted => "免打扰会话",
        WeChatSessionKind.System => "官方/系统会话",
        WeChatSessionKind.FromSelf => "自己发送",
        _ => "未知",
    };

    /// <summary>
    /// 过滤判定结果（v0.2.17 工单A：一次判定同时给出"拦不拦 / 为什么 / 渠道标签 / 内容类型"）。
    /// </summary>
    public readonly record struct FilterDecision(
        bool Blocked,
        string Reason,
        string Source,
        MessageContentKind Content)
    {
        /// 放行时的便捷构造。</summary>
        public static FilterDecision Pass(WeChatSessionKind kind, MessageContentKind content, string reason = "")
            => new(false, reason, SourceTag(kind), content);

        /// 拦截时的便捷构造。</summary>
        public static FilterDecision Block(WeChatSessionKind kind, MessageContentKind content, string reason)
            => new(true, reason, SourceTag(kind), content);
    }

    /// <summary>
    /// 统一判定入口（v0.2.17 工单A 重写）。
    ///
    /// 顺序（每一条的理由都写进 reason，日志可直接引用）：
    ///   1. 过滤总开关关闭 → 放行；
    ///   2. 免打扰会话 → 拦（用户显式设置的静音，不是误伤）；
    ///   3. 官方/系统会话 → 拦，reason 注明这是**来源渠道标签**判定、并带上内容类型；
    ///   4. 好友/群聊的多媒体（图片/视频/表情/语音/文件）→ **放行**（反误伤核心，
    ///      覆盖"私聊多媒体没有未读证据被当成自己发的"这条旧误伤路径）；
    ///   5. 自己发送的文本 → 拦（自己发的消息不进未读数，是噪声不是误伤）；
    ///   6. 用户自定义拦截关键词 → 拦。
    /// </summary>
    public static FilterDecision Decide(SessionRow row, AppConfig config)
    {
        var kind = Classify(row);
        var content = ClassifyContent(row);

        if (!config.WeChatFilterEnabled)
        {
            return FilterDecision.Pass(kind, content, "过滤开关已关闭");
        }

        switch (kind)
        {
            case WeChatSessionKind.Muted:
                return FilterDecision.Block(kind, content, "免打扰会话（用户显式静音）");

            case WeChatSessionKind.System:
                return FilterDecision.Block(
                    kind, content, $"官方/系统会话（来源渠道标签，内容={ContentName(content)}）");
        }

        // 好友/群聊多媒体：一律放行（工单A 反误伤）。
        // 旧逻辑在这里漏了一个大口子：私聊的图片/表情摘要是裸内容（无"发言人:"前缀），
        // 一旦未读证据没被 UIA 捕获就会被 Classify 判成 FromSelf 直接拦掉。
        if (kind == WeChatSessionKind.Chat && IsMultimedia(content) && config.WeChatAllowChatMultimedia)
        {
            return FilterDecision.Pass(kind, content, $"好友/群聊{ContentName(content)}放行");
        }

        // 被判成"自己发送"但其实是多媒体：同样放行（私聊好友发来的图片没有未读证据时走这条）。
        if (kind == WeChatSessionKind.FromSelf && IsMultimedia(content) && config.WeChatAllowChatMultimedia)
        {
            return FilterDecision.Pass(kind, content, $"{ContentName(content)}无未读证据，按好友多媒体放行");
        }

        if (kind == WeChatSessionKind.FromSelf)
        {
            return FilterDecision.Block(kind, content, "自己发送");
        }

        // 用户自定义拦截关键词（配置里逗号分隔）——显式配置功能，不参与上面的类型判定
        foreach (var keyword in SplitKeywords(config.WeChatBlockedKeywords))
        {
            if (row.Contact.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return FilterDecision.Block(kind, content, $"命中自定义拦截词“{keyword}”");
            }
        }

        return FilterDecision.Pass(kind, content);
    }

    /// <summary>判断是否拦截。reason 用于日志（说明拦截依据）。</summary>
    public static bool ShouldBlock(SessionRow row, AppConfig config, out string reason)
    {
        var decision = Decide(row, config);
        reason = decision.Blocked ? decision.Reason : string.Empty;
        return decision.Blocked;
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
