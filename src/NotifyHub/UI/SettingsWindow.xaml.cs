using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NotifyHub.Interop;
using NotifyHub.Models;
using NotifyHub.Services;
using NotifyHub.Services.Uia;

namespace NotifyHub.UI;

/// <summary>
/// 设置窗口：ntfy 多订阅与下行推送、微信/QQ 开关、轮询间隔、通知时长与位置、灵动岛模式、
/// 两套提示音开关与音量、微信过滤、浏览器选择、日志目录自定义、运行日志、最近事件，
/// 以及一键清除日志。
/// 原独立的"控制台窗口"已取消，详细日志并入这里的「运行日志」栏。
///
/// v0.2.9 需求 6：
///   · 界面改为毛玻璃、自绘标题栏，配色跟随 Windows 应用主题并按背景亮度自动选深浅；
///   · 删除「主题 / 文字配色」与透明度调节（外观固定毛玻璃 + 自动对比度）；
///   · 新增「灵动岛总长度」「左右区域比例」两个滑块，拖动时岛实时重排。
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] CornerValues = { "TopRight", "TopLeft", "BottomRight", "BottomLeft" };

    /// <summary>ntfy 色板展示名（设置页下拉用）。</summary>
    private static readonly string[] NtfyColorNames = { "默认橙", "雾蓝", "青绿", "薰衣草紫", "玫粉" };

    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly MonitorController _monitor;
    private readonly NotificationManager _notifications;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>ntfy 订阅编辑中的列表（保存时写回配置）。</summary>
    private readonly List<NtfySubscription> _ntfyEditing = new();

    // 日志增量渲染锚点：ConsoleSnapshot 每次返回新 List，但元素是同一批实例，用引用比较判断
    private ConsoleLine? _lastLogLine;

    public SettingsWindow(ConfigService config, LogService log, MonitorController monitor, NotificationManager notifications)
    {
        InitializeComponent();

        _config = config;
        _log = log;
        _monitor = monitor;
        _notifications = notifications;

        // v0.2.16：本窗是「无系统标题栏 + AllowsTransparency」的分层窗口，
        // WPF 的最小化会把 HWND 压成 160×28 挪到屏幕左下角并继续绘制（用户报的白块）。
        // 统一拦成"收进托盘"，托盘双击 / --open-settings 再叫回来。
        WindowSquashGuard.Attach(
            this,
            note => _log.Info("设置", "最小化已拦截", note));

        // 浏览器下拉的变更事件必须在 InitializeComponent 之后挂：
        // XAML 加载期（ItemsControl.EndInit → SelectedIndex 强制生效）也会派发
        // SelectionChanged，那时后面声明的命名元素还没建出来，处理器会踩 NRE。
        BrowserChoiceCombo.SelectionChanged += OnBrowserChoiceChanged;

        // v0.2.7：两个滑块的 ValueChanged 同样延后挂载（加载期会强制把 Value 收敛到
        // Minimum，早挂的话后面的数值 TextBlock 还是 null）
        AutoCollapseSlider.ValueChanged += OnAutoCollapseSecondsChanged;
        LyricsFontSizeSlider.ValueChanged += OnLyricsFontSizeChanged;
        LyricOffsetSlider.ValueChanged += OnLyricOffsetChanged;

        // v0.2.9 需求 6：两个布局滑块（同样延后挂载，XAML 加载期会强制收敛 Value）
        IslandWidthSlider.ValueChanged += OnIslandWidthChanged;
        IslandSplitSlider.ValueChanged += OnIslandSplitChanged;

        LoadFromConfig();
        RefreshEvents();
        RefreshLogs();

        // v0.2.9 需求 6：毛玻璃外观（先按系统主题上一版，窗口上屏后再按真实背景校正一次）
        ApplyGlassChrome();
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(ApplyGlassChrome), DispatcherPriority.Background);

        _log.RecentChanged += OnRecentChanged;
        _log.ConsoleChanged += OnConsoleChanged;
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();

        Closed += (_, _) =>
        {
            _log.RecentChanged -= OnRecentChanged;
            _log.ConsoleChanged -= OnConsoleChanged;
            _statusTimer.Stop();
        };
    }

    // ---------------- v0.2.9 需求 6：毛玻璃外观 ----------------

    /// <summary>当前用的是浅色壳还是深色壳（诊断用）。</summary>
    public bool GlassIsLight { get; private set; }

    /// <summary>
    /// 应用毛玻璃外观：外壳渐变 + 前景色一起换。
    ///
    /// 深浅的判定顺序（需求 6「配色跟随系统，根据背景对比度自动适配」）：
    ///   1) 先跟随 Windows「应用使用浅色主题」；
    ///   2) 再实测窗口背后内容的平均亮度——系统是浅色、但背后是纯黑全屏（视频/深色 IDE）时，
    ///      高透明白玻璃会糊成一片，这时切成深色壳；反之亦然。
    /// 这样"跟随系统"不会在极端背景下伤害可读性。
    /// </summary>
    public void ApplyGlassChrome()
    {
        var useLight = ThemePalette.SystemPrefersLight();

        double? backdrop = null;
        try
        {
            if (IsLoaded && ActualWidth > 1)
            {
                var origin = PointToScreen(new Point(0, 0));
                var dpi = VisualTreeHelper.GetDpi(this);
                var rect = new Rect(
                    (origin.X / dpi.DpiScaleX) + 8,
                    (origin.Y / dpi.DpiScaleY) + 8,
                    Math.Max(1, ActualWidth - 16),
                    Math.Max(1, ActualHeight - 16));

                backdrop = BackdropProbe.AverageLuminance(rect);
            }
        }
        catch
        {
            // 取样失败就按系统主题
        }

        if (backdrop is { } luminance)
        {
            if (useLight && luminance < 0.22)
            {
                useLight = false;
            }
            else if (!useLight && luminance > 0.78)
            {
                useLight = true;
            }
        }

        GlassIsLight = useLight;

        Resources["AppFg"] = ThemePalette.Brush(
            useLight ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White);
        Resources["AppSubFg"] = ThemePalette.Brush(
            useLight ? Color.FromArgb(0xB4, 0x1C, 0x1C, 0x1E) : Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF));
        Resources["AppCard"] = ThemePalette.Brush(
            useLight ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        Resources["AppInput"] = ThemePalette.Brush(
            useLight ? Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        Resources["AppBorder"] = ThemePalette.Brush(
            useLight ? Color.FromArgb(0x2E, 0x00, 0x00, 0x00) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        Resources["AppHover"] = ThemePalette.Brush(
            useLight ? Color.FromArgb(0x1C, 0x00, 0x00, 0x00) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

        Shell.Background = useLight
            ? new LinearGradientBrush(
                Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0xE2, 0xEB, 0xED, 0xF1), 90)
            : new LinearGradientBrush(
                Color.FromArgb(0xF2, 0x2C, 0x2C, 0x32),
                Color.FromArgb(0xE0, 0x12, 0x12, 0x16), 90);
    }

    // ---------------- 自绘标题栏 ----------------

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 双击/快速拖动时 DragMove 可能抛 InvalidOperationException，忽略即可
        }
    }

    /// <summary>
    /// 自绘标题栏的最小化按钮。v0.2.16 起只是"请求最小化"——
    /// 真正的动作由 <see cref="WindowSquashGuard"/> 转成 Hide()，
    /// 避免分层窗被压成 160×28 的白块停在屏幕左下角。
    /// </summary>
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void LoadFromConfig()
    {
        var config = _config.Current;

        // ntfy 多订阅（v0.2.4）
        NtfyEnabledCheck.IsChecked = config.NtfyEnabled;
        _ntfyEditing.Clear();
        foreach (var sub in config.NtfySubscriptions)
        {
            _ntfyEditing.Add(sub.Clone());
        }

        if (_ntfyEditing.Count == 0)
        {
            // 旧配置单地址迁移兜底
            _ntfyEditing.Add(new NtfySubscription
            {
                Enabled = true,
                Name = string.Empty,
                Url = config.NtfyUrl,
                ColorIndex = 0,
            });
        }

        RebuildNtfyListUI();
        NtfyPushUrlBox.Text = config.NtfyPushUrl;

        WeChatCheck.IsChecked = config.WeChatEnabled;
        QQCheck.IsChecked = config.QQEnabled;
        IntervalBox.Text = config.PollIntervalMs.ToString();
        DurationBox.Text = config.NotificationDurationMs.ToString();
        AutoStartCheck.IsChecked = config.AutoStart;
        DumpOnStartCheck.IsChecked = config.DumpUiaOnStart;
        IslandFixedRadio.IsChecked = config.IslandMode != "Free";
        IslandFreeRadio.IsChecked = config.IslandMode == "Free";
        MusicEnabledCheck.IsChecked = config.MusicEnabled;
        WbWatchCheck.IsChecked = config.WorkBuddyWatchEnabled;

        MessageSoundCheck.IsChecked = config.MessageSoundEnabled;
        MessageVolumeSlider.Value = config.MessageSoundVolume;
        MessageVolumeText.Text = $"{config.MessageSoundVolume}%";
        SelectComboByTag(MessageSoundCombo, config.MessageSoundStyle);

        TaskSoundCheck.IsChecked = config.TaskSoundEnabled;
        TaskVolumeSlider.Value = config.TaskSoundVolume;
        TaskVolumeText.Text = $"{config.TaskSoundVolume}%";
        SelectComboByTag(TaskSoundCombo, config.TaskSoundStyle);

        SuppressAppSoundsCheck.IsChecked = config.SuppressAppSounds;

        WeChatFilterCheck.IsChecked = config.WeChatFilterEnabled;
        FilterKeywordsBox.Text = config.WeChatBlockedKeywords;

        // v0.2.6 外观：主题固定毛玻璃（v0.2.9 需求 6 起不再可配）
        LyricsEnabledCheck.IsChecked = config.LyricsEnabled;

        // v0.2.9 需求 6：岛总长度 / 左右区域比例
        var widthPercent = Math.Clamp(
            config.IslandWidthPercent, AppConfig.MinIslandWidthPercent, AppConfig.MaxIslandWidthPercent);
        IslandWidthSlider.Value = widthPercent;
        IslandWidthText.Text = $"{widthPercent}%";

        var splitRatio = Math.Clamp(
            config.IslandSplitRatio, AppConfig.MinIslandSplitRatio, AppConfig.MaxIslandSplitRatio);
        IslandSplitSlider.Value = splitRatio;
        IslandSplitText.Text = $"{splitRatio} : {100 - splitRatio}";

        // v0.2.7 需求 1：歌词字号
        var lyricSize = Math.Clamp(config.LyricsFontSize, AppConfig.MinLyricsFontSize, AppConfig.MaxLyricsFontSize);
        LyricsFontSizeSlider.Value = lyricSize;
        LyricsFontSizeText.Text = $"{lyricSize}";

        // v0.2.12 需求 1：歌词来源与时间微调
        LyricsNetworkCheck.IsChecked = config.LyricsNetworkFallback;
        var offset = Math.Clamp(config.LyricOffsetMs, AppConfig.MinLyricOffsetMs, AppConfig.MaxLyricOffsetMs);
        LyricOffsetSlider.Value = offset;
        LyricOffsetText.Text = $"{offset} ms";

        // v0.2.7 需求 2：超时自动收纳（开关 + 时长 + 音乐播放例外）
        AutoCollapseCheck.IsChecked = config.AutoCollapseEnabled;
        var collapseSeconds = Math.Clamp(
            config.AutoCollapseSeconds, AppConfig.MinAutoCollapseSeconds, AppConfig.MaxAutoCollapseSeconds);
        AutoCollapseSlider.Value = collapseSeconds;
        AutoCollapseText.Text = $"{collapseSeconds} 秒";
        KeepOpenWhilePlayingCheck.IsChecked = config.KeepOpenWhilePlaying;
        UpdateAutoCollapseEnabledState();

        MarqueeSpeedSlider.Value = Math.Clamp(config.MarqueeSpeed, 10, 120);
        MarqueeSpeedText.Text = $"{Math.Clamp(config.MarqueeSpeed, 10, 120)}";

        // 浏览器四选项（v0.2.4 需求 7）
        var choice = AppConfig.NormalizeBrowserChoice(config.BrowserChoice);
        SelectComboByTag(BrowserChoiceCombo, choice);
        BrowserPathBox.Text = config.BrowserPath;
        ApplyBrowserChoiceUI(choice);

        // 日志目录（v0.2.4 需求 8）
        LogDirText.Text = _log.LogDir;
        LogDirText.ToolTip = _log.LogDir;

        var cornerIndex = Array.IndexOf(CornerValues, AppConfig.NormalizeCorner(config.NotificationCorner));
        CornerCombo.SelectedIndex = cornerIndex < 0 ? 0 : cornerIndex;
    }

    // ---------------- ntfy 多订阅（v0.2.4 需求 1/2；v0.2.5 排版修复） ----------------

    /// <summary>把 <see cref="_ntfyEditing"/> 渲染成设置页里的订阅行。</summary>
    private void RebuildNtfyListUI()
    {
        NtfyListPanel.Children.Clear();

        if (_ntfyEditing.Count > 0)
        {
            NtfyListPanel.Children.Add(BuildNtfyHeaderRow());
        }

        for (var i = 0; i < _ntfyEditing.Count; i++)
        {
            NtfyListPanel.Children.Add(BuildNtfyRow(_ntfyEditing[i], i));
        }
    }

    /// <summary>订阅列表的列头行（与数据行列宽一致，避免用户猜哪列是什么）。</summary>
    private UIElement BuildNtfyHeaderRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NtfyRowCols.Enable) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NtfyRowCols.Name) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NtfyRowCols.Color) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NtfyRowCols.Remove) });

        foreach (var (text, col) in new[]
                 {
                     ("启用", 0), ("名称", 1), ("订阅地址", 2), ("通知颜色", 3), ("删除", 4),
                 })
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(col == 0 ? 2 : 4, 0, 4, 0),
            };
            Grid.SetColumn(label, col);
            grid.Children.Add(label);
        }

        return grid;
    }

    /// <summary>订阅行各列的宽度（DIP），列头行与数据行共用，保证上下对齐。</summary>
    private static class NtfyRowCols
    {
        public const double Enable = 34;
        public const double Name = 96;
        public const double Color = 108;
        public const double Remove = 34;
    }

    private UIElement BuildNtfyRow(NtfySubscription sub, int index)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NtfyRowCols.Enable) });           // 启用
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NtfyRowCols.Name) });             // 名称
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });         // 地址
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NtfyRowCols.Color) });            // 颜色
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NtfyRowCols.Remove) });           // 删除

        // 启用勾选框：显式覆盖全局 CheckBox 样式（Margin 右 18px）与列宽，
        // 否则 16px 的框 + 18px 右边距会超出列宽被裁成“半个勾选框”。
        var enabled = new CheckBox
        {
            IsChecked = sub.Enabled,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "启用/停用这个订阅",
        };
        Grid.SetColumn(enabled, 0);
        grid.Children.Add(enabled);

        var name = new TextBox
        {
            Text = sub.Name,
            Height = 26,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "显示在通知卡顶部的来源名（如“家庭”），留空只显示 ntfy",
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var url = new TextBox
        {
            Text = sub.Url,
            Height = 26,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "https://ntfy.sh/你的主题，程序自动补 /sse",
        };
        Grid.SetColumn(url, 2);
        grid.Children.Add(url);

        var color = new ComboBox
        {
            Height = 26,
            Width = 96,
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "该订阅通知弹窗的强调色（低饱和度，与微信/QQ/ntfy 同风格）",
        };
        for (var c = 0; c < NtfyColorNames.Length; c++)
        {
            color.Items.Add(new ComboBoxItem { Content = NtfyColorNames[c], Tag = c });
        }

        color.SelectedIndex = Math.Clamp(sub.ColorIndex, 0, NtfyColorNames.Length - 1);
        Grid.SetColumn(color, 3);
        grid.Children.Add(color);

        // 删除按钮：必须显式把 MinWidth/Padding/Margin 压回 0——
        // 全局 Button 样式给了 MinWidth=88，Width=26 会被 MinWidth 压回去，
        // 按钮实际 88px 宽、内容居中后跑到面板外被裁掉，看起来就是“颜色右边多出一块灰”。
        var remove = new Button
        {
            Content = "×",
            Width = 26,
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "删除这个订阅",
            Tag = grid,
        };
        remove.Click += (_, _) =>
        {
            NtfyListPanel.Children.Remove(grid);
            _ntfyEditing.Remove(sub);
        };
        Grid.SetColumn(remove, 4);
        grid.Children.Add(remove);

        // 保存时从控件读回最新值
        grid.Tag = new NtfyRowBind(sub, enabled, name, url, color);
        return grid;
    }

    private sealed record NtfyRowBind(NtfySubscription Sub, CheckBox Enabled, TextBox Name, TextBox Url, ComboBox Color);

    /// <summary>从 UI 行读回编辑结果（丢弃空地址行）。</summary>
    private List<NtfySubscription> CollectNtfyRows()
    {
        var result = new List<NtfySubscription>();

        foreach (var child in NtfyListPanel.Children)
        {
            if (child is not Grid { Tag: NtfyRowBind bind })
            {
                continue;
            }

            var url = bind.Url.Text.Trim();
            if (url.Length == 0)
            {
                continue; // 空地址行直接丢弃
            }

            result.Add(new NtfySubscription
            {
                Enabled = bind.Enabled.IsChecked == true,
                Name = bind.Name.Text.Trim(),
                Url = url,
                ColorIndex = bind.Color.SelectedItem is ComboBoxItem { Tag: int c } ? c : 0,
            });
        }

        return result;
    }

    private void OnNtfyAddClick(object sender, RoutedEventArgs e)
    {
        var sub = new NtfySubscription
        {
            Enabled = true,
            Name = string.Empty,
            Url = "https://ntfy.sh/",
            ColorIndex = _ntfyEditing.Count % NtfyColorNames.Length,
        };
        _ntfyEditing.Add(sub);
        NtfyListPanel.Children.Add(BuildNtfyRow(sub, _ntfyEditing.Count - 1));
    }

    // ---------------- 浏览器四选项（v0.2.4 需求 7） ----------------

    private void OnBrowserChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        var choice = ComboTag(BrowserChoiceCombo, "System");
        ApplyBrowserChoiceUI(AppConfig.NormalizeBrowserChoice(choice));
    }

    private void ApplyBrowserChoiceUI(string choice)
    {
        // 防御：XAML 加载期可能提前派发（命名元素尚未建全），空元素直接跳过
        if (BrowserPathRow is null || BrowserPathLabel is null || BrowserPathHint is null)
        {
            return;
        }

        var other = choice == "Other";

        BrowserPathRow.Visibility = other ? Visibility.Visible : Visibility.Collapsed;
        BrowserPathLabel.Visibility = other ? Visibility.Visible : Visibility.Collapsed;

        if (!other)
        {
            var located = choice == "System" ? null : BrowserLauncher.Locate(choice);
            BrowserPathHint.Text = choice switch
            {
                "Chrome" => located is null
                    ? "未在常见位置找到 Chrome，将回退系统默认浏览器"
                    : "已定位：" + located,
                "Edge" => located is null
                    ? "未在常见位置找到 Edge，将回退系统默认浏览器"
                    : "已定位：" + located,
                "Quark" => located is null
                    ? "未在常见位置找到夸克，将回退系统默认浏览器"
                    : "已定位：" + located,
                _ => "点击 ntfy 通知时用系统默认浏览器打开主题网页",
            };
        }
        else
        {
            BrowserPathHint.Text = "把桌面浏览器快捷方式拖入下方输入框，或粘贴 .exe / .lnk 路径";
        }
    }

    private void OnBrowserBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择浏览器程序或快捷方式",
            Filter = "程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };

        if (dialog.ShowDialog(this) == true)
        {
            BrowserPathBox.Text = dialog.FileName;
        }
    }

    private void OnBrowserPathDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnBrowserPathDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            BrowserPathBox.Text = files[0];
        }
    }

    // ---------------- 日志目录自定义（v0.2.4 需求 8） ----------------

    private void OnLogDirEditClick(object sender, RoutedEventArgs e)
    {
        var picked = Interop.FolderPicker.Pick(_log.LogDir, "选择日志保存目录");
        if (picked is null)
        {
            return;
        }

        if (_log.SetLogDir(picked))
        {
            // 立即写进配置，重启后仍生效
            var next = _config.Current.Clone();
            next.LogDir = picked;
            if (_config.Save(next))
            {
                _monitor.ApplyConfig();
            }

            LogDirText.Text = _log.LogDir;
            LogDirText.ToolTip = _log.LogDir;
            RefreshLogs();
            StatusText.Text = $"日志目录已切换到：{picked}";
        }
        else
        {
            MessageBox.Show(this, $"无法使用该目录：{picked}\n\n请选择有读写权限的位置。", "切换失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnMarqueeSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MarqueeSpeedText is not null)
        {
            MarqueeSpeedText.Text = $"{(int)Math.Round(e.NewValue)}";
        }
    }

    /// <summary>v0.2.7 需求 2：超时收纳时长滑动时同步数字（1~60 秒）。</summary>
    private void OnAutoCollapseSecondsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AutoCollapseText is not null)
        {
            AutoCollapseText.Text = $"{(int)Math.Round(e.NewValue)} 秒";
        }
    }

    /// <summary>v0.2.7 需求 1：歌词字号滑动时同步数字（10~22）。</summary>
    private void OnLyricOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LyricOffsetText is not null)
        {
            LyricOffsetText.Text = $"{(int)Math.Round(e.NewValue)} ms";
        }
    }

    private void OnLyricsFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LyricsFontSizeText is not null)
        {
            LyricsFontSizeText.Text = $"{(int)Math.Round(e.NewValue)}";
        }

        PushLivePreview();
    }

    // ---------------- v0.2.9 需求 6：两个布局滑块的实时预览 ----------------

    /// <summary>
    /// 实时预览回调（由 App 注入 → IslandController.PreviewLayout）。
    /// 拖动过程中只把"预览配置"交给岛，不写文件；点保存才真正落盘。
    /// </summary>
    public Action<AppConfig>? LivePreview { get; set; }

    private void OnIslandWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IslandWidthText is not null)
        {
            IslandWidthText.Text = $"{(int)Math.Round(e.NewValue)}%";
        }

        PushLivePreview();
    }

    private void OnIslandSplitChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IslandSplitText is not null)
        {
            var value = (int)Math.Round(e.NewValue);
            IslandSplitText.Text = $"{value} : {100 - value}";
        }

        PushLivePreview();
    }

    /// <summary>把当前界面上的布局相关取值组成预览配置，交给岛立即重排。</summary>
    private void PushLivePreview()
    {
        try
        {
            if (IslandWidthSlider is null || IslandSplitSlider is null)
            {
                return;
            }

            var preview = _config.Current.Clone();
            preview.IslandWidthPercent = (int)Math.Round(IslandWidthSlider.Value);
            preview.IslandSplitRatio = (int)Math.Round(IslandSplitSlider.Value);

            // 滑块旁边的歌词字号也一起预览，免得用户以为"字号改了没反应"
            if (LyricsFontSizeSlider is not null)
            {
                preview.LyricsFontSize = (int)Math.Round(LyricsFontSizeSlider.Value);
            }

            if (LyricsEnabledCheck is not null)
            {
                preview.LyricsEnabled = LyricsEnabledCheck.IsChecked == true;
            }

            LivePreview?.Invoke(preview);
        }
        catch
        {
            // 预览失败不影响保存流程
        }
    }

    /// <summary>总开关关闭时，把时长滑块与音乐例外一起置灰（避免"调了不生效"的困惑）。</summary>
    private void OnAutoCollapseToggled(object sender, RoutedEventArgs e) => UpdateAutoCollapseEnabledState();

    private void UpdateAutoCollapseEnabledState()
    {
        if (AutoCollapseSlider is null)
        {
            return;
        }

        var enabled = AutoCollapseCheck.IsChecked == true;
        AutoCollapseSlider.IsEnabled = enabled;
        KeepOpenWhilePlayingCheck.IsEnabled = enabled;
        AutoCollapseText.Opacity = enabled ? 1 : 0.45;
    }

    private void OnMessageVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MessageVolumeText is not null)
        {
            MessageVolumeText.Text = $"{(int)Math.Round(e.NewValue)}%";
        }
    }

    private void OnTaskVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TaskVolumeText is not null)
        {
            TaskVolumeText.Text = $"{(int)Math.Round(e.NewValue)}%";
        }
    }

    // ---------------- 提示音音色 / 试听 ----------------

    /// <summary>按 Tag 选中 ComboBox 项（找不到选第 0 项）。</summary>
    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem { Tag: string t } && string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string ComboTag(ComboBox combo, string fallback)
        => combo.SelectedItem is ComboBoxItem { Tag: string t } ? t : fallback;

    private void OnMessageSoundTestClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var sound = new SoundService(_log);
            sound.Preview(
                SoundService.MessageTone(ComboTag(MessageSoundCombo, "DingDong")),
                (int)Math.Round(MessageVolumeSlider.Value));
        }
        catch
        {
            // 试听失败不影响设置
        }
    }

    private void OnTaskSoundTestClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var sound = new SoundService(_log);
            sound.Preview(
                SoundService.TaskTone(ComboTag(TaskSoundCombo, "Ascend")),
                (int)Math.Round(TaskVolumeSlider.Value));
        }
        catch
        {
            // 试听失败不影响设置
        }
    }

    // v0.2.9 需求 6：主题/文字配色选项已删除，预览按钮一起去掉（外观固定毛玻璃 + 自动对比度）。

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalBox.Text.Trim(), out var interval)
            || interval < AppConfig.MinPollIntervalMs
            || interval > AppConfig.MaxPollIntervalMs)
        {
            MessageBox.Show(
                this,
                $"轮询间隔必须是 {AppConfig.MinPollIntervalMs} ~ {AppConfig.MaxPollIntervalMs} 之间的整数（毫秒）。",
                "参数不合法",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            IntervalBox.Focus();
            return;
        }

        if (!int.TryParse(DurationBox.Text.Trim(), out var duration)
            || duration < AppConfig.MinDurationMs
            || duration > AppConfig.MaxDurationMs)
        {
            MessageBox.Show(
                this,
                $"通知时长必须是 {AppConfig.MinDurationMs} ~ {AppConfig.MaxDurationMs} 之间的整数（毫秒）。",
                "参数不合法",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            DurationBox.Focus();
            return;
        }

        var next = _config.Current.Clone();
        next.NtfyEnabled = NtfyEnabledCheck.IsChecked == true;
        next.NtfySubscriptions = CollectNtfyRows();
        next.NtfyPushUrl = NtfyPushUrlBox.Text.Trim();
        next.WeChatEnabled = WeChatCheck.IsChecked == true;
        next.QQEnabled = QQCheck.IsChecked == true;
        next.PollIntervalMs = interval;
        next.NotificationDurationMs = duration;
        next.AutoStart = AutoStartCheck.IsChecked == true;
        next.DumpUiaOnStart = DumpOnStartCheck.IsChecked == true;
        next.IslandMode = IslandFreeRadio.IsChecked == true ? "Free" : "Fixed";
        next.MusicEnabled = MusicEnabledCheck.IsChecked == true;
        next.WorkBuddyWatchEnabled = WbWatchCheck.IsChecked == true;
        next.NotificationCorner = CornerValues[Math.Clamp(CornerCombo.SelectedIndex, 0, CornerValues.Length - 1)];

        next.MessageSoundEnabled = MessageSoundCheck.IsChecked == true;
        next.MessageSoundVolume = (int)Math.Round(MessageVolumeSlider.Value);
        next.MessageSoundStyle = ComboTag(MessageSoundCombo, "DingDong");
        next.TaskSoundEnabled = TaskSoundCheck.IsChecked == true;
        next.TaskSoundVolume = (int)Math.Round(TaskVolumeSlider.Value);
        next.TaskSoundStyle = ComboTag(TaskSoundCombo, "Ascend");
        next.SuppressAppSounds = SuppressAppSoundsCheck.IsChecked == true;

        next.WeChatFilterEnabled = WeChatFilterCheck.IsChecked == true;
        next.WeChatBlockedKeywords = FilterKeywordsBox.Text.Trim();

        next.MarqueeSpeed = (int)Math.Round(Math.Clamp(MarqueeSpeedSlider.Value, 10, 120));

        // v0.2.9 需求 6：外观固定毛玻璃（跟随系统 + 自动对比度），不再保存主题/配色；
        // 新增两个布局滑块（总长度 / 左右比例）。
        next.ThemeMode = "GlassAuto";
        next.TextTone = "Auto";
        next.IslandWidthPercent = (int)Math.Round(Math.Clamp(
            IslandWidthSlider.Value, AppConfig.MinIslandWidthPercent, AppConfig.MaxIslandWidthPercent));
        next.IslandSplitRatio = (int)Math.Round(Math.Clamp(
            IslandSplitSlider.Value, AppConfig.MinIslandSplitRatio, AppConfig.MaxIslandSplitRatio));

        next.LyricsEnabled = LyricsEnabledCheck.IsChecked == true;
        next.LyricsFontSize = (int)Math.Round(Math.Clamp(
            LyricsFontSizeSlider.Value, AppConfig.MinLyricsFontSize, AppConfig.MaxLyricsFontSize));
        next.LyricsNetworkFallback = LyricsNetworkCheck.IsChecked == true;
        next.LyricOffsetMs = (int)Math.Round(Math.Clamp(
            LyricOffsetSlider.Value, AppConfig.MinLyricOffsetMs, AppConfig.MaxLyricOffsetMs));

        // v0.2.7 需求 2：统一收纳（开关 + 时长 + 音乐播放例外）
        next.AutoCollapseEnabled = AutoCollapseCheck.IsChecked == true;
        next.AutoCollapseSeconds = (int)Math.Round(Math.Clamp(
            AutoCollapseSlider.Value, AppConfig.MinAutoCollapseSeconds, AppConfig.MaxAutoCollapseSeconds));
        next.KeepOpenWhilePlaying = KeepOpenWhilePlayingCheck.IsChecked == true;

        // 浏览器四选项（v0.2.4 需求 7）：前三项自动定位，Other 用用户提供的路径
        var browserChoice = AppConfig.NormalizeBrowserChoice(ComboTag(BrowserChoiceCombo, "System"));
        next.BrowserChoice = browserChoice;
        if (browserChoice == "Other")
        {
            var resolved = BrowserLauncher.ResolveShortcut(BrowserPathBox.Text);
            next.BrowserPath = resolved;

            if (resolved.Length == 0 || (!File.Exists(resolved) && !resolved.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this,
                    "“其他”浏览器需要提供有效的程序路径：\n请把桌面快捷方式拖入输入框，或粘贴 .exe / .lnk 路径。",
                    "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (browserChoice == "System")
        {
            next.BrowserPath = string.Empty;
        }
        else
        {
            var located = BrowserLauncher.Locate(browserChoice);
            if (located is null)
            {
                if (MessageBox.Show(this,
                        $"未在常见安装位置找到所选浏览器，是否仍保存该选择？\n\n点击 ntfy 通知时会回退用系统默认浏览器打开。",
                        "未找到浏览器", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    return;
                }

                next.BrowserPath = string.Empty;
            }
            else
            {
                next.BrowserPath = located;
            }
        }

        if (!_config.Save(next))
        {
            MessageBox.Show(this, "配置保存失败，请查看日志目录下的日志文件。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        StartupService.SetEnabled(next.AutoStart, _log);
        _monitor.ApplyConfig();
        _log.Success("Settings", "设置已保存并应用");

        _notifications.Relayout();
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _notifications.Show(new NotifyMessage
        {
            Source = MessageSource.WeChat,
            Sender = "张三",
            Content = "这是一条来自微信的测试通知，点击可尝试跳转到对应聊天。",
            Time = now,
        });

        _notifications.Show(new NotifyMessage
        {
            Source = MessageSource.QQ,
            Sender = "产品讨论组",
            Content = "[图片] 版本评审材料已经发到群里了，麻烦看一下。",
            Time = now,
        });

        _notifications.Show(new NotifyMessage
        {
            Source = MessageSource.Ntfy,
            Sender = "ntfy 测试",
            Content = "ntfy 通道连通性测试消息。",
            Time = now,
        });

        _log.Info("Settings", "已发送 3 条测试通知");
    }

    private async void OnDumpClick(object sender, RoutedEventArgs e)
    {
        DumpButton.IsEnabled = false;
        StatusText.Text = "正在导出 UIA 树…（微信 / QQ 需先打开）";

        try
        {
            var result = await Task.Run(() => UiaDumper.DumpAll(_log));
            UpdateStatus();
            StatusText.Text = $"UIA 树已导出（{result.NodeCount} 个节点）：{result.FilePath}";

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.FilePath}\"") { UseShellExecute = true });
            }
            catch
            {
                // 打不开资源管理器也不影响导出结果
            }
        }
        catch (Exception ex)
        {
            _log.Error("Settings", "导出 UIA 树失败", ex.Message);
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DumpButton.IsEnabled = true;
        }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _log.LogDir;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("Settings", "打开日志目录失败", ex.Message);
        }
    }

    /// <summary>
    /// 导出 Bug 快照（v0.2.10 需求 6）：截整屏 + 抓前后各 10 秒五个核心模块日志 + 当前配置，
    /// 落到 `snapshots\&lt;时间&gt;\`，然后直接打开查看页（左边截图、右边日志对照）。
    ///
    /// 只在这里调用一次 <see cref="BugSnapshotService.Capture"/>：不常驻、不轮询，
    /// 后段 10 秒的补录是那次调用里起的一次性任务，跑完就结束。
    /// </summary>
    private void OnExportSnapshotClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var info = BugSnapshotService.Capture($"由设置页导出（{DateTime.Now:HH:mm:ss}）");

            SnapshotStatus.Text = info.LineCount > 0
                ? $"已导出：{info.Id}（前段 {info.LineCount} 行，后段 10 秒补录中）"
                : $"已导出：{info.Id}（该时段没有核心模块日志）";

            new SnapshotWindow(info.Id) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            SnapshotStatus.Text = "导出失败：" + ex.Message;
            LogService.Shared.Error("Snapshot", "导出 Bug 快照失败", ex.ToString());
        }
    }

    /// <summary>打开快照查看页（默认最新一条）。</summary>
    private void OnOpenSnapshotClick(object sender, RoutedEventArgs e)
    {
        var latest = BugSnapshotService.List().FirstOrDefault();
        if (latest is null)
        {
            SnapshotStatus.Text = "还没有快照，先点「导出 Bug 快照」。";
            return;
        }

        new SnapshotWindow(latest.Id) { Owner = this }.Show();
    }

    /// <summary>一键清除全部日志（二次确认，只清日志，不删配置与去重记录）。</summary>
    private void OnClearLogsClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmClearLogs())
        {
            return;
        }

        var (deleted, success) = _log.ClearAll();
        _lastLogLine = null;
        LogList.Items.Clear();
        RefreshEvents();
        RefreshLogs();
        StatusText.Text = success
            ? $"日志已清空（删除 {deleted} 个文件）；配置与去重记录已保留。"
            : $"日志已清空，但有部分文件删除失败（可能被占用）。";
    }

    private void OnRecentChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshEvents));
            return;
        }

        RefreshEvents();
    }

    // ---------------- 运行日志（原控制台已合并到这里） ----------------

    private void OnConsoleChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshLogs));
            return;
        }

        RefreshLogs();
    }

    /// <summary>增量追加日志：只补新行，不重建整表（日志可能每秒十几条）。</summary>
    private void RefreshLogs()
    {
        var lines = _log.ConsoleSnapshot();
        var start = 0;

        if (_lastLogLine is not null)
        {
            start = -1;

            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(lines[i], _lastLogLine))
                {
                    start = i + 1;
                    break;
                }
            }

            // 找不到锚点（日志被清空或环形缓冲滚过去了）→ 全量重建
            if (start < 0)
            {
                start = 0;
                LogList.Items.Clear();
            }
        }

        for (var i = start; i < lines.Count; i++)
        {
            AppendLogLine(lines[i]);
        }

        while (LogList.Items.Count > LogService.MaxConsoleLines)
        {
            LogList.Items.RemoveAt(0);
        }

        if (lines.Count > 0)
        {
            _lastLogLine = lines[^1];
        }

        LogStatusText.Text = $"最近 {lines.Count} 条 · 文件：{_log.LogDir}";

        if (AutoScrollCheck.IsChecked == true && LogList.Items.Count > 0)
        {
            // 只滚日志框自己的 ScrollViewer：
            // 用 LogList.ScrollIntoView 会连带把外层整页滚到底，用户一打开设置页就看不到顶部了
            FindScrollViewer(LogList)?.ScrollToEnd();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void AppendLogLine(ConsoleLine line)
    {
        var tag = line.Level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Success => "SUC",
            LogLevel.Warn => "WRN",
            LogLevel.Error => "ERR",
            _ => "???",
        };

        var text = new TextBlock
        {
            Text = $"{line.Time:HH:mm:ss}  {tag}  {line.Text}",
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize = 12,
            LineHeight = 19,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = LogBrush(line.Level),
        };

        LogList.Items.Add(new ListBoxItem
        {
            Content = text,
            Padding = new Thickness(9, 1, 9, 1),
            Background = Brushes.Transparent,
            Focusable = false,
        });
    }

    private static Brush LogBrush(LogLevel level) => level switch
    {
        LogLevel.Success => new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),
        LogLevel.Warn => new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)),
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
        LogLevel.Debug => new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x85)),
        _ => new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xD1)),
    };

    /// <summary>日志栏里的"清空日志"（同样二次确认）。</summary>
    private void OnClearLogInlineClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmClearLogs())
        {
            return;
        }

        var (deleted, success) = _log.ClearAll();
        _lastLogLine = null;
        LogList.Items.Clear();
        RefreshEvents();
        RefreshLogs();

        StatusText.Text = success
            ? $"日志已清空（删除 {deleted} 个文件）；配置与去重记录已保留。"
            : $"日志已清空，但有部分文件删除失败（可能被占用）。";
    }

    private bool ConfirmClearLogs()
        => MessageBox.Show(
            this,
            "确定清空全部本地日志？清除后无法恢复。\n\n将清空：运行日志、会话抓取日志、消息历史缓存日志。\n不会删除：配置文件、消息去重 ID 缓存、所有设置参数。",
            "清空全部日志",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;

    private void RefreshEvents()
    {
        var selected = EventList.SelectedIndex;
        EventList.ItemsSource = _log.RecentSnapshot();

        if (selected >= 0 && selected < EventList.Items.Count)
        {
            EventList.SelectedIndex = selected;
        }
    }

    private void UpdateStatus()
    {
        StatusText.Text =
            $"微信: {_monitor.WeChatStatus}    QQ: {_monitor.QQStatus}    ntfy: {_monitor.NtfyStatus}    " +
            $"{(_monitor.IsPaused ? "已暂停监听" : "监听中")}    配置: {_config.FilePath}    日志: {_log.LogDir}";
    }
}
