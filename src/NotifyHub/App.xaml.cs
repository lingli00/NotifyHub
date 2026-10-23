using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using NotifyHub.Interop;
using NotifyHub.Models;
using NotifyHub.Services;
using NotifyHub.Services.Parsing;
using NotifyHub.Services.Uia;
using NotifyHub.UI;

namespace NotifyHub;

/// <summary>
/// 应用入口。托盘常驻、无主窗口：
/// 正常启动 → 托盘 + 三个监听源；
/// --selftest / --selftest-ui / --demo / --dump 用于自动化验证与首次适配。
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private TaskbarIcon? _tray;
    private ConfigService? _configService;
    private MonitorController? _monitor;
    private NotificationManager? _notifications;
    private IslandController? _islandController;
    private SoundService? _sound;
    private AppSoundMuter? _soundMuter;
    private SettingsWindow? _settings;
    private MenuItem? _pauseMenuItem;
    private DispatcherTimer? _eventFlushTimer;

    private bool _demoPassed;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        var options = CommandLineOptions.Parse(e.Args);

        // 灵动岛 / 媒体转发是最热的一条路径（AI 每推进一步都会调），
        // 必须抢在日志初始化、配置读取、声音子系统之前处理掉：
        // 这些初始化对"只发一行状态就退出"的进程纯属浪费，实测能省掉一大截启动时间。
        if (options.Mode is AppMode.Island or AppMode.IslandDone or AppMode.Media or AppMode.IslandStream)
        {
            RunIslandFastPath(options);
            return;
        }

        base.OnStartup(e);

        var log = LogService.Shared;
        log.Initialize();
        _sound = new SoundService(log);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error("App", "未处理异常", args.ExceptionObject?.ToString() ?? "(null)");

        // 自检 / 帮助 / dump 三种模式不需要完整 GUI 与托盘
        switch (options.Mode)
        {
            case AppMode.Help:
                ConsoleBridge.TryAttach();
                ConsoleBridge.WriteLine("NotifyHub 0.2 — 桌面消息聚合通知 · 灵动岛 · 音乐控制");
                ConsoleBridge.WriteLine("  NotifyHub.exe            托盘常驻运行");
                ConsoleBridge.WriteLine("  NotifyHub.exe --selftest 无界面逻辑自检（退出码 0=通过）");
                ConsoleBridge.WriteLine("  NotifyHub.exe --selftest-ui 通知层自检（不抢焦点/堆叠/位置）");
                ConsoleBridge.WriteLine("  NotifyHub.exe --demo     弹出 6 条演示通知（14 秒后退出）");
                ConsoleBridge.WriteLine("  NotifyHub.exe --dump     导出微信/QQ 的 UIA 树后退出");
                ConsoleBridge.WriteLine("  NotifyHub.exe --locate 名字  激活微信并搜索定位到该聊天（验证定位链路）");
                ConsoleBridge.WriteLine("  NotifyHub.exe --island 文本      灵动岛：显示一行执行中动作（转发给常驻实例）");
                ConsoleBridge.WriteLine("  NotifyHub.exe --island-done 文本 灵动岛：播放完成动画 + 完成提示音后收起");
                ConsoleBridge.WriteLine("  NotifyHub.exe --island-stream    从标准输入逐行读状态（长连接，更新延迟 <100ms）");
                ConsoleBridge.WriteLine("  NotifyHub.exe --media toggle|play|pause|next|prev  控制当前播放器");
                ConsoleBridge.WriteLine("  灵动岛：设置页可切换 固定顶部居中 / 自由拖动（左/右键拖空白区，10 秒无交互自动吸附四边）");
                ConsoleBridge.WriteLine("  日志：设置页「运行日志」栏（原独立控制台已合并进来）");
                Shutdown();
                return;

            case AppMode.SelfTest:
            {
                // UIA 的 COM 调用必须跑在 MTA 后台线程：WPF UI 线程是 STA 且尚未进消息循环，
                // 直接调用会死锁。Task.Run 的线程池线程是 MTA。
                var code = Task.Run(SelfTest.RunAll).GetAwaiter().GetResult();
                Environment.Exit(code);
                return;
            }

            case AppMode.Dump:
            {
                var dump = Task.Run(() => UiaDumper.DumpAll(log)).GetAwaiter().GetResult();
                ConsoleBridge.TryAttach();
                ConsoleBridge.WriteLine(dump.Summary);
                Shutdown();
                return;
            }

            case AppMode.Locate when options.LocateTarget is not null:
            {
                // 手动验证“点击通知→定位聊天”链路：激活微信并搜索该联系人
                var message = new NotifyMessage
                {
                    Source = MessageSource.WeChat,
                    Sender = options.LocateTarget,
                    Content = string.Empty,
                };
                var ok = Task.Run(() => WindowActivator.TryOpenConversation(message, log)).GetAwaiter().GetResult();
                ConsoleBridge.TryAttach();
                ConsoleBridge.WriteLine(ok ? "LOCATE OK" : "LOCATE FAIL");
                Shutdown();
                return;
            }

            case AppMode.Sessions:
            {
                // 真机核验微信消息识别链路（需求 4/5）：
                // 读一次会话列表，按监听器同款解析打印每行的 FromSelf / 未读 / 免打扰标记。
                var rows = Task.Run(() =>
                {
                    var handle = WindowFinder.FindMainWindow(
                        new[] { "WeChat", "Weixin" },
                        new[] { "WeChatMainWndForPC", "WeChatMainWndForPC1", "mmui::MainWindow" });
                    if (handle == IntPtr.Zero)
                    {
                        return (List<SessionRow>?)null;
                    }

                    var element = AutomationElement.FromHandle(handle);
                    return element is null ? new List<SessionRow>() : SessionListReader.Read(element).Rows;
                }).GetAwaiter().GetResult();

                ConsoleBridge.TryAttach();

                if (rows is null)
                {
                    ConsoleBridge.WriteLine("SESSIONS FAIL（未找到微信主窗口）");
                    Environment.Exit(2);
                    return;
                }

                // 控制台按 UTF-8 输出，避免中文在 GBK 代码页下乱码
                try
                {
                    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                }
                catch
                {
                    // 无控制台时忽略
                }

                ConsoleBridge.WriteLine($"SESSIONS {rows.Count} 行（FromSelf=自己发的，不推送；HasUnread=未读）");
                foreach (var row in rows)
                {
                    var kind = WeChatFilter.Classify(row);
                    ConsoleBridge.WriteLine(
                        $"  [{row.Contact}] 类型={WeChatFilter.KindName(kind)} FromSelf={row.FromSelf} " +
                        $"未读={row.UnreadCount} Muted={row.Muted} | {row.Summary}");
                }

                Shutdown();
                return;
            }

            case AppMode.Settings:
                {
                    // 只开设置窗口（不启动监听、不留托盘），用于排版检查；
                    // 用 RenderTargetBitmap 自渲染成 PNG——不依赖屏幕截屏，也没遮挡问题。
                    // 两个 Tab 各截一张（设置 / 运行日志）。
                    var configService = new ConfigService(log);
                    configService.Load();
                    var notifications = new NotificationManager(log, () => configService.Current, _sound) { DemoMode = true };
                    var monitor = new MonitorController(log, configService);
                    var settings = new SettingsWindow(configService, log, monitor, notifications);
                    settings.Show();

                    var captureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    captureTimer.Tick += (_, _) =>
                    {
                        captureTimer.Stop();
                        CaptureWindow(settings, "settings-capture.png", log);

                        // 整页内容（ScrollViewer 会被视口裁剪，直接 Render 内容面板拿到完整排版）
                        CaptureVisual(settings.SettingsContent, "settings-full.png", log);

                        settings.Tabs.SelectedIndex = 1;
                        settings.UpdateLayout();

                        var second = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        second.Tick += (_, _) =>
                        {
                            second.Stop();
                            CaptureWindow(settings, "settings-logs.png", log);
                            Shutdown();
                        };
                        second.Start();
                    };
                    captureTimer.Start();
                    return;
                }

            case AppMode.MarqueeTest:
                {
                    // 跑马灯离屏自检：各形态离屏渲染成 PNG，并按“非背景像素数”判定文字是否真的画出来了。
                    var host = new Window
                    {
                        Width = 900,
                        Height = 420,
                        WindowStyle = WindowStyle.None,
                        AllowsTransparency = true,
                        Background = Brushes.Transparent,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        Topmost = true,
                    };

                    var panel = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Margin = new Thickness(24),
                    };
                    var dark = new Border
                    {
                        CornerRadius = new CornerRadius(22),
                        Padding = new Thickness(18, 10, 18, 10),
                        Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x2C, 0x2C, 0x31)),
                        Child = panel,
                    };
                    host.Content = dark;

                    // 四个形态：短文本(显式色)/短文本(继承色)/长文本(滚动)/长文本(继承色)
                    var shortExplicit = new UI.MarqueeText
                    {
                        Width = 500,
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)),
                        Text = "短文本应该水平居中显示",
                    };
                    var shortInherit = new UI.MarqueeText { Width = 500, FontSize = 14, Text = "继承色短文本" };
                    var longExplicit = new UI.MarqueeText
                    {
                        Width = 500,
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)),
                        Text = new string('滚', 60) + "——长文本应该自动横向循环滚动",
                    };
                    var longInherit = new UI.MarqueeText { Width = 500, FontSize = 14, Text = new string('动', 60) };

                    // ②④ 的前景色来自父级容器（ContentControl 本地值经继承链下传）。
                    // 注意：WPF 继承属性有个固有行为——树中无人设色时，继承链会从树根解析出
                    // Control 的黑色默认值，优先级高于派生类型的元数据默认值（实测探针证实），
                    // 所以“控件自身默认色”在视觉树里永远是黑色；这里验证的是 OverrideMetadata
                    // 中声明的 Inherits 选项：父级设色能正常传到自绘控件上。
                    var inheritBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x66));
                    var shortInheritHost = new ContentControl { Foreground = inheritBrush, Content = shortInherit };
                    var longInheritHost = new ContentControl { Foreground = inheritBrush, Content = longInherit };

                    panel.Children.Add(new TextBlock { Text = "① 短文本·显式前景色（居中不滚动）", Foreground = Brushes.White });
                    panel.Children.Add(shortExplicit);
                    panel.Children.Add(new TextBlock { Text = "② 短文本·父级继承色（居中不滚动）", Foreground = Brushes.White });
                    panel.Children.Add(shortInheritHost);
                    panel.Children.Add(new TextBlock { Text = "③ 长文本·显式前景色（循环滚动）", Foreground = Brushes.White });
                    panel.Children.Add(longExplicit);
                    panel.Children.Add(new TextBlock { Text = "④ 长文本·父级继承色（循环滚动）", Foreground = Brushes.White });
                    panel.Children.Add(longInheritHost);

                    host.Show();

                    var evalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    evalTimer.Tick += async (_, _) =>
                    {
                        evalTimer.Stop();

                        var controls = new (string Name, UI.MarqueeText Control, bool ExpectScroll)[]
                        {
                            ("① 短文本·显式前景色", shortExplicit, false),
                            ("② 短文本·父级继承色", shortInherit, false),
                            ("③ 长文本·显式前景色", longExplicit, true),
                            ("④ 长文本·父级继承色", longInherit, true),
                        };

                        // 先挂起逐帧重绘，保证抓拍稳定（悬停校验前再恢复）
                        foreach (var (_, control, _) in controls)
                        {
                            control.SuspendRendering = true;
                        }
                        host.UpdateLayout();

                        var lines = new List<string>();
                        var allOk = true;

                        // 整窗抓拍 + 逐控件裁剪计数。
                        // 注意：RTB 直接 Render 滚动控件会抓到空帧——这是 WPF 对持续
                        // InvalidateVisual 动画控件的已知局限（实测静态控件正常、滚动控件空帧），
                        // 整窗抓拍不受影响，故走“整窗抓拍 → 按控件坐标裁剪”的路径。
                        BitmapSource? shot = null;
                        try
                        {
                            shot = CaptureWindowBitmap(host);
                        }
                        catch (Exception ex)
                        {
                            lines.Add($"FAIL  整窗抓拍失败: {ex.GetType().Name}: {ex.Message}");
                            allOk = false;
                        }

                        var dumpIndex = 1;
                        foreach (var (name, control, expectScroll) in controls)
                        {
                            var lit = -1;
                            var litCenter = -1.0;
                            var cropWidth = 500.0;
                            var rect = default(Int32Rect);
                            var alphaCount = -1;
                            var maxR = -1;
                            var maxG = -1;
                            if (shot is not null)
                            {
                                (lit, litCenter, cropWidth, rect, alphaCount, maxR, maxG) =
                                    LitInShot(shot, host, control, $"marquee-crop-{dumpIndex++}.png");
                            }

                            var scrollOk = control.IsScrolling == expectScroll;
                            var renderOk = lit > 30;
                            // 短文本居中校验（需求 2）：亮像素水平中心应落在可视区中心 ±2px
                            var centerOk = expectScroll || Math.Abs(litCenter - cropWidth / 2.0) <= 2;
                            var ok = scrollOk && renderOk && centerOk;
                            allOk &= ok;
                            lines.Add(
                                $"{(ok ? "PASS" : "FAIL")}  {name}: 亮像素={lit} " +
                                $"亮区中心={litCenter:F1}（期望 {cropWidth / 2.0:F1}） " +
                                $"滚动={control.IsScrolling}（期望 {expectScroll}）");
                            lines.Add(
                                $"      调试 {name}: {control.DebugState} " +
                                $"[裁剪={rect} alpha>200的像素={alphaCount} maxR={maxR} maxG={maxG}]");
                        }

                        // 悬停暂停 / 移开继续（需求 1）：合成鼠标进入/离开事件，观察滚动位移
                        foreach (var (_, control, _) in controls)
                        {
                            control.SuspendRendering = false;
                        }
                        await Task.Delay(150);

                        var hoverIn = new System.Windows.Input.MouseEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, 0)
                        {
                            RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent,
                        };
                        var hoverOut = new System.Windows.Input.MouseEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, 0)
                        {
                            RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent,
                        };

                        var offsetBefore = longExplicit.Offset;
                        longExplicit.RaiseEvent(hoverIn);
                        await Task.Delay(450);
                        var offsetHovering = longExplicit.Offset;
                        var hoverPaused = Math.Abs(offsetHovering - offsetBefore) < 0.5;

                        longExplicit.RaiseEvent(hoverOut);
                        await Task.Delay(450);
                        var offsetAfter = longExplicit.Offset;
                        var resumed = offsetAfter < offsetHovering - 1;

                        var hoverOk = hoverPaused && resumed;
                        allOk &= hoverOk;
                        lines.Add(
                            $"{(hoverOk ? "PASS" : "FAIL")}  ⑤ 悬停暂停/移开继续: " +
                            $"进入前={offsetBefore:F1} 悬停中={offsetHovering:F1} 离开后={offsetAfter:F1}");

                        foreach (var line in lines)
                        {
                            ConsoleBridge.WriteLine(line);
                            log.Write(allOk ? LogLevel.Info : LogLevel.Error, "MarqueeTest", line);
                        }

                        try
                        {
                            CaptureWindow(host, "marquee-test.png", log);
                        }
                        catch
                        {
                            // 截图失败不影响结论
                        }

                        Environment.Exit(allOk ? 0 : 1);
                    };
                    evalTimer.Start();
                    return;
                }

        }

        _mutex = new Mutex(true, @"Local\NotifyHub.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            log.Warn("App", "已有 NotifyHub 实例在运行，本次启动退出");
            Shutdown();
            return;
        }

        _configService = new ConfigService(log);
        _configService.Load();
        _configService.Changed += OnConfigChanged;

        // 日志目录自定义（需求 8）：配置里指定了就切换过去（含历史事件文件迁移）
        if (!string.IsNullOrWhiteSpace(_configService.Current.LogDir))
        {
            log.SetLogDir(_configService.Current.LogDir);
        }

        _notifications = new NotificationManager(log, () => _configService.Current, _sound);

        // 监听应用提示音静音：本应用消息提示音开启时，微信/QQ 自带音效自动关闭
        _soundMuter = new AppSoundMuter(log);
        _soundMuter.Apply(ShouldMuteAppSounds());

        if (options.Mode is AppMode.Demo or AppMode.SelfTestUi)
        {
            RunNotificationDemo(options.Mode == AppMode.SelfTestUi, options.Seconds);
            return;
        }

        _monitor = new MonitorController(log, _configService);
        _monitor.NotificationReady += OnNotificationReady;
        _monitor.Start();

        CreateTray();

        // 灵动岛总控：WorkBuddy 任务状态 + 音乐监控（SMTC）+ 全屏自动隐藏 + 位置持久化
        _islandController = new IslandController(log, _configService, Dispatcher, _sound);
        _islandController.Start();

        // 灵动岛转发通道：常驻期间监听 --island 命令
        IslandIpc.StartServer(Dispatcher, log);

        _eventFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _eventFlushTimer.Tick += (_, _) =>
        {
            log.FlushIfDirty();
            _monitor?.FlushCaches();

            // 周期补静音：微信/QQ 比 NotifyHub 后启动时，新音频会话也要静音（内部有节流）
            _soundMuter?.Apply(ShouldMuteAppSounds());
        };
        _eventFlushTimer.Start();

        // exe 被移动过的话，修正自启注册表里的路径
        if (_configService.Current.AutoStart)
        {
            StartupService.SetEnabled(true, log);
        }

        if (_configService.Current.DumpUiaOnStart)
        {
            log.Info("App", "探测模式：启动时导出 UIA 树");
            Task.Run(() => UiaDumper.DumpAll(log));
        }

        log.Info("App", "NotifyHub 已启动（托盘常驻）");
    }

    /// <summary>把窗口当前外观自渲染成位图（排版验证/自检用，不依赖屏幕截屏）。</summary>
    private static BitmapSource CaptureWindowBitmap(Window window)
    {
        // 注意方向：TransformToDevice 才是 DIP→设备像素的缩放（1.0=96DPI）
        var toDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice
                       ?? Matrix.Identity;
        var width = (int)Math.Ceiling(window.ActualWidth * toDevice.M11);
        var height = (int)Math.Ceiling(window.ActualHeight * toDevice.M22);

        var rtb = new RenderTargetBitmap(
            width, height, 96 * toDevice.M11, 96 * toDevice.M22, PixelFormats.Pbgra32);
        rtb.Render(window);
        return rtb;
    }

    /// <summary>把窗口当前外观自渲染成 PNG（排版验证用，不依赖屏幕截屏）。</summary>
    private static void CaptureWindow(Window window, string fileName, LogService log)
    {
        try
        {
            var rtb = CaptureWindowBitmap(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            var path = Path.Combine(AppPaths.Root, fileName);
            using var stream = File.Create(path);
            encoder.Save(stream);
            log.Info("Settings", "窗口渲染完成", path);
        }
        catch (Exception ex)
        {
            log.Error("Settings", "窗口渲染失败", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 渲染 ScrollViewer 里的完整内容面板（视口裁剪不到的部分也拍全，排版验证用）。
    /// 先按内容实测尺寸撑一次布局，再离屏渲染，避免拍到半截。
    /// </summary>
    private static void CaptureVisual(FrameworkElement content, string fileName, LogService log)
    {
        try
        {
            content.Measure(new Size(content.ActualWidth > 0 ? content.ActualWidth : 1000, double.PositiveInfinity));
            var width = (int)Math.Ceiling(content.ActualWidth > 0 ? content.ActualWidth : content.DesiredSize.Width);
            var height = (int)Math.Ceiling(content.DesiredSize.Height);

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(content);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            var path = Path.Combine(AppPaths.Root, fileName);
            using var stream = File.Create(path);
            encoder.Save(stream);
            log.Info("Settings", "整页渲染完成", path);
        }
        catch (Exception ex)
        {
            log.Error("Settings", "整页渲染失败", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 从整窗抓拍位图中裁出指定控件的区域，统计“亮像素”（文字笔画）数量，
    /// 并返回亮像素水平中心与裁剪区宽度（短文本居中校验用）。
    /// </summary>
    private static (int Lit, double Center, int CropWidth, Int32Rect Rect, int AlphaCount, int MaxR, int MaxG) LitInShot(
        BitmapSource shot, Window host, UI.MarqueeText control, string? dumpFileName = null)
    {
        var toDevice = PresentationSource.FromVisual(host)?.CompositionTarget?.TransformToDevice
                       ?? Matrix.Identity;
        var topLeft = control.TranslatePoint(new Point(0, 0), host);
        var x = (int)Math.Round(topLeft.X * toDevice.M11);
        var y = (int)Math.Round(topLeft.Y * toDevice.M22);
        var w = (int)Math.Round(control.ActualWidth * toDevice.M11);
        var h = (int)Math.Round(control.ActualHeight * toDevice.M22);

        // 裁进位图边界内
        x = Math.Max(0, Math.Min(x, shot.PixelWidth - 1));
        y = Math.Max(0, Math.Min(y, shot.PixelHeight - 1));
        w = Math.Max(1, Math.Min(w, shot.PixelWidth - x));
        h = Math.Max(1, Math.Min(h, shot.PixelHeight - y));

        var crop = new CroppedBitmap(shot, new Int32Rect(x, y, w, h));

        if (!string.IsNullOrEmpty(dumpFileName))
        {
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(crop));
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dumpFileName);
                using var fs = File.Create(path);
                encoder.Save(fs);
            }
            catch
            {
                // 诊断落盘失败不影响主流程
            }
        }

        var pixels = new byte[crop.PixelWidth * crop.PixelHeight * 4];
        crop.CopyPixels(pixels, crop.PixelWidth * 4, 0);
        var lit = 0;
        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var alphaCount = 0;
        var maxR = 0;
        var maxG = 0;
        for (var row = 0; row < crop.PixelHeight; row++)
        {
            for (var col = 0; col < crop.PixelWidth; col++)
            {
                var i = (row * crop.PixelWidth + col) * 4;
                // 文字笔画：高亮度且不透明（深色卡片底 R=44，通不过该阈值）。
                // 注意字节序：PixelFormats.Pbgra32 的通道序是 B,G,R,A——
                // R 在 i+2、G 在 i+1、A 在 i+3（曾按 RGBA 读，导致灰色文字
                // 碰巧通过、彩色文字全被漏判）。
                if (pixels[i + 3] > 200 && pixels[i + 2] > 150 && pixels[i + 1] > 150)
                {
                    lit++;
                    if (col < minX)
                    {
                        minX = col;
                    }
                    if (col > maxX)
                    {
                        maxX = col;
                    }
                }
                if (pixels[i + 3] > 200)
                {
                    alphaCount++;
                }
                if (pixels[i + 2] > maxR)
                {
                    maxR = pixels[i + 2];
                }
                if (pixels[i + 1] > maxG)
                {
                    maxG = pixels[i + 1];
                }
            }
        }

        var center = lit > 0 ? (minX + maxX) / 2.0 : -1;
        return (lit, center, crop.PixelWidth, new Int32Rect(x, y, w, h), alphaCount, maxR, maxG);
    }

    /// <summary>
    /// 灵动岛快路径：抢在日志初始化 / 配置读取 / 声音子系统之前处理，
    /// 只做"连管道 → 写一行 → 退出"，把单次命令的进程内开销压到最低。
    /// 常驻实例没起来时退化成独立弹出（播完即退）。
    /// </summary>
    private void RunIslandFastPath(CommandLineOptions options)
    {
        var log = LogService.Shared;

        try
        {
            switch (options.Mode)
            {
                case AppMode.IslandStream:
                {
                    // 常驻状态流：标准输入逐行读，复用同一条管道长连接
                    var forwarded = IslandIpc.RunStreamForwarder(log);
                    ConsoleBridge.TryAttach();
                    ConsoleBridge.WriteLine($"STREAM END（已转发 {forwarded} 条）");
                    Shutdown();
                    return;
                }

                case AppMode.Media when options.MediaAction is not null:
                {
                    if (!IslandIpc.TryForwardMedia(options.MediaAction, log))
                    {
                        ConsoleBridge.TryAttach();
                        ConsoleBridge.WriteLine("MEDIA FAIL（NotifyHub 常驻实例未运行）");
                        Environment.Exit(1);
                        return;
                    }

                    Shutdown();
                    return;
                }

                case AppMode.Island:
                case AppMode.IslandDone:
                {
                    var done = options.Mode == AppMode.IslandDone;
                    var text = options.IslandText ?? (done ? "完成" : "正在执行任务…");

                    if (IslandIpc.TryForward(text, done, log))
                    {
                        Shutdown();
                        return;
                    }

                    var island = new IslandWindow();

                    if (done)
                    {
                        island.ShowDone(text);
                    }
                    else
                    {
                        island.ShowRunning(text);
                    }

                    // 独立模式也要有完成提示音（读用户配置里的开关与音量）
                    if (done)
                    {
                        try
                        {
                            var standalone = new ConfigService(log);
                            standalone.Load();
                            using var sound = new SoundService(log);
                            sound.PlayTaskDone(standalone.Current);
                        }
                        catch
                        {
                            // 提示音失败不影响展示
                        }
                    }

                    var timer = new DispatcherTimer
                    {
                        Interval = done ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(8),
                    };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        Shutdown();
                    };
                    timer.Start();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error("App", "灵动岛快路径失败", $"{ex.GetType().Name}: {ex.Message}");
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        _eventFlushTimer?.Stop();
        _notifications?.Clear();
        _monitor?.Dispose();
        _islandController?.Dispose();
        _soundMuter?.RestoreSync();
        _sound?.Dispose();
        LogService.Shared.FlushIfDirty();

        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // 忽略
        }

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // 未持有则忽略
            }

            _mutex.Dispose();
            _mutex = null;
        }

        LogService.Shared.Info("App", "NotifyHub 已退出");
        base.OnExit(e);
    }

    private void CreateTray()
    {
        var log = LogService.Shared;
        var menu = new ContextMenu();

        var settingsItem = new MenuItem { Header = "设置 / 日志(_S)…" };
        settingsItem.Click += (_, _) => ShowSettings();

        _pauseMenuItem = new MenuItem { Header = "暂停监听", IsCheckable = true };
        _pauseMenuItem.Click += (_, _) => TogglePause(_pauseMenuItem.IsChecked);

        var logItem = new MenuItem { Header = "打开日志目录" };
        logItem.Click += (_, _) => OpenLogFolder();

        var dumpItem = new MenuItem { Header = "导出 UIA 诊断树" };
        dumpItem.Click += (_, _) => Task.Run(() =>
        {
            var result = UiaDumper.DumpAll(log);
            log.Info("App", "UIA 诊断树已导出", result.FilePath);
        });

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Shutdown();

        menu.Items.Add(settingsItem);
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(dumpItem);
        menu.Items.Add(logItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _tray = new TaskbarIcon
        {
            IconSource = IconFactory.Create(32),
            ToolTipText = "NotifyHub · 微信 / QQ / ntfy 通知",
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
        };

        // 双击托盘直接开设置（日志也在这里），不再有独立控制台窗口
        _tray.TrayMouseDoubleClick += (_, _) => ShowSettings();

        log.Info("App", "托盘图标已创建");
    }

    private void TogglePause(bool paused)
    {
        _monitor?.SetPaused(paused);

        if (_tray is not null)
        {
            _tray.IconSource = IconFactory.Create(32, paused);
            _tray.ToolTipText = paused
                ? "NotifyHub · 已暂停监听"
                : "NotifyHub · 微信 / QQ / ntfy 通知";
        }

        if (_pauseMenuItem is not null)
        {
            _pauseMenuItem.IsChecked = paused;
        }
    }

    private void ShowSettings()
    {
        if (_configService is null || _monitor is null || _notifications is null)
        {
            return;
        }

        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(_configService, LogService.Shared, _monitor, _notifications);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    private void OpenLogFolder()
    {
        try
        {
            var dir = LogService.Shared.LogDir;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Shared.Warn("App", "打开日志目录失败", ex.Message);
        }
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        LogService.Shared.Info("App", $"配置已更新: 间隔={config.PollIntervalMs}ms 时长={config.NotificationDurationMs}ms");
        _soundMuter?.Apply(ShouldMuteAppSounds());
    }

    private bool ShouldMuteAppSounds()
        => _configService is { } cfg && cfg.Current.SuppressAppSounds && cfg.Current.MessageSoundEnabled;

    private void OnNotificationReady(NotifyMessage message)
    {
        if (_shuttingDown)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => _notifications?.Show(message)));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Shared.Error("App", "UI 线程异常（已拦截，程序继续运行）", $"{e.Exception.GetType().Name}: {e.Exception.Message}{Environment.NewLine}{e.Exception.StackTrace}");
        e.Handled = true;
    }

    /// <summary>
    /// 演示 / 通知层自检：一次弹 6 条，验证“堆叠最多 5 条、位置在屏幕内、不抢焦点”。
    /// 结果写日志 + demo-result.txt，--selftest-ui 用退出码表达结论。
    /// </summary>
    private void RunNotificationDemo(bool assertMode, int seconds)
    {
        var log = LogService.Shared;
        _notifications!.DemoMode = true;

        var beforeForeground = NativeMethods.GetForegroundWindow();

        var samples = new[]
        {
            new NotifyMessage { Source = MessageSource.WeChat, Sender = "张三", Content = "在吗？刚才那个需求文档我改了一版，你看下。" },
            new NotifyMessage { Source = MessageSource.QQ, Sender = "产品讨论组", Content = "[图片] 评审材料已经发到群里了，麻烦看一下。" },
            new NotifyMessage { Source = MessageSource.Ntfy, Sender = "CI 构建", Content = "notifyhub #128 构建成功，用时 2m14s。" },
            new NotifyMessage { Source = MessageSource.WeChat, Sender = "文件传输助手", Content = "report-2026-10.pdf" },
            new NotifyMessage { Source = MessageSource.QQ, Sender = "QQ", Content = "QQ 收到一条新消息", LowFidelity = true },
            new NotifyMessage { Source = MessageSource.Ntfy, Sender = "磁盘告警", Content = "C 盘使用率 91%，请及时清理。" },
        };

        foreach (var sample in samples)
        {
            _notifications.Show(sample);
        }

        log.Info("Demo", $"已弹出 {samples.Length} 条演示通知，等待渲染后校验");

        var evalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        evalTimer.Tick += (_, _) =>
        {
            evalTimer.Stop();
            EvaluateDemo(beforeForeground, samples.Length);
        };
        evalTimer.Start();

        var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(assertMode ? 6 : seconds) };
        exitTimer.Tick += (_, _) =>
        {
            exitTimer.Stop();
            if (assertMode)
            {
                Environment.Exit(_demoPassed ? 0 : 1);
            }

            Shutdown();
        };
        exitTimer.Start();
    }

    private void EvaluateDemo(IntPtr beforeForeground, int shownCount)
    {
        var log = LogService.Shared;
        var afterForeground = NativeMethods.GetForegroundWindow();
        var count = _notifications!.ActiveCount;
        var expected = Math.Min(shownCount, AppConfig.MaxStackLimit);
        var area = SystemParameters.WorkArea;

        var allInside = _notifications.ActiveWindows.All(window =>
            window.Left >= area.Left - 1
            && window.Top >= area.Top - 1
            && window.Left + window.Width <= area.Right + 1
            && window.Top + window.Height <= area.Bottom + 1);

        var focusOk = beforeForeground == afterForeground;
        var stackOk = count == expected;
        var visibleOk = _notifications.ActiveWindows.All(w => w.IsVisible && w.CardOpacity > 0.9);

        // Win32 侧取证 + 屏幕抓图，用于离线核对“窗口是否真的显示在桌面上”
        var win32Info = DemoCapture.Describe(_notifications.ActiveWindows);
        var capturePath = Path.Combine(AppPaths.Root, "demo-capture.png");
        var capture = DemoCapture.CaptureRegion(_notifications.ActiveWindows, capturePath);

        _demoPassed = focusOk && stackOk && allInside && visibleOk;

        var details = string.Join(" | ", _notifications.ActiveWindows.Select(w =>
            $"L={w.Left:F0} T={w.Top:F0} vis={w.IsVisible} op={w.CardOpacity:F2}"));

        var text =
            $"演示校验: 弹出 {shownCount} 条 → 存活 {count} 条（期望 {expected}）" +
            $"；抢焦点={(focusOk ? "否" : "是")}" +
            $"（before=0x{beforeForeground.ToInt64():X} after=0x{afterForeground.ToInt64():X}）" +
            $"；位置均在屏幕内={allInside}" +
            $"；窗口均可见且不透明={visibleOk}" +
            $"；结论={(_demoPassed ? "PASS" : "FAIL")}" +
            $"{Environment.NewLine}明细: {details}" +
            $"{Environment.NewLine}Win32: {win32Info}" +
            $"{Environment.NewLine}抓图: {capture ?? "(失败)"}";

        log.Info("Demo", text);

        try
        {
            File.WriteAllText(
                Path.Combine(AppPaths.Root, "demo-result.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{text}{Environment.NewLine}" +
                $"工作区: {area}{Environment.NewLine}");
        }
        catch
        {
            // 忽略
        }
    }
}
