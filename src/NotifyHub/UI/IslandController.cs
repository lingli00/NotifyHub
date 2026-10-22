using System.Windows.Threading;
using NotifyHub.Models;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 灵动岛总控（常驻模式）：粘合 WorkBuddy 任务状态、音乐播放器监控（SMTC）、
/// 全屏检测、任务完成提示音与配置持久化。懒创建唯一的 IslandWindow 并注入配置读取器。
/// 任务状态只保留“一行最新实时动作文字”，多行日志会被压平成一行。
/// </summary>
public sealed class IslandController : IDisposable
{
    /// <summary>当前总控（IslandIpc 转发时使用）。</summary>
    public static IslandController? Current { get; private set; }

    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly Dispatcher _dispatcher;
    private readonly SoundService _sound;
    private readonly MediaPlayerMonitor _media;
    private readonly FullscreenWatcher _fullscreen;
    private WorkBuddyWatcher? _workbuddy;
    private long _manualUntilMs; // 手动 --island 推送后的 15 秒内，自动监视不覆盖
    private IslandWindow? _island;
    private bool _started;

    public IslandController(LogService log, ConfigService config, Dispatcher dispatcher, SoundService sound)
    {
        _log = log;
        _config = config;
        _dispatcher = dispatcher;
        _sound = sound;

        _media = new MediaPlayerMonitor(log);
        _fullscreen = new FullscreenWatcher(log);
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Current = this;

        _config.Changed += OnConfigChanged;

        _media.MediaChanged += info => _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Island().SetMedia(info);
            }
            catch (Exception ex)
            {
                _log.Error("Island", "更新音乐行失败", $"{ex.GetType().Name}: {ex.Message}");
            }
        }));

        _fullscreen.FullscreenChanged += fullscreen => _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Island().SetFullscreen(fullscreen);
            }
            catch (Exception ex)
            {
                _log.Error("Island", "切换全屏状态失败", $"{ex.GetType().Name}: {ex.Message}");
            }
        }));

        _ = _media.StartAsync();

        StartWorkBuddyWatch();

        // 预热灵动岛窗口（离屏建 HWND + 首帧渲染），
        // 让第一条状态更新也能在 100ms 内上屏，而不是被窗口创建时间吃掉。
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500).ConfigureAwait(false);
            _ = _dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Island().WarmUp();
                }
                catch (Exception ex)
                {
                    _log.Warn("Island", "预热灵动岛失败", ex.Message);
                }
            }));
        });

        _log.Info("Island", "灵动岛总控已启动（音乐监控 + 全屏检测 + 任务完成提示音）");
    }

    /// <summary>显示任务状态（任意线程调用）。只取首行、压平空白、超长截断。</summary>
    public void ShowTask(string text, bool done)
    {
        // 手动/外部推送优先：15 秒内不被 WorkBuddy 自动监视的更新覆盖
        Interlocked.Exchange(ref _manualUntilMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 15_000);

        var single = ToSingleLine(text, done ? "任务完成" : "正在执行任务…");

        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (done)
                {
                    Island().ShowDone(single);
                }
                else
                {
                    Island().ShowRunning(single);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Island", "显示任务状态失败", $"{ex.GetType().Name}: {ex.Message}");
            }
        }));
    }

    /// <summary>
    /// 自动读取 WorkBuddy 任务进度：监视本地会话记录（.workbuddy\projects\**\*.jsonl），
    /// AI 每一步工具调用实时上岛；回合结束报完成态（绿勾 + 完成提示音）。
    /// </summary>
    private void StartWorkBuddyWatch()
    {
        if (!_config.Current.WorkBuddyWatchEnabled)
        {
            _log.Info("Island", "WorkBuddy 任务监视未启用（配置关闭）");
            return;
        }

        try
        {
            _workbuddy = new WorkBuddyWatcher(_log);
            _workbuddy.TaskChanged += (text, done) => _dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // 手动推送优先窗口内不覆盖
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < Volatile.Read(ref _manualUntilMs))
                    {
                        return;
                    }

                    if (done)
                    {
                        Island().ShowDone(ToSingleLine(text, "任务完成"));
                    }
                    else
                    {
                        Island().ShowRunning(ToSingleLine(text, "正在执行任务…"));
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("Island", "显示 WorkBuddy 自动状态失败", $"{ex.GetType().Name}: {ex.Message}");
                }
            }));
            _workbuddy.Start();
        }
        catch (Exception ex)
        {
            _log.Warn("Island", "启动 WorkBuddy 任务监视失败", ex.Message);
        }
    }

    /// <summary>媒体快捷控制（来自岛内按钮或 --media 命令）。</summary>
    public void MediaCommand(string action)
    {
        switch (action)
        {
            case "play":
            case "pause":
            case "toggle":
                _ = _media.TogglePlayPauseAsync();
                break;
            case "next":
                _ = _media.NextAsync();
                break;
            case "prev":
            case "previous":
                _ = _media.PreviousAsync();
                break;
            default:
                _log.Warn("Island", "未知媒体命令", action);
                break;
        }
    }

    private IslandWindow Island()
    {
        if (_island is not null)
        {
            return _island;
        }

        var island = new IslandWindow();
        island.ModeProvider = () => _config.Current.IslandMode;
        island.MusicEnabledProvider = () => _config.Current.MusicEnabled;
        island.SavedPositionProvider = () =>
        {
            var config = _config.Current;
            return config.IslandLeft.HasValue && config.IslandTop.HasValue
                ? (config.IslandLeft.Value, config.IslandTop.Value)
                : null;
        };

        island.PositionCommitted += (left, top) => SavePosition(left, top);

        // 任务完成提示音（与消息提示音完全独立）
        island.TaskDoneShown += (_, _) =>
        {
            try
            {
                _sound.PlayTaskDone(_config.Current);
            }
            catch (Exception ex)
            {
                _log.Warn("Island", "播放任务完成提示音失败", ex.Message);
            }
        };

        island.PlayPauseRequested += () => _ = _media.TogglePlayPauseAsync();
        island.NextRequested += () => _ = _media.NextAsync();
        island.PreviousRequested += () => _ = _media.PreviousAsync();

        _island = island;
        return island;
    }

    /// <summary>把任意状态文本压成“一行实时动作”：只取首行、压缩空白、最多 90 字。</summary>
    public static string ToSingleLine(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var firstLine = text.Replace("\r", string.Empty).Split('\n')[0].Trim();

        // 压平连续空白（进度条/表格之类会带一堆空格）
        while (firstLine.Contains("  ", StringComparison.Ordinal))
        {
            firstLine = firstLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (firstLine.Length == 0)
        {
            return fallback;
        }

        return firstLine.Length > 90 ? firstLine[..90] + "…" : firstLine;
    }

    /// <summary>拖拽结束：位置写入配置自动保存。</summary>
    private void SavePosition(double left, double top)
    {
        try
        {
            var next = _config.Current.Clone();
            next.IslandLeft = Math.Round(left, 1);
            next.IslandTop = Math.Round(top, 1);
            _config.Save(next);
            _log.Info("Island", "灵动岛位置已保存", $"{left:F0},{top:F0}");
        }
        catch (Exception ex)
        {
            _log.Warn("Island", "保存灵动岛位置失败", ex.Message);
        }
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Island().ApplyModeChange();
            }
            catch (Exception ex)
            {
                _log.Warn("Island", "应用灵动岛配置失败", ex.Message);
            }
        }));
    }

    public void Dispose()
    {
        _config.Changed -= OnConfigChanged;
        _media.Dispose();
        _fullscreen.Dispose();
        _workbuddy?.Dispose();

        if (Current == this)
        {
            Current = null;
        }
    }
}
