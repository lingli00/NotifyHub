namespace NotifyHub.UI;

/// <summary>
/// 布局实况预览的场景（v0.2.18 需求1 改版）：设置页选定后，直接把对应的
/// 虚拟样板内容铺到屏幕上真实的灵动岛上，不另建预览面板。
/// </summary>
public enum PreviewScene
{
    /// <summary>仅音乐播放（曲目 + 歌手 + 歌词）。</summary>
    Music,

    /// <summary>仅 WorkBuddy 任务。</summary>
    Task,

    /// <summary>仅消息通知（岛内空闲时收到推送的形态）。</summary>
    Notice,

    /// <summary>音乐 + 任务双内容（左右分栏最挤的形态）。</summary>
    Both,
}
