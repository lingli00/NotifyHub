using System.Diagnostics;

namespace NotifyHub.Services;

/// <summary>
/// 播放进度估算器（v0.2.7 引入，v0.2.9 重写）。
///
/// 为什么需要它：
///   SMTC 的 <c>TimelineProperties.Position</c> 不是"实时进度"，而是播放器最后一次
///   上报时刻的快照。大多数播放器只在 拖动进度 / 暂停 / 播放 / 切歌 时才刷新它，
///   正常播放期间这个值会**一直停在原处**。直接拿它去比对歌词时间轴，表现就是
///   「歌词卡死在某一行，手动暂停再播放才跳一下」。
///
/// v0.2.9 修掉的两个真问题（对应「歌词滞后约 1 秒」的反馈）：
///   1) **上报年龄**：<c>TimelineProperties.LastUpdatedTime</c> 告诉我们这个位置是
///      "哪一刻的位置"。旧版直接把它当"现在"，于是整条时间轴恒定落后一个上报周期。
///      现在锚点 = 上报位置 + 上报年龄（0~10 秒之间才采信，防止个别播放器给脏值）。
///   2) **向下拽**：旧版对每次上报做 <c>projected + error*0.35</c> 的温和收敛。
///      当播放器每次上报的值都落后于外推值（就是第 1 条的固定滞后），这个收敛
///      会每秒把估算值往回调 1.7 秒，稳态被硬生生拽回到滞后的上报值上——
///      这是"总是差一秒"的直接原因。
///      现在：播放中位置**只增不减**；上报值落后视为陈旧，直接忽略继续外推；
///      只有上报值超前（说明墙钟外推偏慢）或发生大幅跳变（seek/切歌）才采纳。
///
/// 纯逻辑、无 WinRT 依赖，便于自检直接构造场景验证。
/// </summary>
public sealed class PlaybackClock
{
    /// <summary>
    /// 这一帧上报的位置是否**明显不属于当前这首歌**（v0.2.13）。
    ///
    /// 真实场景（2026-10-23 20:16 日志）：Folia 切到《牵丝戏》后立刻读了 timeline，
    /// 播放器报的还是上一首的 239.36s，而新歌才刚开始、总长 210s —— 于是歌词一下跳到
    /// 第 29 行，日志打出"换句 #29 偏差 20702ms"。用户看到的就是"歌词时间对不上"。
    ///
    /// 两条判据：
    ///   · 位置 ≥ 总时长（且总时长有效）→ 不可能是这首歌的位置；
    ///   · 切歌后 2.5 秒内，位置远大于"实际流逝的时间"→ 上一首的残留。
    /// 命中的处理是"这次当作 0 并重置估算器"，而不是硬把播放器报的值拉回来。
    /// </summary>
    public static bool LooksStaleOnTrackChange(
        double reportedSeconds, double secondsSinceTrackChange, double durationSeconds)
    {
        if (reportedSeconds <= 0)
        {
            return false;
        }

        if (durationSeconds > 0.5 && reportedSeconds > durationSeconds - 0.25)
        {
            return true;
        }

        return secondsSinceTrackChange >= 0
               && secondsSinceTrackChange < 2.5
               && reportedSeconds > secondsSinceTrackChange + 3;
    }

    /// <summary>上报值与外推值偏差超过这个秒数，判定为拖动进度/切歌。</summary>
    private const double SeekThresholdSeconds = 1.5;

    /// <summary>上报值变化小于这个秒数视为"播放器没刷新"（浮点噪声）。</summary>
    private const double FreshEpsilonSeconds = 0.02;

    /// <summary>上报年龄的采信上限（秒）：超过这个值当作脏数据，不加补偿。</summary>
    private const double MaxReportAgeSeconds = 10.0;

    /// <summary>上报值落后外推值超过这个量，视为"陈旧上报"，忽略。</summary>
    private const double StaleToleranceSeconds = 0.08;

    private double _anchorPosition;
    private long _anchorTicks;
    private double _lastReport = double.NaN;
    private bool _playing;
    private bool _started;

    /// <summary>当前估算位置（秒）。</summary>
    public double Position => _anchorPosition;

    /// <summary>当前是否处于播放态。</summary>
    public bool IsPlaying => _playing;

