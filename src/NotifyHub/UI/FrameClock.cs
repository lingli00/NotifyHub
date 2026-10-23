using System.Diagnostics;
using System.Windows.Media;

namespace NotifyHub.UI;

/// <summary>
/// 帧时钟（v0.2.9 需求 1）：把 <see cref="CompositionTarget.Rendering"/> 收敛成「一帧一次」。
///
/// 为什么需要它：
///   Rendering 回调**不是**"每个 vsync 一次"。只要 WPF 需要提交一帧，就会把事件派发给所有订阅者，
///   而同一帧里如果同时有多个窗口在布局、或多个动画各自 InvalidateVisual，同一个
///   RenderingTime 会被重复派发多次（实测 1080p@60Hz 下最坏一帧能触发 3 次）。
///   高刷屏上这个放大倍数更高，重复算一遍偏移既白烧 CPU，也让"按耗时推进"的动画在
///   同一帧里被切碎成不等距的小步长，观感反而发涩。
///
///   用 <see cref="RenderingEventArgs.RenderingTime"/> 做去重，就能保证：
///     · 每一帧最多更新一次，步长 = 真实帧间隔（60Hz→16.7ms，144Hz→6.9ms）
///     · 动画速度只跟时间有关，与刷新率、与订阅者数量都无关
///   这就是"动画跟随屏幕刷新率自适应"的实现方式——不需要知道屏幕是 60 还是 144。
/// </summary>
internal static class FrameClock
{
    /// <summary>本帧是否应该更新。<paramref name="lastFrame"/> 由调用方持有。</summary>
    public static bool ShouldTick(ref TimeSpan lastFrame, EventArgs e)
    {
        if (e is not RenderingEventArgs args)
        {
            // 非 WPF 合成事件（测试/异常路径）：照常放行
            return true;
        }

        if (args.RenderingTime == lastFrame)
        {
            return false;
        }

        lastFrame = args.RenderingTime;
        return true;
    }

    /// <summary>重置去重游标（动画刚开始时调用，避免首帧被误判为重复）。</summary>
    public static void Reset(ref TimeSpan lastFrame) => lastFrame = TimeSpan.MinValue;
}

/// <summary>
/// 帧率实测（--fps-test）：跑固定秒数，统计真实帧率与卡顿情况。
/// 用来验证"高刷屏能跑满刷新率"，也用来回归 60Hz 下不掉帧。
/// </summary>
internal sealed class FpsMeter
{
    private readonly List<double> _intervals = new();
    private readonly Stopwatch _watch = new();
    private TimeSpan _lastFrame = TimeSpan.MinValue;
    private double _lastMs = -1;

    public void Start()
    {
        _intervals.Clear();
        _lastFrame = TimeSpan.MinValue;
        _lastMs = -1;
        _watch.Restart();
    }

    /// <summary>喂一帧。做去重，重复派发的同帧不计入。</summary>
    public void Tick(EventArgs e)
    {
        if (!_watch.IsRunning)
        {
            return;
        }

        if (!FrameClock.ShouldTick(ref _lastFrame, e))
        {
            return;
        }

        var ms = _watch.Elapsed.TotalMilliseconds;
        if (_lastMs >= 0)
        {
            _intervals.Add(ms - _lastMs);
        }

        _lastMs = ms;
    }

    public FpsReport Finish(int refreshHz)
    {
        _watch.Stop();

        var totalMs = _watch.Elapsed.TotalMilliseconds;
        var frames = _intervals.Count + 1;
        var avgFps = totalMs > 1 ? frames / (totalMs / 1000.0) : 0;

        double avgInterval = 0;
        double maxInterval = 0;
        var jank = 0;

        if (_intervals.Count > 0)
        {
            avgInterval = _intervals.Average();
            maxInterval = _intervals.Max();

            // 超过 1.6 帧视为一次可感知卡顿（正常抖动在 ±3ms 内）
            var threshold = (avgInterval > 0 ? avgInterval : 16.7) * 1.6;
            jank = _intervals.Count(v => v > threshold);
        }

        return new FpsReport(frames, totalMs, avgFps, avgInterval, maxInterval, jank, refreshHz);
    }
}

internal sealed record FpsReport(
    int Frames,
    double TotalMs,
    double AvgFps,
    double AvgIntervalMs,
    double MaxIntervalMs,
    int JankFrames,
    int RefreshHz)
{
    public string ToReport()
    {
        var lines = new List<string>
        {
            $"采样时长      {TotalMs / 1000.0:F2} s",
            $"渲染帧数      {Frames}",
            $"平均帧率      {AvgFps:F1} FPS",
            $"平均帧间隔    {AvgIntervalMs:F2} ms",
            $"最大帧间隔    {MaxIntervalMs:F2} ms",
            $"卡顿帧        {JankFrames}（> 1.6× 平均间隔）",
        };

        if (RefreshHz > 0)
        {
            var ratio = AvgFps / RefreshHz;
            lines.Add($"屏幕刷新率    {RefreshHz} Hz → 达成率 {ratio * 100:F0}%");
            lines.Add(ratio switch
            {
                >= 0.92 => "结论          已跑满刷新率",
                >= 0.75 => "结论          基本流畅，偶有空帧（多为窗口首次呈现）",
                _ => "结论          未跑满，需检查是否被硬件加速/远程桌面限制",
            });
        }
        else
        {
            lines.Add("屏幕刷新率    未探测到（不影响动画速度，动画按真实耗时推进）");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
