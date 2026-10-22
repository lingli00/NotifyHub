namespace NotifyHub.Models;

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

    /// <summary>【WorkBuddy 任务完成提示音】开关（与消息提示音互相独立）。</summary>
    public bool TaskSoundEnabled { get; set; } = true;

    /// <summary>【WorkBuddy 任务完成提示音】音量 0-100。</summary>
    public int TaskSoundVolume { get; set; } = 80;

    /// <summary>微信通知过滤：只推好友私聊与未免打扰群，拦截公众号/服务通知/免打扰群。</summary>
    public bool WeChatFilterEnabled { get; set; } = true;

    /// <summary>额外拦截关键词（逗号分隔，命中会话名或摘要即拦截）。</summary>
    public string WeChatBlockedKeywords { get; set; } =
        "服务通知,微信团队,微信支付,订阅号消息,公众号,看一看,搜一搜,游戏中心,微信运动,腾讯新闻,QQ邮箱提醒,微信广告,广告通知";

    /// <summary>已推送消息去重缓存保留时长（小时）。</summary>
    public int DedupHours { get; set; } = 24;

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
        DedupHours = Math.Clamp(DedupHours, 1, 168);
        WeChatBlockedKeywords = (WeChatBlockedKeywords ?? string.Empty).Trim();
    }

    public static string NormalizeCorner(string? corner) => corner switch
    {
        "TopLeft" => "TopLeft",
        "BottomRight" => "BottomRight",
        "BottomLeft" => "BottomLeft",
        _ => "TopRight",
    };
}
