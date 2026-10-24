namespace NotifyHub.UI;

/// <summary>
/// 布局实况预览的内容来源（v0.2.19 需求2）。
///
/// <para><see cref="Auto"/> 是默认值：**真实优先**——机器上正在放歌 / 正在跑任务时，
/// 直接拿当前真实的岛状态来调参；只有对应数据都没有时才用虚拟样板兜底。</para>
///
/// <para>其余四项是"强制样板"，用于没有真实数据时也能预览完整布局（含歌词、通知形态）。</para>
/// </summary>
public enum PreviewScene
{
    /// <summary>真实优先：有真实内容就用真实内容，没有才用样板兜底。</summary>
    Auto,

    /// <summary>仅音乐播放（曲目 + 歌手 + 歌词）。</summary>
    Music,

    /// <summary>仅 WorkBuddy 任务。</summary>
    Task,

    /// <summary>仅消息通知（岛内空闲时收到推送的形态）。</summary>
    Notice,

    /// <summary>音乐 + 任务双内容（左右分栏最挤的形态）。</summary>
    Both,
}
