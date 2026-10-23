namespace NotifyHub.Models;

using System.Text.Json.Serialization;

/// <summary>
/// 应用配置。字段允许缺省，读取后统一通过 <see cref="Normalize"/> 收敛到安全区间，
/// 避免用户填 0 或负数把轮询线程打爆。
/// </summary>
public sealed class AppConfig
{
    public const int MinPollIntervalMs = 300;
    public const int MaxPollIntervalMs = 2000;
    public const int MinDurationMs = 1000;
    public const int MaxDurationMs = 30000;
    public const int MaxStackLimit = 5;

    /// <summary>ntfy 订阅地址，支持 https://ntfy.sh/topic 或完整 .../sse。</summary>
    public string NtfyUrl { get; set; } = "https://ntfy.sh/notifyhub-test";

    /// <summary>
    /// ntfy 多订阅列表（v0.2.4，需求 1）：每个地址独立开关、独立展示名、独立通知颜色。
    /// 加载旧配置时由 <see cref="ConfigService"/> 把单地址 <see cref="NtfyUrl"/> 迁移为第一项。
    /// </summary>
    public List<NtfySubscription> NtfySubscriptions { get; set; } = new();

    /// <summary>
    /// ntfy 下行推送地址（v0.2.4，需求 3）：把灵动岛里 WorkBuddy 正在执行的中文过程信息
    /// 原样推送到手机端（手机 ntfy App 订阅同一 topic 即可收到）。空 = 关闭。
    /// </summary>
    public string NtfyPushUrl { get; set; } = string.Empty;

    public bool NtfyEnabled { get; set; }

    public bool WeChatEnabled { get; set; } = true;

    public bool QQEnabled { get; set; } = true;

    /// <summary>UIA 轮询间隔（毫秒），默认 300，上限 2000。</summary>
    public int PollIntervalMs { get; set; } = 300;

    /// <summary>通知停留时长（毫秒）。</summary>
    public int NotificationDurationMs { get; set; } = 4000;

    /// <summary>通知弹窗位置：TopRight / TopLeft / BottomRight / BottomLeft。</summary>
    public string NotificationCorner { get; set; } = "TopRight";

    /// <summary>开机自启（HKCU Run）。便携版默认关闭。</summary>
    public bool AutoStart { get; set; }

    /// <summary>启动时把微信 / QQ 的 UIA 树 dump 到日志目录（探测模式）。</summary>
    public bool DumpUiaOnStart { get; set; }

    /// <summary>同时显示的堆叠上限，硬上限 5。</summary>
    public int MaxStack { get; set; } = 3;

    /// <summary>灵动岛位置模式：Fixed=锁定屏幕顶部居中（不可拖动）；Free=自由拖动。</summary>
    public string IslandMode { get; set; } = "Fixed";

    /// <summary>自由拖动模式保存的位置（DIP 坐标）。null 表示尚未保存过（JSON 可空序列化）。</summary>
    public double? IslandLeft { get; set; }

    /// <summary>自由拖动模式保存的位置（DIP 坐标）。null 表示尚未保存过。</summary>
    public double? IslandTop { get; set; }

    /// <summary>启用音乐控制模块：播放音乐时灵动岛显示播放器控件，无音频自动隐藏。</summary>
    public bool MusicEnabled { get; set; } = true;

    /// <summary>自动读取 WorkBuddy 任务进度：监视本地会话记录，灵动岛实时显示 AI 正在执行的动作，回合结束报完成态。</summary>
    public bool WorkBuddyWatchEnabled { get; set; } = true;

    /// <summary>【消息通知提示音】开关（仅放行的聊天消息播放）。</summary>
    public bool MessageSoundEnabled { get; set; } = true;

    /// <summary>【消息通知提示音】音量 0-100。</summary>
    public int MessageSoundVolume { get; set; } = 80;

    /// <summary>【消息通知提示音】音色：DingDong/Crystal/Bubble/Marimba/Digital。</summary>
    public string MessageSoundStyle { get; set; } = "DingDong";

    /// <summary>【WorkBuddy 任务完成提示音】开关（与消息提示音互相独立）。</summary>
    public bool TaskSoundEnabled { get; set; } = true;

