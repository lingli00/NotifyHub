using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NotifyHub.UI;

/// <summary>跑马灯的短文本锚点：Center=水平居中（歌名等）｜Left=左对齐（歌词/状态行）。</summary>
public enum TextAnchor
{
    Center,
    Left,
}

/// <summary>
/// 跑马灯文本（需求 1 + 需求 2）：
///   - 文本宽度 ≤ 可视区：不滚动，水平居中显示（禁止左对齐）；
///   - 文本宽度 &gt; 可视区：自动横向无缝循环滚动，鼠标悬停暂停、移开继续；
///   - 滚动速度 <see cref="Speed"/>（DIP/秒）可在设置页调整，帧率无关（按真实耗时推进）；
///   - 可视区（Clip 区）中心即文字块的视觉中心锚点：文本初始居中，
///     溢出部分从两侧对称长出、向左滚动，循环点与原起点严格重合（无缝）。
///
/// 实现要点：无模板自绘控件（Control + OnRender）——文字直接用 DrawingContext.DrawText
/// 画，长文本画两份副本做无缝循环。不搭子元素树，规避一切布局/继承/测量层面的不确定性
/// （早期版本用 UserControl+Canvas+TextBlock，实测 Canvas 的 DesiredSize 会诡异地归零，
/// 导致整个控件高度为 0、文字完全不可见，故改为自绘）。
/// </summary>
public sealed class MarqueeText : Control
{
    /// <summary>默认滚动速度（DIP/秒），“平缓”。</summary>
    public const double DefaultSpeed = 45;

    /// <summary>两份文本之间的间隙（DIP）。</summary>
    private const double TextGap = 28;

    private double _textWidth;
    private double _offset;
    private bool _scrolling;
    private bool _hovering;
    private DateTime _lastTick = DateTime.MinValue;

