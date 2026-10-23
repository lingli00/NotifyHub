using System.Diagnostics;

namespace NotifyHub.Services;

/// <summary>
/// 播放进度估算器（v0.2.7 需求 1 的根因修复）。
///
/// 为什么需要它：
///   SMTC 的 <c>TimelineProperties.Position</c> 不是"实时进度"，而是播放器最后一次
///   上报时刻的快照。大多数播放器只在 拖动进度 / 暂停 / 播放 / 切歌 时才刷新它，
///   正常播放期间这个值会**一直停在原处**。直接拿它去比对歌词时间轴，表现就是
///   「歌词卡死在某一行，手动暂停再播放才跳一下」。
///
/// 做法：
///   1. 锚定：每次读到一个**变化了的**上报值，就以它为锚点记录当时的时间戳；
///   2. 外推：两次上报之间，用墙钟（Stopwatch，单调递增，不受系统改时间影响）推进；
///   3. 纠偏：新上报值与外推值差得远（&gt;1.5s）说明用户拖了进度或切了歌，直接采纳；
///      差得小则是播放器按整秒取整造成的量化误差，只做温和收敛，避免进度来回抽搐；
///   4. 暂停：冻结在当前外推值，恢复播放时重新锚定上报值。
///
/// 纯逻辑、无 WinRT 依赖，便于自检直接构造场景验证。
/// </summary>
public sealed class PlaybackClock
{
    /// <summary>上报值与外推值偏差超过这个秒数，判定为拖动进度/切歌。</summary>
    private const double SeekThresholdSeconds = 1.5;

    /// <summary>上报值变化小于这个秒数视为"播放器没刷新"（浮点噪声）。</summary>
    private const double FreshEpsilonSeconds = 0.02;

    /// <summary>正常推进时向上报值收敛的比例（0=完全不采纳，1=直接采纳）。</summary>
    private const double SmoothFactor = 0.35;

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

    /// <summary>换歌 / 无播放会话时清空，避免把上一首的进度带过来。</summary>
    public void Reset()
    {
        _started = false;
        _lastReport = double.NaN;
        _anchorPosition = 0;
        _anchorTicks = 0;
        _playing = false;
    }

    /// <summary>
    /// 用一次 SMTC 上报值推进估算，返回「此刻」的播放位置（秒）。
    /// </summary>
    /// <param name="reported">播放器上报的位置（秒），取自 TimelineProperties。</param>
    /// <param name="playing">播放器当前是否在播放。</param>
    /// <param name="nowTicks">当前时间戳（<see cref="Stopwatch.GetTimestamp"/>）。</param>
    public double Update(double reported, bool playing, long nowTicks)
    {
        if (double.IsNaN(reported) || double.IsInfinity(reported) || reported < 0)
        {
            reported = 0;
        }

        if (!_started)
        {
            _started = true;
            _lastReport = reported;
            _anchorPosition = reported;
            _anchorTicks = nowTicks;
            _playing = playing;
            return reported;
        }

        // 播放/暂停切换：以最新上报值重新锚定（暂停时冻结，恢复时从当前位置继续）
        if (playing != _playing)
        {
            _playing = playing;
            _lastReport = reported;
            _anchorPosition = reported;
            _anchorTicks = nowTicks;
            return reported;
        }

        var projected = _anchorPosition + (_playing ? ElapsedSeconds(nowTicks) : 0);

        // 播放器没刷新位置 → 继续外推。这就是「歌词卡死」的修复点。
        if (Math.Abs(reported - _lastReport) < FreshEpsilonSeconds)
        {
            _anchorPosition = projected;
            _anchorTicks = nowTicks;
            return projected;
        }

        _lastReport = reported;
        var error = reported - projected;

        if (Math.Abs(error) > SeekThresholdSeconds)
        {
            // 拖动进度条 / 切歌：上报值是权威值，直接采纳
            _anchorPosition = reported;
        }
        else
        {
            // 正常推进：温和收敛，避免整秒量化导致的来回抖动
            _anchorPosition = projected + (error * SmoothFactor);
        }

        _anchorTicks = nowTicks;

        // 时长为 0 的直播流：位置只增不减即可，不做额外约束
        return _anchorPosition;
    }

    private double ElapsedSeconds(long nowTicks)
        => Math.Max(0, (nowTicks - _anchorTicks) / (double)Stopwatch.Frequency);
}
