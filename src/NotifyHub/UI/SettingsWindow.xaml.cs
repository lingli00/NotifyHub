using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NotifyHub.Models;
using NotifyHub.Services;
using NotifyHub.Services.Uia;

namespace NotifyHub.UI;

/// <summary>
/// 设置窗口：ntfy 地址、微信/QQ 开关、轮询间隔、通知时长与位置、灵动岛模式、
/// 两套提示音开关与音量、微信过滤、最近事件，以及一键清除日志。
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] CornerValues = { "TopRight", "TopLeft", "BottomRight", "BottomLeft" };

    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly MonitorController _monitor;
    private readonly NotificationManager _notifications;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public SettingsWindow(ConfigService config, LogService log, MonitorController monitor, NotificationManager notifications)
    {
        InitializeComponent();

        _config = config;
        _log = log;
        _monitor = monitor;
        _notifications = notifications;

        LoadFromConfig();
        RefreshEvents();

        _log.RecentChanged += OnRecentChanged;
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();

        Closed += (_, _) =>
        {
            _log.RecentChanged -= OnRecentChanged;
            _statusTimer.Stop();
        };
    }

    private void LoadFromConfig()
    {
        var config = _config.Current;
        NtfyUrlBox.Text = config.NtfyUrl;
        NtfyEnabledCheck.IsChecked = config.NtfyEnabled;
        WeChatCheck.IsChecked = config.WeChatEnabled;
        QQCheck.IsChecked = config.QQEnabled;
        IntervalBox.Text = config.PollIntervalMs.ToString();
        DurationBox.Text = config.NotificationDurationMs.ToString();
        AutoStartCheck.IsChecked = config.AutoStart;
        DumpOnStartCheck.IsChecked = config.DumpUiaOnStart;
        IslandFixedRadio.IsChecked = config.IslandMode != "Free";
        IslandFreeRadio.IsChecked = config.IslandMode == "Free";
        MusicEnabledCheck.IsChecked = config.MusicEnabled;

        MessageSoundCheck.IsChecked = config.MessageSoundEnabled;
        MessageVolumeSlider.Value = config.MessageSoundVolume;
        MessageVolumeText.Text = $"{config.MessageSoundVolume}%";

        TaskSoundCheck.IsChecked = config.TaskSoundEnabled;
        TaskVolumeSlider.Value = config.TaskSoundVolume;
        TaskVolumeText.Text = $"{config.TaskSoundVolume}%";

        WeChatFilterCheck.IsChecked = config.WeChatFilterEnabled;
        FilterKeywordsBox.Text = config.WeChatBlockedKeywords;

        var cornerIndex = Array.IndexOf(CornerValues, AppConfig.NormalizeCorner(config.NotificationCorner));
        CornerCombo.SelectedIndex = cornerIndex < 0 ? 0 : cornerIndex;
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
        next.NtfyUrl = NtfyUrlBox.Text.Trim();
        next.NtfyEnabled = NtfyEnabledCheck.IsChecked == true;
        next.WeChatEnabled = WeChatCheck.IsChecked == true;
        next.QQEnabled = QQCheck.IsChecked == true;
        next.PollIntervalMs = interval;
        next.NotificationDurationMs = duration;
        next.AutoStart = AutoStartCheck.IsChecked == true;
        next.DumpUiaOnStart = DumpOnStartCheck.IsChecked == true;
        next.IslandMode = IslandFreeRadio.IsChecked == true ? "Free" : "Fixed";
        next.MusicEnabled = MusicEnabledCheck.IsChecked == true;
        next.NotificationCorner = CornerValues[Math.Clamp(CornerCombo.SelectedIndex, 0, CornerValues.Length - 1)];

        next.MessageSoundEnabled = MessageSoundCheck.IsChecked == true;
        next.MessageSoundVolume = (int)Math.Round(MessageVolumeSlider.Value);
        next.TaskSoundEnabled = TaskSoundCheck.IsChecked == true;
        next.TaskSoundVolume = (int)Math.Round(TaskVolumeSlider.Value);

        next.WeChatFilterEnabled = WeChatFilterCheck.IsChecked == true;
        next.WeChatBlockedKeywords = FilterKeywordsBox.Text.Trim();

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
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("Settings", "打开日志目录失败", ex.Message);
        }
    }

    /// <summary>一键清除全部日志（二次确认，只清日志，不删配置与去重记录）。</summary>
    private void OnClearLogsClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定清空全部本地日志？清除后无法恢复。\n\n将清空：控制台展示日志、会话抓取日志、消息历史缓存日志。\n不会删除：配置文件、消息去重 ID 缓存、所有设置参数。",
            "清空全部日志",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var (deleted, success) = _log.ClearAll();
        RefreshEvents();
        StatusText.Text = success
            ? $"日志已清空（删除 {deleted} 个文件）；配置与去重记录已保留。"
            : $"日志已清空，但有 {deleted} 个文件删除失败（可能被占用）。";
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
            $"{(_monitor.IsPaused ? "已暂停监听" : "监听中")}    配置: {_config.FilePath}";
    }
}
