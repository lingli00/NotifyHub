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
    private readonly NtfyPublisher _publisher;
    private readonly LyricsService _lyrics;
    private readonly DispatcherTimer _lyricTimer;
    private WorkBuddyWatcher? _workbuddy;

    // 歌词状态：当前曲目的时间轴 + 已展示行号（行号不变就不刷新 UI）
    private IReadOnlyList<LyricLine>? _lyricLines;
    private string _lyricKey = string.Empty;
    private int _lyricIndex = int.MinValue;
    private long _manualUntilMs; // 手动 --island 推送后的 15 秒内，自动监视不覆盖
    private IslandWindow? _island;
    private bool _started;

    public IslandController(LogService log, ConfigService config, Dispatcher dispatcher, SoundService sound)
    {
        _log = log;
        _config = config;
        _dispatcher = dispatcher;
        _sound = sound;
        _publisher = new NtfyPublisher(log);
        _lyrics = new LyricsService(log);

        _media = new MediaPlayerMonitor(log);
        _fullscreen = new FullscreenWatcher(log);

        // 歌词同步：250ms 问一次播放进度，行号变化才刷新岛（避免每帧重排跑马灯）
        _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _lyricTimer.Tick += (_, _) => SyncLyricLine();
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
                OnMediaForLyrics(info);
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

        // 需求 3：灵动岛内显示的中文过程信息原样下行推送到手机端（内容与岛内完全一致）
        PushDown(single);

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
    /// 下行推送（v0.2.4 需求 3）：把灵动岛里 WorkBuddy 正在执行的中文过程信息
    /// 原样推到手机端。未配置地址时静默跳过；失败只记日志不打扰。
    /// </summary>
    private void PushDown(string singleLineText)
    {
        try
        {
            if (!_config.Current.WorkBuddyWatchEnabled)
            {
                return;
            }

            _publisher.Send(_config.Current.NtfyPushUrl, singleLineText);
        }
        catch (Exception ex)
        {
            _log.Warn("Island", "下行推送失败", ex.Message);
        }
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

                    var single = ToSingleLine(text, done ? "任务完成" : "正在执行任务…");

                    // 需求 3：与岛内显示完全一致的中文过程信息，原样下行推送到手机端
                    PushDown(single);

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
        island.MarqueeSpeedProvider = () => _config.Current.MarqueeSpeed;
        island.LyricsEnabledProvider = () => _config.Current.LyricsEnabled;
        island.ApplyThemeWithBackdrop(_config.Current);
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

    /// <summary>
    /// 曲目变化 → 重新取歌词（同曲走缓存，不重复请求）。
    /// 换歌瞬间先收起旧词，避免上一首的歌词短暂串到新歌上。
    /// </summary>
    private void OnMediaForLyrics(MediaInfo? info)
    {
        var key = info is null ? string.Empty : $"{info.Title}|{info.Artist}";
        if (string.Equals(key, _lyricKey, StringComparison.Ordinal))
        {
            return;
        }

        _lyricKey = key;
        _lyricLines = null;
        _lyricIndex = int.MinValue;

        if (info is null || string.IsNullOrWhiteSpace(info.Title) || !_config.Current.LyricsEnabled)
        {
            _lyricTimer.Stop();
            Island().ShowLyrics(null, null);
            return;
        }

        Island().ShowLyrics(null, null);
        _ = LoadLyricsAsync(info.Title, info.Artist);
    }

    private async Task LoadLyricsAsync(string title, string artist)
    {
        var key = $"{title}|{artist}";

        IReadOnlyList<LyricLine>? lines;
        try
        {
            lines = await _lyrics.GetAsync(title, artist).ConfigureAwait(false);
        }
        catch
        {
            lines = null;
        }

        _ = _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!string.Equals(key, _lyricKey, StringComparison.Ordinal))
            {
                return; // 已经换歌，丢弃过期结果
            }

            _lyricLines = lines;
            _lyricIndex = int.MinValue;

            if (lines is { Count: > 0 })
            {
                SyncLyricLine();
                _lyricTimer.Start();
            }
            else
            {
                _lyricTimer.Stop();
                Island().ShowLyrics(null, null);
            }
        }));
    }

    /// <summary>按播放进度切换歌词行；行号没变就不动 UI（跑马灯不重排）。</summary>
    private void SyncLyricLine()
    {
        if (_lyricLines is not { Count: > 0 })
        {
            _lyricTimer.Stop();
            return;
        }

        var position = _media.GetPositionSeconds();
        if (position < 0)
        {
            return;
        }

        var index = LyricsService.IndexAt(_lyricLines, position);
        if (index == _lyricIndex)
        {
            return;
        }

        _lyricIndex = index;
        Island().ShowLyrics(TextAt(index), TextAt(index + 1));
    }

    private string? TextAt(int index)
        => _lyricLines is { } lines && index >= 0 && index < lines.Count ? lines[index].Text : null;

    /// <summary>当前展示的第 n 行（1=当前行，2=下一行），配置变更后重放一次用。</summary>
    private string? CurrentLyricText(int offset) => TextAt(_lyricIndex + offset - 1);

    /// <summary>拖拽结束：位置写入配置自动保存。</summary>
    private void SavePosition(double left, double top)    {
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

                // v0.2.6：主题（纯色/毛玻璃 + 文字配色）保存后立即生效
                Island().ApplyThemeWithBackdrop(config);
                Island().ShowLyrics(CurrentLyricText(1), CurrentLyricText(2));

                // 音乐开关/跑马灯速度等变更即时生效：
                // 需求 8——此前只调 ApplyModeChange，音乐组可见性不会重算，
                // 表现为“取消勾选保存后不消失、重新勾选反而消失”。
                Island().RefreshMedia();
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
        _lyricTimer.Stop();
        _media.Dispose();
        _fullscreen.Dispose();
        _workbuddy?.Dispose();
        _publisher.Dispose();

        if (Current == this)
        {
            Current = null;
        }
    }
}