    /// <summary>【WorkBuddy 任务完成提示音】音量 0-100。</summary>
    public int TaskSoundVolume { get; set; } = 80;

    /// <summary>【任务完成提示音】音色：Ascend/Success/Bell/Chord。</summary>
    public string TaskSoundStyle { get; set; } = "Ascend";

    /// <summary>
    /// 自动静音微信/QQ 自带提示音：消息提示音开启时把微信/QQ 的音频会话静音，
    /// 只保留本应用音效；关闭本项或消息提示音时恢复。
    /// </summary>
    public bool SuppressAppSounds { get; set; } = true;

    /// <summary>微信通知过滤：只推好友私聊与未免打扰群，拦截公众号/服务通知/免打扰群。</summary>
    public bool WeChatFilterEnabled { get; set; } = true;

    /// <summary>额外拦截关键词（逗号分隔，命中会话名或摘要即拦截）。</summary>
    public string WeChatBlockedKeywords { get; set; } =
        "服务通知,微信团队,微信支付,订阅号消息,公众号,看一看,搜一搜,游戏中心,微信运动,腾讯新闻,QQ邮箱提醒,微信广告,广告通知";

    /// <summary>已推送消息去重缓存保留时长（小时）。</summary>
    public int DedupHours { get; set; } = 24;

    /// <summary>
    /// 长文本跑马灯滚动速度（DIP/秒）。适用于灵动岛歌名、任务状态与 ntfy 通知正文；
    /// 文本超出容器才滚动，悬停暂停。默认 45（平缓），范围 10~120。
    /// </summary>
    public int MarqueeSpeed { get; set; } = 45;

    /// <summary>
    /// 音乐 App 白名单（v0.2.4 需求 6：功能已删除，用户不需要）。
    /// 字段标记 <see cref="JsonIgnoreAttribute"/>——旧配置里的值直接忽略，
    /// 音乐名单改为 <see cref="Services.MediaPlayerMonitor.MusicApps"/> 内置固定。
    /// </summary>
    [JsonIgnore]
    [Obsolete("v0.2.4 起音乐白名单功能已移除（需求 6），此字段不再序列化、不再生效。")]
    public string MusicAppWhitelist { get; set; } = string.Empty;

    /// <summary>
    /// 浏览器选择（v0.2.4，需求 7）：Chrome / Edge / Quark / Other。
    /// 前三项由程序自动定位安装路径写入 <see cref="BrowserPath"/>；Other 用
    /// <see cref="BrowserPath"/> 指向用户拖入/粘贴的浏览器快捷方式或 exe。
    /// </summary>
    public string BrowserChoice { get; set; } = "System";

    /// <summary>
    /// 浏览器可执行文件路径（点击 ntfy 通知时唤起）。留空 = 系统默认浏览器。
    /// 选前三项固定浏览器时由程序写入探测到的路径；Other 时为用户提供的路径。
    /// </summary>
    public string BrowserPath { get; set; } = string.Empty;

    /// <summary>
    /// 日志保存目录（v0.2.4，需求 8）。空 = 默认（便携模式为程序目录 logs，否则 %AppData%\NotifyHub\logs）。
    /// </summary>
    public string LogDir { get; set; } = string.Empty;

    /// <summary>
    /// 外观主题（v0.2.6，需求 3）：
    ///   SolidDark  = 纯色暗黑（原有视觉，完全不透明感）
    ///   GlassDark  = 深色毛玻璃（半透明，深色背景下用）
    ///   GlassLight = 浅色/白色毛玻璃（半透明，浅色背景下用）
    ///   GlassAuto  = 毛玻璃 + 跟随 Windows 应用主题自动选深/浅
    /// </summary>
    public string ThemeMode { get; set; } = "GlassAuto";

    /// <summary>
    /// 文字配色（v0.2.6，需求 4）：Auto = 按背景深浅自动取对比度更高的颜色；
    /// White / Black = 用户强制指定（毛玻璃背景不确定时的兜底）。
    /// </summary>
    public string TextTone { get; set; } = "Auto";

    /// <summary>
    /// 双行歌词（v0.2.6，需求 5；v0.2.7 需求 1 明确为「歌词总开关」）：
    /// 音乐播放时在音乐控制区下方显示两行歌词（跑马灯）。关掉则恢复单行布局。
    /// </summary>
    public bool LyricsEnabled { get; set; } = true;