    static MarqueeText()
    {
        // 无模板：样式查找指向自身（不提供模板资源 → 不渲染模板子树），
        // 视觉完全由 OnRender 自绘产生。
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MarqueeText),
            new FrameworkPropertyMetadata(typeof(MarqueeText)));

        // Control.Foreground 的元数据默认值是黑色——画在深色灵动岛/通知卡上等于隐形，
        // 是个反复踩过的坑（历史：控件不显式设色时文字完全看不见）。
        // 覆写默认值为岛内文字同款浅色；选项显式声明：
        //   Inherits      —— 父级设色仍能正常继承；
        //   AffectsRender —— 运行期改色会触发重绘（自绘控件必需）。
        // 调用方显式设色时行为不变。
        var defaultBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7));
        defaultBrush.Freeze();
        ForegroundProperty.OverrideMetadata(
            typeof(MarqueeText),
            new FrameworkPropertyMetadata(
                defaultBrush,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public MarqueeText()
    {
        // 自身即可视区：裁剪 + 透明背景；命中测试整块矩形（否则鼠标进不来）
        ClipToBounds = true;
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = false;

        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;

        MouseEnter += (_, _) => _hovering = true;
        MouseLeave += (_, _) => _hovering = false;
    }

    /// <summary>显示的文本。</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeText),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (MarqueeText)d;
        self.InvalidateMeasure();
        self.InvalidateVisual();
        self._offset = 0;
        self._lastTick = DateTime.MinValue;
    }

    /// <summary>滚动速度（DIP/秒）。</summary>
    public static readonly DependencyProperty SpeedProperty =
        DependencyProperty.Register(nameof(Speed), typeof(double), typeof(MarqueeText),
            new PropertyMetadata(DefaultSpeed));

    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    /// <summary>
    /// 短文本的锚点（v0.2.7）：默认居中（歌名）；歌词行与任务状态行用 Left，
    /// 两行左对齐才像"歌词/日志"，居中会显得散。
    /// 长文本滚动时同样以它为滚动起点。
    /// </summary>
    public TextAnchor Anchor
    {
        get => _anchor;
        set
        {
            if (_anchor == value)
            {
                return;
            }

            _anchor = value;
            _scrolling = false;
            _offset = 0;
            InvalidateVisual();
        }
    }

    private TextAnchor _anchor = TextAnchor.Center;

    /// <summary>
    /// 截屏/自检时置 true：暂停逐帧推进与 InvalidateVisual。
    /// 否则 RenderTargetBitmap.Render 抓拍会被渲染帧中间的重绘打断，抓到空帧。
    /// </summary>
    public bool SuspendRendering { get; set; }

    /// <summary>当前是否处于滚动状态（诊断用，实时计算）。</summary>
    public bool IsScrolling => _textWidth > 0 && _textWidth > ViewWidth;

    /// <summary>当前滚动位移（诊断/自检用，实时值）。</summary>
    public double Offset => _offset;

    /// <summary>诊断快照：文本/前景/尺寸/滚动态（自检与排障用）。</summary>
    public string DebugState =>
        $"text='{Text}' fg={(Foreground?.ToString() ?? "null")} " +
        $"size={ActualWidth:F0}x{ActualHeight:F0} textWidth={_textWidth:F1} " +
        $"offset={_offset:F1} scroll={IsScrolling}";

    /// <summary>可视区宽度：显式 Width 优先，否则取布局给出的 ActualWidth。</summary>
    private double ViewWidth => double.IsNaN(Width) ? ActualWidth : Width;

    protected override Size MeasureOverride(Size constraint)
    {
        var text = Text ?? string.Empty;
        if (text.Length == 0)
        {
            _textWidth = 0;
            _offset = 0;
            return new Size(0, 0);
        }

        var formatted = BuildFormattedText(text);
        _textWidth = formatted.Width;

        double width;
        if (!double.IsNaN(Width))
        {
            width = Width;
        }
        else if (!double.IsInfinity(constraint.Width))
        {
            width = Math.Min(_textWidth, constraint.Width);
        }
        else
        {
            width = _textWidth;
        }

        return new Size(width, formatted.Height + 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = Text ?? string.Empty;
        var view = ViewWidth;
        if (text.Length == 0 || view <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var formatted = BuildFormattedText(text);
        var y = Math.Max(0, (ActualHeight - formatted.Height) / 2);

        if (_textWidth <= view)
        {
            // 短文本：居中（歌名）或左对齐（歌词/状态行）
            _scrolling = false;
            _offset = 0;
            var x = _anchor == TextAnchor.Left ? 0 : Math.Max(0, (view - _textWidth) / 2);
            dc.DrawText(formatted, new Point(x, y));
            return;
        }

        // 长文本：无缝循环。起点 = 短文本同款锚点（居中时两侧对称溢出，左对齐时从左侧开始）
        if (!_scrolling)
        {
            _scrolling = true;
            _offset = _anchor == TextAnchor.Left ? 0 : (view - _textWidth) / 2;
        }

        dc.DrawText(formatted, new Point(_offset, y));
        dc.DrawText(formatted, new Point(_offset + _textWidth + TextGap, y));
    }

    /// <summary>整块矩形可命中：鼠标进入/离开才能驱动悬停暂停。</summary>
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    /// <summary>渲染帧回调：帧率无关推进，悬停/离屏时暂停推进（内容保持）。</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;

        // 截屏挂起：不推进、不重绘（保留最后一帧供抓拍）
        if (SuspendRendering || !_scrolling || !IsVisible || ViewWidth <= 0)
        {
            _lastTick = now;
            return;
        }

        if (_lastTick != DateTime.MinValue && !_hovering)
        {
            var dt = (now - _lastTick).TotalSeconds;
            if (dt > 0 && dt < 0.5)
            {
                var speed = Math.Clamp(Speed, 5, 400);
                _offset -= speed * dt;

                // 循环：位移越过“起点 + 一整份文本 + 间隙”后回到起点（副本严格接续，无缝）
                var span = _textWidth + TextGap;
                var start = _anchor == TextAnchor.Left ? 0 : (ViewWidth - _textWidth) / 2;
                if (_offset <= start - span)
                {
                    _offset += span;
                }
            }
        }

        _lastTick = now;
        InvalidateVisual();
    }

    private FormattedText BuildFormattedText(string text) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
        FontSize,
        Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
