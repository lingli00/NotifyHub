using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace NotifyHub.UI;

/// <summary>细条指示器的三种形态。</summary>
public enum StripMode
{
    /// <summary>没有内容（理论上看不到）。</summary>
    None,

    /// <summary>只有音乐在播：一段黄色光块在轨道里来回游动。</summary>
    Music,

    /// <summary>只有 WorkBuddy 在跑任务：琥珀色光块单向巡游（带拖尾）。</summary>
    Task,

    /// <summary>音乐 + 任务同时：黄色来回 + 琥珀单向，两条同时在轨道里跑。</summary>
    Both,

    /// <summary>岛内有新通知：用该来源的主题色（微信绿 / QQ 蓝 / ntfy 橙）单向巡游。</summary>
    Notice,
}

/// <summary>
/// 收纳态「跑道指示器」（v0.2.8 需求 4）。
///
/// 为什么要有它：岛收成细条之后只剩一个静态小圆点，用户完全看不出"现在是在放歌、
/// 还是在跑任务、还是两者都在"。这里用一条圆角跑道（running track）+ 轨道内游动的光块
/// 表达状态，三种形态一眼可分：
///
///   ● 仅音乐      → 黄色光块在轨道内**来回**游动（像音量电平在跳）
///   ● 仅任务      → 琥珀色光块**单向**巡游 + 拖尾（像进度在推进）
///   ● 音乐+任务   → 两条同时跑：琥珀单向 + 黄色反向，颜色与节奏都不同
///   ● 完成态      → 绿色，光块停在中间并轻微呼吸（保留原有的"呼吸灯"观感）
///
/// 实现：Control 级别以下的自绘元素（FrameworkElement + OnRender），
/// 所有动画挂在 <see cref="CompositionTarget.Rendering"/> 上按真实耗时推进，
/// 与刷新率同步（60fps 显示器每帧一次），不用 DispatcherTimer 轮询，
/// 因此不会出现"走两帧停一帧"的顿感。
/// </summary>
public sealed class StripIndicator : FrameworkElement
{
    private static readonly Color MusicColor = Color.FromRgb(0xFF, 0xD6, 0x0A);   // 与岛上状态点同款黄
    private static readonly Color TaskColor = Color.FromRgb(0xFF, 0xB0, 0x20);    // 执行中琥珀
    private static readonly Color DoneColor = Color.FromRgb(0x34, 0xC7, 0x59);    // 完成绿

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSeconds;
    private bool _hooked;

    private StripMode _mode = StripMode.None;
    private bool _done;

    /// <summary>
    /// 通知来源色（v0.2.9 需求 3）：<see cref="StripMode.Notice"/> 时用它画光块，
    /// 让收纳态也能一眼分辨"是微信、QQ 还是 ntfy 来的消息"。
    /// </summary>
    private Color? _accent;

    /// <summary>设置来源强调色（null = 用内置色）。</summary>
    public Color? AccentColor
    {
        get => _accent;
        set
        {
            if (Nullable.Equals(_accent, value))
            {
                return;
            }

            _accent = value;
            InvalidateVisual();
        }
    }

    /// <summary>当前实际使用的强调色（诊断用）。</summary>
    public Color EffectiveAccent => _accent ?? MusicColor;