    /// <summary>歌词字号（v0.2.7 需求 1，DIP）：当前行取 size+1.5，下一行取 size-1.5。</summary>
    public int LyricsFontSize { get; set; } = 13;

    /// <summary>
    /// 超时自动收纳总开关（v0.2.7 需求 2）。关闭后灵动岛在内容存在期间一直展开，
    /// 不再收成顶部细条。
    /// </summary>
    public bool AutoCollapseEnabled { get; set; } = true;

    /// <summary>
    /// 超时自动收纳时长（秒，v0.2.7 需求 2，默认 3 秒）。
    /// 有新消息 / 新任务 / 状态更新时重新计时；固定与自由模式统一按它收纳。
    /// </summary>
    public int AutoCollapseSeconds { get; set; } = 3;

    /// <summary>
    /// 音乐播放期间保持展开（不做自动收纳）。默认开——否则 3 秒就收成细条，歌词没得看。
    /// </summary>
    public bool KeepOpenWhilePlaying { get; set; } = true;

    /// <summary>
    /// 灵动岛总长度（百分比，v0.2.9 需求 6）：100 = 默认自适应宽度，
    /// 小于 100 整体收紧，大于 100 允许更长（长歌词能显示更多字）。
    /// 只作用于"宽度上限"，内容短时岛仍然会自己收窄，不会硬撑。
    /// </summary>
    public int IslandWidthPercent { get; set; } = 100;

    /// <summary>
    /// 岛内左右区域分配比例（v0.2.9 需求 6）：左 = 播放器/歌词区占比（百分比）。
    /// 50 = 均衡；调小 → 播放器变窄、右侧变宽；调大 → 播放器变宽、右侧变窄。
    /// </summary>
    public int IslandSplitRatio { get; set; } = 50;

    /// <summary>岛总长度允许范围（百分比）。</summary>
    public const int MinIslandWidthPercent = 60;

    public const int MaxIslandWidthPercent = 160;

    /// <summary>左右分配比例允许范围（百分比）。</summary>
    public const int MinIslandSplitRatio = 25;

    public const int MaxIslandSplitRatio = 75;

    /// <summary>歌词字号允许范围。</summary>
    public const int MinLyricsFontSize = 10;

    public const int MaxLyricsFontSize = 22;

    /// <summary>自动收纳时长允许范围（秒）。</summary>
    public const int MinAutoCollapseSeconds = 1;

    public const int MaxAutoCollapseSeconds = 60;

    /// <summary>主题候选值（设置页下拉与此一致）。</summary>
    public static readonly string[] ThemeModes = { "SolidDark", "GlassDark", "GlassLight", "GlassAuto" };