    /// <summary>是否已经拿到过一次上报值（用于诊断）。</summary>
    public bool HasAnchor => _started;

    /// <summary>最近一次上报值的年龄（秒，诊断用；用于核对"歌词为什么偏"）。</summary>
    public double LastReportAge { get; private set; }

    /// <summary>被判定为陈旧而忽略的上报次数（诊断用）。</summary>
    public int StaleDrops { get; private set; }

    /// <summary>换歌 / 无播放会话时清空，避免把上一首的进度带过来。</summary>
    public void Reset()
    {
        _started = false;
        _lastReport = double.NaN;
        _anchorPosition = 0;
        _anchorTicks = 0;
        _playing = false;
        LastReportAge = 0;
        StaleDrops = 0;
    }

    /// <summary>
    /// 用一次 SMTC 上报值推进估算，返回「此刻」的播放位置（秒）。
    /// </summary>
    /// <param name="reported">播放器上报的位置（秒），取自 TimelineProperties。</param>
    /// <param name="playing">播放器当前是否在播放。</param>
    /// <param name="nowTicks">当前时间戳（<see cref="Stopwatch.GetTimestamp"/>）。</param>
    /// <param name="reportAgeSeconds">
    /// 这个上报值是"多久以前"的位置（当前时刻 - LastUpdatedTime，秒）。
    /// 传 0 或负值表示未知，按 0 处理；超过 10 秒按 0 处理（脏数据）。
    /// </param>
    public double Update(double reported, bool playing, long nowTicks, double reportAgeSeconds = 0)
    {
        if (double.IsNaN(reported) || double.IsInfinity(reported) || reported < 0)
        {
            reported = 0;
        }

        // 上报年龄补偿：把"某刻的位置"换算成"此刻的位置"
        var age = double.IsNaN(reportAgeSeconds) || double.IsInfinity(reportAgeSeconds)
            ? 0
            : Math.Clamp(reportAgeSeconds, 0, MaxReportAgeSeconds);
        LastReportAge = age;

        var correctedReport = playing ? reported + age : reported;

        if (!_started)
        {
            _started = true;
            _lastReport = reported;
            _anchorPosition = correctedReport;
            _anchorTicks = nowTicks;
            _playing = playing;
            return _anchorPosition;
        }

        // 播放/暂停切换：以最新上报值重新锚定（暂停时冻结，恢复时从当前位置继续）
        if (playing != _playing)
        {
            _playing = playing;
            _lastReport = reported;
            _anchorPosition = correctedReport;
            _anchorTicks = nowTicks;
            return _anchorPosition;
        }

        var elapsed = ElapsedSeconds(nowTicks);
        var projected = _anchorPosition + (_playing ? elapsed : 0);

        // 播放器没刷新位置 → 继续外推。这就是「歌词卡死」的修复点。
        if (Math.Abs(reported - _lastReport) < FreshEpsilonSeconds)
        {
            _anchorPosition = projected;
            _anchorTicks = nowTicks;
            return projected;
        }

        _lastReport = reported;
        var delta = correctedReport - projected;

        // 播放中位置只增不减：上报值明显落后于外推值 = 上报本身陈旧（播放器 ticking 慢），
        // 忽略它，继续用墙钟外推。旧版在这里做温和收敛，等于把正确的进度往错误的值上拽。
        if (_playing && delta < -StaleToleranceSeconds && Math.Abs(delta) <= SeekThresholdSeconds)
        {
            StaleDrops++;
            _anchorPosition = projected;
            _anchorTicks = nowTicks;
            return projected;
        }

        // 大幅跳变（前后都算）= 用户拖进度条 / 切歌 / 循环重播：上报值是权威值
        if (Math.Abs(delta) > SeekThresholdSeconds)
        {
            _anchorPosition = correctedReport;
        }
        else
        {
            // 小幅超前/落后：以投影为基准直接对齐到上报值，不做比例收敛
            // （比例收敛会把误差长期留在系统里，正是旧版"总差一点"的来源之一）
            _anchorPosition = correctedReport;
        }

        _anchorTicks = nowTicks;
        return _anchorPosition;
    }

    private double ElapsedSeconds(long nowTicks)
        => Math.Max(0, (nowTicks - _anchorTicks) / (double)Stopwatch.Frequency);
}
