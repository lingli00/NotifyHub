namespace NotifyHub.UI;

/// <summary>「短按 + 上滑」手势的采样结果。</summary>
public enum SwipeGestureState
{
    /// <summary>没有按下或在等待第一帧。</summary>
    Idle,

    /// <summary>已按下，正在跟踪。</summary>
    Tracking,

    /// <summary>本帧触发强制收纳。</summary>
    Triggered,
}

/// <summary>
/// 「短按 + 上滑」手势识别（v0.2.18 需求2；v0.2.17 时为"长按 + 上滑"）。
///
/// 纯状态机、不依赖 WPF：调用方每帧喂入 (是否按下, 光标Y, 单调时钟Ms)，
/// 它只回答"这一帧算不算触发"。抽成独立类是为了让自检能直接构造时序断言
/// （按下不动 / 按下上滑 / 松手重置），不需要把窗口跑起来。
///
/// 判定规则（v0.2.18 起去掉长按时长要求）：
///   · 按下瞬间记录起点 Y；
///   · 向上滑过 <paramref name="swipePx"/>（物理像素）即触发——**不要求按住多久**；
///   · 单纯点击（没有位移）不会触发；
///   · 松手 / 未按下 → 回到 Idle，等下一次按下；
///   · 触发后保持 Triggered 直到 <see cref="Reset"/>（防止一帧内重复触发）。
/// </summary>
public sealed class SwipeUpCollapseTracker
{
    private bool _pressed;
    private int _startY;
    private bool _triggered;

    /// <summary>当前是否正在跟踪（已按下且未触发）。</summary>
    public bool IsTracking => _pressed && !_triggered;

    /// <summary>是否已触发（等待 Reset）。</summary>
    public bool IsTriggered => _triggered;

    /// <summary>清空跟踪状态（光标离开岛 / 强制收纳后调用）。</summary>
    public void Reset()
    {
        _pressed = false;
        _triggered = false;
    }

    /// <summary>
    /// 喂入一帧采样。<paramref name="nowMs"/> 必须是单调时钟（Environment.TickCount64），
    /// 保留只为调用方统一传参（短按上滑不消费时长）。
    /// </summary>
    public SwipeGestureState Sample(bool pressing, int y, long nowMs, int swipePx)
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
            return SwipeGestureState.Tracking;
        }

        // 向上为正；越过阈值即触发（v0.2.18：不再要求按住时长）
        if (_startY - y >= swipePx)
        {
            _triggered = true;
            return SwipeGestureState.Triggered;
        }

        return SwipeGestureState.Tracking;
    }
}
