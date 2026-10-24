namespace NotifyHub.UI;

/// <summary>「长按 + 上滑」手势的采样结果。</summary>
public enum SwipeGestureState
{
    /// 没有按下或在等待第一帧。</summary>
    Idle,

    /// 已按下，正在跟踪。</summary>
    Tracking,

    /// 本帧触发强制收纳。</summary>
    Triggered,
}

/// <summary>
/// 「长按鼠标 + 向上滑动」手势识别（v0.2.17 工单C）。
///
/// 纯状态机、不依赖 WPF：调用方每帧喂入 (是否按下, 光标Y, 单调时钟Ms)，
/// 它只回答"这一帧算不算触发"。抽成独立类是为了让自检能直接构造时序断言
/// （按下→不动→松手 / 按下→等够长按→上滑 / 快速大距离上滑），不需要把窗口跑起来。
///
/// 判定规则：
///   · 按下瞬间记录起点 Y 与时刻；
///   · 按住至少 <paramref name="holdMs"/> 且向上滑过 <paramref name="swipePx"/> → 触发；
///   · 快速大距离上滑（≥ <see cref="FastSwipePx"/>，默认 56px）不看按住时长——顺手一甩也算；
///   · 松手 / 未按下 → 回到 Idle，等下一次按下；
///   · 触发后保持 Triggered 直到 <see cref="Reset"/>（防止一帧内重复触发）。
/// </summary>
public sealed class SwipeUpCollapseTracker
{
    /// <summary>快速上滑的距离阈值（物理像素）：不看按住时长的"顺手一甩"。</summary>
    public const int FastSwipePx = 56;

    private bool _pressed;
    private int _startY;
    private long _startMs;
    private bool _triggered;

    /// 当前是否正在跟踪（已按下且未触发）。</summary>
    public bool IsTracking => _pressed && !_triggered;

    /// 是否已触发（等待 Reset）。</summary>
    public bool IsTriggered => _triggered;

    /// 清空跟踪状态（光标离开岛 / 强制收纳后调用）。</summary>
    public void Reset()
    {
        _pressed = false;
        _triggered = false;
    }

    /// <summary>
    /// 喂入一帧采样。<paramref name="nowMs"/> 必须是单调时钟（Environment.TickCount64）。
    /// </summary>
    public SwipeGestureState Sample(bool pressing, int y, long nowMs, int holdMs, int swipePx)
    {
        if (_triggered)
        {
            return SwipeGestureState.Triggered;
        }

        if (!pressing)
        {
            _pressed = false;
            return SwipeGestureState.Idle;
        }

        if (!_pressed)
        {
            _pressed = true;
            _startY = y;
            _startMs = nowMs;
            return SwipeGestureState.Tracking;
        }

        var dy = _startY - y; // 向上为正
        var held = nowMs - _startMs;

        if (dy >= FastSwipePx || (dy >= swipePx && held >= holdMs))
        {
            _triggered = true;
            return SwipeGestureState.Triggered;
        }

        return SwipeGestureState.Tracking;
    }
}