    /// <summary>文字配色候选值。</summary>
    public static readonly string[] TextTones = { "Auto", "White", "Black" };

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    public void Normalize()
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, MinPollIntervalMs, MaxPollIntervalMs);
        NotificationDurationMs = Math.Clamp(NotificationDurationMs, MinDurationMs, MaxDurationMs);
        MaxStack = Math.Clamp(MaxStack, 1, MaxStackLimit);
        NtfyUrl = (NtfyUrl ?? string.Empty).Trim();
        IslandMode = string.Equals(IslandMode, "Free", StringComparison.OrdinalIgnoreCase) ? "Free" : "Fixed";
        NotificationCorner = NormalizeCorner(NotificationCorner);
        MessageSoundVolume = Math.Clamp(MessageSoundVolume, 0, 100);
        TaskSoundVolume = Math.Clamp(TaskSoundVolume, 0, 100);
        MessageSoundStyle = NormalizeSoundStyle(MessageSoundStyle,
            "DingDong", "Crystal", "Bubble", "Marimba", "Digital");
        TaskSoundStyle = NormalizeSoundStyle(TaskSoundStyle,
            "Ascend", "Success", "Bell", "Chord");
        DedupHours = Math.Clamp(DedupHours, 1, 168);
        MarqueeSpeed = Math.Clamp(MarqueeSpeed, 10, 120);
        WeChatBlockedKeywords = (WeChatBlockedKeywords ?? string.Empty).Trim();

        // v0.2.4：多订阅列表清理（去空项、颜色下标收敛、旧单地址迁移兜底）
        NtfySubscriptions ??= new List<NtfySubscription>();
        NtfySubscriptions = NtfySubscriptions
            .Where(s => s is not null)
            .Select(s =>
            {
                s.Url = (s.Url ?? string.Empty).Trim();
                s.Name = (s.Name ?? string.Empty).Trim();
                s.ColorIndex = Math.Clamp(s.ColorIndex, 0, NtfyPalette.Count - 1);
                return s;
            })
            .Where(s => s.Url.Length > 0)
            .ToList();

        if (NtfySubscriptions.Count == 0 && NtfyUrl.Length > 0)
        {
            // 旧配置只有单地址：迁移成第一个订阅（保持原行为）
            NtfySubscriptions.Add(new NtfySubscription
            {
                Enabled = NtfyEnabled,
                Name = string.Empty,
                Url = NtfyUrl,
                ColorIndex = 0,
            });
        }

        NtfyPushUrl = (NtfyPushUrl ?? string.Empty).Trim();
        BrowserChoice = NormalizeBrowserChoice(BrowserChoice);
        BrowserPath = (BrowserPath ?? string.Empty).Trim();
        LogDir = (LogDir ?? string.Empty).Trim();

        // v0.2.6 外观：非法值回落默认（纯色暗黑 / 自动文字色）
        ThemeMode = ThemeModes.FirstOrDefault(m => string.Equals(m, ThemeMode, StringComparison.OrdinalIgnoreCase))
                    ?? "SolidDark";
        TextTone = TextTones.FirstOrDefault(t => string.Equals(t, TextTone, StringComparison.OrdinalIgnoreCase))
                   ?? "Auto";

        // v0.2.7：歌词字号与自动收纳时长收敛到安全区间
        LyricsFontSize = Math.Clamp(LyricsFontSize, MinLyricsFontSize, MaxLyricsFontSize);
        AutoCollapseSeconds = Math.Clamp(AutoCollapseSeconds, MinAutoCollapseSeconds, MaxAutoCollapseSeconds);

        // v0.2.9 需求 6：岛宽度与左右分配比例
        if (IslandWidthPercent <= 0)
        {
            IslandWidthPercent = 100; // 旧配置缺字段时反序列化成 0，按默认处理
        }

        if (IslandSplitRatio <= 0)
        {
            IslandSplitRatio = 50;
        }

        IslandWidthPercent = Math.Clamp(IslandWidthPercent, MinIslandWidthPercent, MaxIslandWidthPercent);
        IslandSplitRatio = Math.Clamp(IslandSplitRatio, MinIslandSplitRatio, MaxIslandSplitRatio);
    }

    /// <summary>浏览器选择收敛到合法值。</summary>
    public static string NormalizeBrowserChoice(string? choice) => choice switch
    {
        "Chrome" => "Chrome",
        "Edge" => "Edge",
        "Quark" => "Quark",
        "Other" => "Other",
        _ => "System",
    };

    /// <summary>
    /// ntfy 订阅通知色板（需求 2：低饱和度微调，与微信绿 #07C160 / QQ蓝 #12B7F5 /
    /// ntfy橙 #F97316 同一明亮但不刺眼的风格）。索引 0 保持原 ntfy 橙色。
    /// </summary>
    public static readonly IReadOnlyList<uint> NtfyPalette = new[]
    {
        0xF97316u, // 0 橙（默认，与原 ntfy 一致）
        0x5B8DEFu, // 1 雾蓝
        0x3FA96Bu, // 2 青绿
        0x8E7BD8u, // 3 薰衣草紫
        0xC96A8Eu, // 4 玫粉
    };

    public static string NormalizeCorner(string? corner) => corner switch
    {
        "TopLeft" => "TopLeft",
        "BottomRight" => "BottomRight",
        "BottomLeft" => "BottomLeft",
        _ => "TopRight",
    };

    /// <summary>音色名收敛到白名单，非法值回落默认。</summary>
    private static string NormalizeSoundStyle(string? style, string fallback, params string[] allowed)
    {
        foreach (var a in allowed)
        {
            if (string.Equals(style, a, StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }

        return fallback;
    }
}
