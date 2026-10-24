namespace NotifyHub.UI;

/// <summary>「按住上滑后松手」手势的采样结果。</summary>
public enum SwipeGestureState
{
    /// <summary>没有按下、或按下但没有有效位移。</summary>
    Idle,

    /// <summary>已按下，正在跟踪上滑距离。</summary>
    Tracking,

    /// <summary>本次**松手**判定为收纳手势（只报一次）。</summary>
    Triggered,
}

/// <summary>
/// 「按住左键上滑 → 松手收纳」手势识别（v0.2.19 需求8）。
///
/// 与旧版的区别：**触发时机在松开鼠标那一刻**，而不是滑到阈值就立刻收——
/// 手感更像"拖上去、松手即收"，也不会在拖动途中被半路打断。
///
/// 纯状态机、不依赖 WPF：调用方每帧喂 (是否按下, 光标Y, 单调时钟Ms, 阈值)，
/// 它只回答"这一帧算不算触发"。抽成独立类是为了让自检能构造时序断言。
///
/// 规则：
///   · 按下记录起点 Y，持续记录最大上滑距离；
///   · 松手时若最大上滑 ≥ swipePx → Triggered（一次），随即复位；
///   · 松手时位移不足 → 视为普通点击，Idle；
///   · 不需要按住多久（没有长按判定）。
/// </summary>
public sealed class SwipeUpCollapseTracker
{
    private bool _pressed;
    private int _startY;
    private int _maxUp;
    private bool _triggered;

    /// <summary>当前是否正在跟踪（已按下且未触发）。</summary>
    public bool IsTracking => _pressed && !_triggered;

    /// <summary>本次按住期间的最大上滑距离（物理像素，诊断用）。</summary>
    public int MaxUpPx => _maxUp;

    /// <summary>清空跟踪状态（光标离开 / 强制收纳后调用）。</summary>
    public void Reset()
    {
        _pressed = false;
        _maxUp = 0;
        _triggered = false;
    }

    /// <summary>
    /// 喂入一帧采样。<paramref name="nowMs"/> 必须是单调时钟（Environment.TickCount64），
    /// 保留只为调用方统一传参（本手势不消费时长）。
    /// </summary>
    public SwipeGestureState Sample(bool pressing, int y, long nowMs, int swipePx)
    {
        if (!pressing)
        {
            if (!_pressed)
            {
                return SwipeGestureState.Idle;
            }

            // 松手：按下期间上滑够远才算收纳（触发一次后复位，不会连报）
            _pressed = false;
            _triggered = _maxUp >= swipePx;
            _maxUp = 0;

            return _triggered ? SwipeGestureState.Triggered : SwipeGestureState.Idle;
        }

        if (!_pressed)
        {
            _pressed = true;
            _startY = y;
            _maxUp = 0;
            _triggered = false;
            return SwipeGestureState.Tracking;
        }

        _maxUp = Math.Max(_maxUp, _startY - y);
        return SwipeGestureState.Tracking;
    }
}
