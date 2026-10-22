using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NotifyHub.Models;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 紧凑版深色控制台：顶部标题栏（标题 + 状态 + 设置 + 折叠 + 关闭）→ 中间日志区
/// （等宽字体、行高统一、可滚动、按级别着色）→ 底部单行命令输入 + 发送。
/// 职责：这里放完整详细日志；灵动岛/音乐悬浮条只放一行实时状态，两者不混淆。
/// </summary>
public partial class ConsoleWindow : Window
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly MonitorController _monitor;
    private readonly NotificationManager _notifications;
    private readonly SoundService _sound;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool _collapsed;
    private double _expandedHeight = 420;
    private bool _renderPending;

    public ConsoleWindow(
        LogService log,
        ConfigService config,
        MonitorController monitor,
        NotificationManager notifications,
        SoundService sound,
        Action openSettings)
    {
        InitializeComponent();

        _log = log;
        _config = config;
        _monitor = monitor;
        _notifications = notifications;
        _sound = sound;
        _openSettings = openSettings;

        Loaded += (_, _) => PositionBottomRight();
        _log.ConsoleChanged += OnLogChanged;
        _log.RecentChanged += OnLogChanged;

        _statusTimer.Tick += (_, _) => UpdateStatusLine();
        _statusTimer.Start();

        RenderAll();
        UpdateStatusLine();
        AppendSystem($"NotifyHub 控制台已就绪 · 配置：{_config.FilePath}");
        AppendSystem("输入 help 查看可用命令");
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left + 12, area.Right - Width - 24);
        Top = Math.Max(area.Top + 12, area.Bottom - Height - 24);
    }

    protected override void OnClosed(EventArgs e)
    {
        _log.ConsoleChanged -= OnLogChanged;
        _log.RecentChanged -= OnLogChanged;
        _statusTimer.Stop();
        base.OnClosed(e);
    }

    private void OnLogChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => RenderAll()));
            return;
        }

        RenderAll();
    }

    /// <summary>重建日志列表（最多 600 行，滚动到底）——行数有上限，重建成本可控。</summary>
    private void RenderAll()
    {
        if (_renderPending)
        {
            return;
        }

        _renderPending = true;

        try
        {
            var lines = _log.ConsoleSnapshot();
            LogList.Items.Clear();

            foreach (var line in lines)
            {
                AddLine(line);
            }

            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        }
        finally
        {
            _renderPending = false;
        }
    }

    private void AddLine(ConsoleLine line)
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
            FontSize = 12.5,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = BrushFor(line.Level),
        };

        LogList.Items.Add(new ListBoxItem
        {
            Content = text,
            Padding = new Thickness(9, 1, 9, 1),
            Background = Brushes.Transparent,
            Focusable = false,
        });
    }

    private void AppendSystem(string message)
    {
        _log.Info("Console", message);
    }

    private static Brush BrushFor(LogLevel level) => level switch
    {
        LogLevel.Success => new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),
        LogLevel.Warn => new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)),
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
        LogLevel.Debug => new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x85)),
        _ => new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xD1)),
    };

    private void UpdateStatusLine()
    {
        StatusText.Text =
            $"微信 {_monitor.WeChatStatus} · QQ {_monitor.QQStatus} · ntfy {_monitor.NtfyStatus}" +
            (_monitor.IsPaused ? " · 已暂停" : string.Empty) +
            (AppPaths.IsPortable ? " · 便携模式" : string.Empty);
    }

    // ---------------- 命令 ----------------

    private void OnSendClick(object sender, RoutedEventArgs e) => ExecuteCommand();

    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExecuteCommand();
            e.Handled = true;
        }
    }

    private void ExecuteCommand()
    {
        var raw = (CommandBox.Text ?? string.Empty).Trim();
        CommandBox.Clear();

        if (raw.Length == 0)
        {
            return;
        }

        AppendSystem($"> {raw}");

        var space = raw.IndexOf(' ');
        var command = (space < 0 ? raw : raw[..space]).ToLowerInvariant();
        var argument = space < 0 ? string.Empty : raw[(space + 1)..].Trim();

        try
        {
            switch (command)
            {
                case "help":
                case "?":
                    _log.Info("Console", "命令: help | clear | island <文本> | done <文本> | media <prev|toggle|next> | test <wechat|qq|ntfy|sound-message|sound-task> | stats | settings | pause | resume");
                    break;

                case "clear":
                case "cls":
                    ClearLogs(confirm: false);
                    break;

                case "island":
                    ShowIsland(argument.Length == 0 ? "正在执行任务…" : argument, done: false);
                    break;

                case "done":
                    ShowIsland(argument.Length == 0 ? "任务完成" : argument, done: true);
                    break;

                case "media":
                    var action = argument.Length == 0 ? "toggle" : argument;
                    if (IslandController.Current is { } controller)
                    {
                        controller.MediaCommand(action);
                        _log.Success("Console", $"媒体命令已发送: {action}");
                    }
                    else
                    {
                        _log.Warn("Console", "常驻实例未运行，无法控制播放器");
                    }

                    break;

                case "test":
                    RunTest(argument);
                    break;

                case "stats":
                    _log.Info("Console",
                        $"统计: 通知显示中 {_notifications.ActiveCount} · 排队 {_notifications.PendingCount} · 事件 {_log.RecentSnapshot().Count} 条",
                        $"配置 {_config.FilePath}");
                    break;

                case "settings":
                    _openSettings();
                    break;

                case "pause":
                    _monitor.SetPaused(true);
                    _log.Success("Console", "已暂停监听");
                    break;

                case "resume":
                    _monitor.SetPaused(false);
                    _log.Success("Console", "已恢复监听");
                    break;

                default:
                    _log.Warn("Console", $"未知命令: {command}（输入 help 查看）");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Console", "命令执行失败", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ShowIsland(string text, bool done)
    {
        if (IslandController.Current is { } controller)
        {
            controller.ShowTask(text, done);
        }
        else
        {
            IslandWindow.ShowTask(text, done);
        }

        _log.Success("Console", done ? "已发送完成态" : "已发送任务状态", text);
    }

    private void RunTest(string argument)
    {
        var now = DateTime.Now;

        switch (argument.ToLowerInvariant())
        {
            case "wechat":
                _notifications.Show(new NotifyMessage { Source = MessageSource.WeChat, Sender = "张三", Content = "在吗？需求文档我改了一版，你看下。", Time = now });
                break;
            case "qq":
                _notifications.Show(new NotifyMessage { Source = MessageSource.QQ, Sender = "产品讨论组", Content = "[图片] 版本评审材料已经发到群里了。", Time = now });
                break;
            case "ntfy":
                _notifications.Show(new NotifyMessage { Source = MessageSource.Ntfy, Sender = "CI 构建", Content = "notifyhub #128 构建成功，用时 2m14s。", Time = now });
                break;
            case "sound-message":
                _sound.PlayMessage(_config.Current);
                _log.Success("Console", "已播放消息通知提示音");
                break;
            case "sound-task":
                _sound.PlayTaskDone(_config.Current);
                _log.Success("Console", "已播放任务完成提示音");
                break;
            default:
                _log.Warn("Console", "用法: test wechat | qq | ntfy | sound-message | sound-task");
                break;
        }
    }

    /// <summary>清空日志（控制台清空与设置面板一键清除效果一致）。</summary>
    private void ClearLogs(bool confirm)
    {
        if (confirm)
        {
            var result = MessageBox.Show(
                this,
                "确定清空全部本地日志？清除后无法恢复。\n（不会删除配置与消息去重记录）",
                "清空日志",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK)
            {
                return;
            }
        }

        var (deleted, success) = _log.ClearAll();
        RenderAll();

        _log.Success("Console", success ? $"日志已清空（删除 {deleted} 个文件）" : "日志已清空，但部分文件删除失败");
    }

    // ---------------- 窗口操作 ----------------

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
            // 拖拽异常忽略
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _openSettings();

    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;

        if (_collapsed)
        {
            _expandedHeight = Height;
            Body.Visibility = Visibility.Collapsed;
            InputBar.Visibility = Visibility.Collapsed;
            Height = 40;
            CollapseButton.Content = "\u25B4";
        }
        else
        {
            Body.Visibility = Visibility.Visible;
            InputBar.Visibility = Visibility.Visible;
            Height = _expandedHeight > 0 ? _expandedHeight : 420;
            CollapseButton.Content = "\u25BE";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