    public StripIndicator()
    {
        IsHitTestVisible = false; // 命中交给外层 Border
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Hook();
            }
            else
            {
                Unhook();
            }
        };
    }

    /// <summary>当前形态。</summary>
    public StripMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            InvalidateVisual();
        }
    }

    /// <summary>是否完成态（整条转绿 + 静止呼吸）。</summary>
    public bool IsDone
    {
        get => _done;
        set
        {
            if (_done == value)
            {
                return;
            }

            _done = value;
            InvalidateVisual();
        }
    }

    private void Hook()
    {
        if (_hooked)
        {
            return;
        }

        _hooked = true;
        _lastSeconds = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Unhook()
    {
        if (!_hooked)
        {
            return;
        }

        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // 一帧只推进一次（v0.2.9 需求 1）：细条动画靠相位推进，重复派发会让彗星抖动
        if (!FrameClock.ShouldTick(ref _lastFrameTime, e))
        {
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastSeconds;
        _lastSeconds = now;

        // 完成态是静止画面，不需要逐帧重绘
        // 待命态通常不动（省 CPU），但悬停分区时要给"活的"反馈，所以放行
        if (_done || (_mode == StripMode.None && _hoverZone == StripZone.None))
        {
            return;
        }

        if (dt <= 0 || dt > 0.5)
        {
            return; // 首帧/休眠唤醒后跳变，跳过这一拍
        }

        _phase += dt;
        InvalidateVisual();
    }

    private double _phase;

    /// <summary>上一帧的合成时间戳（FrameClock 去重用，v0.2.9 需求 1）。</summary>
    private TimeSpan _lastFrameTime = TimeSpan.MinValue;

    /// <summary>动画相位（秒，诊断/自检用：连续两次采样应该变大说明在动）。</summary>
    public double Phase => _phase;

    private StripZone _hoverZone = StripZone.None;

    /// <summary>
    /// 悬停分区（v0.2.10 需求 2）：左段 = 音乐播放器、右段 = WorkBuddy。
    /// 悬停时**指示器配色跟着换**，让用户在点之前就知道这一半是干什么的：
    ///   左 → 音乐黄；右 → 任务琥珀。离开后回到按内容推导的配色。
    /// </summary>
    public StripZone HoverZone
    {
        get => _hoverZone;
        set
        {
            if (_hoverZone == value)
            {
                return;
            }

            _hoverZone = value;
            InvalidateVisual();
        }
    }


    /// <summary>
    /// 撑满父级给的空间。
    /// 坑（v0.2.8 实拍发现）：FrameworkElement 的默认 MeasureOverride 返回 0×0，
    /// 放进 Border（VerticalAlignment=Center、无显式高度）后被安排成 0 高，
    /// <see cref="OnRender"/> 开头的 `h &lt;= 2` 判断直接 return —— 结果是**指示器完全不可见**，
    /// 细条只剩一条灰底（渲染出来看不出任何状态）。跑马灯控件当年踩的是同一个坑。
    /// </summary>
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2)
        {
            return;
        }

        var radius = h / 2;
        var trackRect = new Rect(0.5, 0.5, w - 1, h - 1);
        var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), 1);
        trackPen.Freeze();
        dc.DrawRoundedRectangle(null, trackPen, trackRect, radius, radius);

        // v0.2.18：光带一律**裁剪在轨道内**。无缝循环下头部/拖尾会探出轨道边缘
        // （进入从左侧、退出向右侧），不裁剪就会画到细条端头之外——
        // 用户报的"待机条动画溢出条的两端、观感不连贯"。
        // 用 try/finally 保证多条 return 分支下 Pop 也成对执行。
        dc.PushClip(new RectangleGeometry(trackRect, radius, radius));
        try
        {
            DrawTrackContent(dc, w, h);
        }
        finally
        {
            dc.Pop();
        }
    }

    /// <summary>轨道内的光带绘制（裁剪由 <see cref="OnRender"/> 统一负责）。</summary>
    private void DrawTrackContent(DrawingContext dc, double w, double h)
    {
        // 光块的内边距：留出 2px 让光块在轨道里"悬浮"
        var inset = 2.5;
        var trackLeft = inset;
        var trackWidth = Math.Max(4, w - (inset * 2));
        var segH = Math.Max(2, h - (inset * 2));
        var segY = inset;

        // 完成态：不管之前是音乐还是任务，一律"停在中间的一段绿光"（静止，呼吸交给外层整条透明度）。
        // 之前这里会落到 Task 分支去画彗星，而 _phase 在完成态被冻结在 0 → 绿块永远卡在左端，
        // 看着像"还有任务在跑"，和"完成"的语义相反。
        if (_done)
        {
            var doneWidth = Math.Min(trackWidth, 26);
            DrawSegment(dc, trackLeft + ((trackWidth - doneWidth) / 2), segY,
                doneWidth, segH, DoneColor, 0.95);
            return;
        }

        // 悬停分区配色优先于内容配色（需求 2）：让"这一半是音乐 / 那一半是 WorkBuddy"可预判。
        // 左段用音乐黄来回摆（像电平），右段用琥珀单向巡游（像任务进度），配色与动感一起换。
        if (_hoverZone == StripZone.Left)
        {
            DrawOscillating(dc, trackLeft, trackWidth, segY, segH, MusicColor, speed: 0.72);
            return;
        }

        if (_hoverZone == StripZone.Right)
        {
            DrawComet(dc, trackLeft, trackWidth, segY, segH, TaskColor, speed: 0.6);
            return;
        }

        switch (_mode)
        {
            case StripMode.Music:
                DrawOscillating(dc, trackLeft, trackWidth, segY, segH, MusicColor, speed: 0.55);
                break;

            case StripMode.Task:
                DrawComet(dc, trackLeft, trackWidth, segY, segH, TaskColor, speed: 0.42);
                break;

            case StripMode.Both:
                // 琥珀单向巡游（任务）+ 黄色反向来回（音乐），两条轨道错开半格
                var taskH = Math.Max(2, (segH - 1.5) / 2);
                DrawComet(dc, trackLeft, trackWidth, segY, taskH, TaskColor, speed: 0.42);
                DrawOscillating(dc, trackLeft, trackWidth, segY + taskH + 1.5, taskH,
                    MusicColor, speed: 0.62, reverse: true);
                break;

            case StripMode.Notice:
                // 通知：来源色单向巡游，节奏比任务略快（"有事发生"的观感）
                DrawComet(dc, trackLeft, trackWidth, segY, segH, _accent ?? MusicColor, speed: 0.58);
                break;

            default:
                // 待命（v0.2.9 需求 2：岛常驻，无内容时细条也一直在）：
                // 一段很淡的居中光块，靠外层呼吸提醒"程序在跑"，不喧宾夺主。
                // 用中性白而不是音乐黄：黄色半透明压在深底上会变成脏橄榄色，看着像"发霉"；
                // 中性色也避免用户误以为"有音乐在放"。
                var idleWidth = Math.Min(trackWidth, 22);
                DrawSegment(dc, trackLeft + ((trackWidth - idleWidth) / 2), segY,
                    idleWidth, segH, Colors.White, 0.26);
                break;
        }
    }

    /// <summary>来回游动（正弦缓动），用于"音乐"。</summary>
    private void DrawOscillating(DrawingContext dc, double left, double width,
        double y, double height, Color color, double speed, bool reverse = false)
    {
        var segWidth = Math.Min(width, Math.Max(14, width * 0.32));
        var travel = Math.Max(0, width - segWidth);

        // sin 在 [-1,1]，映射到 [0,1]；周期由 speed 控制（cycles/秒）
        var t = (Math.Sin((_phase * speed * Math.PI * 2) + (reverse ? Math.PI : 0)) + 1) / 2;

        DrawSegment(dc, left + (travel * t), y, segWidth, height, color, 0.95);
    }

    /// <summary>
    /// 单向巡游 + 拖尾，用于"任务在推进"。
    ///
    /// v0.2.18 需求3（无缝循环）：旧实现用 <c>(phase * speed) % 1.0</c> 直接映射到
    /// [0, 轨道宽-光块宽]，相位归零时光块从右端**瞬移**回左端——音乐+任务双形态时
    /// 两条轨道一起跳，用户看到的就是"末尾切回初始帧落差大、卡顿割裂"。
    ///
    /// 现在改成跑马灯式的无极循环：光块头部从"完全在轨道左侧外"走到"完全在轨道右侧外"，
    /// 一个周期刚好够它整体进出一次：
    ///   · 首帧（t=0）与尾帧（t=1）画面完全一致——都只剩空轨道（光块整体在屏外）；
    ///   · 运动全程连续：头部从左侧边缘滑入、横穿、从右侧边缘滑出，没有任何跳变；
    ///   · 周期 = 轨道宽 + 光带总跨度，保证"出得去"才"回得来"，不会出现
    ///     "右端还剩半截、左端已经冒头"的撕裂帧。
    /// </summary>
    private void DrawComet(DrawingContext dc, double left, double width,
        double y, double height, Color color, double speed)
    {
        var segWidth = Math.Min(width, Math.Max(12, width * 0.26));

        // 光带总跨度（头 + 拖尾 + 头部光晕），与下面的绘制保持一致
        var extent = CometExtent(segWidth);

        // 一个周期内头部从轨道左侧外走到右侧外（t: 0→1）
        var x = CometHeadX((_phase * speed) % 1.0, left, width, segWidth);

        // 拖尾：往回叠 3 段，逐渐变淡变窄，形成"彗星"
        for (var i = 3; i >= 1; i--)
        {
            var back = x - (i * segWidth * 0.45);
            if (back < left)
            {
                continue;
            }

            var alpha = 0.30 - (i * 0.08);
            DrawSegment(dc, back, y, segWidth * (1 - (i * 0.12)), height, color, alpha);
        }

        DrawSegment(dc, x, y, segWidth, height, color, 0.95);

        // 头部光晕
        DrawSegment(dc, x - 1, y - 0.5, segWidth + 2, height + 1, color, 0.18);
    }

    /// <summary>光带总跨度（头 + 拖尾 + 光晕，DIP）：循环周期必须覆盖它才能无缝。</summary>
    internal const double CometTailFactor = 1.35;

    /// <summary>光带总跨度（DIP）。</summary>
    internal static double CometExtent(double segWidth) => (segWidth * CometTailFactor) + segWidth + 1;

    /// <summary>
    /// 周期相位 <paramref name="t"/>（0→1）对应的光块头部 X 坐标（纯函数，自检用）。
    /// t=0 时头部恰好在轨道左侧外（整条光带不可见），t→1 时走到右侧外（同样不可见），
    /// 因此首帧与尾帧画面一致；中间过程连续滑入/滑出，无跳变。
    /// </summary>
    internal static double CometHeadX(double t, double left, double width, double segWidth)
    {
        var start = left - (segWidth + 1);              // 头部起点：光晕右缘刚好贴上轨道左缘
        var travel = width + CometExtent(segWidth);     // 一个周期走完"进→穿→出"
        return start + (travel * (t - Math.Floor(t)));
    }

    private static void DrawSegment(DrawingContext dc, double x, double y,
        double width, double height, Color color, double alpha)
    {
        if (width <= 0.5 || height <= 0.5)
        {
            return;
        }

        var brush = new SolidColorBrush(color) { Opacity = Math.Clamp(alpha, 0, 1) };
        brush.Freeze();

        var radius = height / 2;
        dc.DrawRoundedRectangle(brush, null, new Rect(x, y, width, height), radius, radius);
    }
}
