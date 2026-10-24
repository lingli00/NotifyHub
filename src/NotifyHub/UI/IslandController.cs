using System.Runtime.CompilerServices;
using System.Windows;
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

    /// <summary>进程在线巡检（v0.2.11 需求 2）：播放器/WorkBuddy 一打开就切信息展示模式。</summary>
    private readonly PresenceWatcher _presence;
    private readonly LyricsService _lyrics;
    private readonly DispatcherTimer _lyricTimer;

    /// <summary>歌词看门狗（每 2 秒）：补取词、补启动跟随、补定行号。</summary>
    private readonly DispatcherTimer _lyricWatch;
    private WorkBuddyWatcher? _workbuddy;

    // ---------------- 歌词状态（v0.2.9 重做，需求 4/5） ----------------
    // 这一组字段是"歌词为什么不显示"的全部真相来源，诊断日志会一起打出来。

    /// <summary>当前曲目的时间轴（null = 还没取到/没有）。</summary>
    private IReadOnlyList<LyricLine>? _lyricLines;

    /// <summary>当前曲目的键（歌名|歌手）；换歌时变化。</summary>
    private string _lyricKey = string.Empty;

    /// <summary>已展示行号（行号不变就不刷新 UI）。</summary>
    private int _lyricIndex = int.MinValue;

    /// <summary>本曲已经尝试取词的次数（上限 3 次，之后认定"确实没有"）。</summary>
    private int _lyricAttempts;

    /// <summary>上次取词尝试的时刻（用于按间隔重试）。</summary>
    private DateTime _lyricLastAttemptUtc = DateTime.MinValue;

    /// <summary>最近一次拿到的媒体信息（看门狗用它判断"有没有歌、歌名是什么"）。</summary>
    private MediaInfo? _currentMedia;

    /// <summary>看门狗节流：连续取不到进度时的计数。</summary>
    private int _playbackMisses;

    /// <summary>歌词取词最多尝试次数（超过就认为这首确实没词）。</summary>
    private const int MaxLyricAttempts = 3;

    /// <summary>两次取词尝试之间的最小间隔（秒）。</summary>
    private const int LyricRetryIntervalSeconds = 8;

    /// <summary>歌词轮询间隔（毫秒，v0.2.9：200 → 90，需求 5 要求切换误差 ≤300ms）。</summary>
    private const int LyricTickMs = 90;

    private long _manualUntilMs; // 手动 --island 推送后的 15 秒内，自动监视不覆盖

    /// <summary>
    /// 设置页"岛总长度 / 左右比例"滑块的实时预览配置（v0.2.9 需求 6）。
    /// 拖动过程中只改这个内存副本，不动 <see cref="ConfigService.Current"/>——
    /// 用户取消/直接关窗时不会把半成品写进配置文件。
    /// </summary>
    private AppConfig? _preview;

    private IslandWindow? _island;
    private bool _started;

    public IslandController(LogService log, ConfigService config, Dispatcher dispatcher, SoundService sound)
    {
        _log = log;
        _config = config;
        _dispatcher = dispatcher;
        _sound = sound;
        _publisher = new NtfyPublisher(log);
        // v0.2.12 需求 1：歌词服务要读配置（本地优先 / 联网补词开关 / 额外歌词目录）
        _lyrics = new LyricsService(log, () => _config.Current);

        _media = new MediaPlayerMonitor(log);
        _fullscreen = new FullscreenWatcher(log);
        _presence = new PresenceWatcher(log);

        // 歌词同步：90ms 问一次播放进度，行号变化才刷新岛（避免每帧重排跑马灯）。
        // 进度本身由 MediaPlayerMonitor 的 PlaybackClock 做墙钟外推（含上报年龄补偿），
        // 因此这里的间隔只影响"换句延迟"，不影响准确性。
        // v0.2.9 需求 5：200ms → 90ms，把换句误差压进 300ms 以内。
        _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LyricTickMs) };
        _lyricTimer.Tick += (_, _) => SyncLyricLine();

        _lyricWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _lyricWatch.Tick += (_, _) => OnLyricWatchdog();
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

        // v0.2.11 需求 2：进程在线巡检（1.5 秒一次，只在状态变化时回调）
        _presence.Changed += (musicRunning, workBuddyRunning) => _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Island().SetPresence(musicRunning, workBuddyRunning, _presence.MusicProcessName);
            }
            catch (Exception ex)
            {
                _log.Warn("Island", "更新进程在线状态失败", ex.Message);
            }
        }));

        _presence.Start();

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

        // 歌词看门狗：常驻跑着，成本是每 2 秒一次字典查询 + 几次字段判断
        _lyricWatch.Start();

        // v0.2.9 需求 2：启动即常驻——此时没有音乐也没有任务，先把顶部窄条亮出来，
        // 用户能一眼确认程序在跑；有内容时它会自己展开。
        _ = _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Island().ShowPersistentStrip();
            }
            catch (Exception ex)
            {
                _log.Warn("Island", "启动常驻细条失败", ex.Message);
            }
        }));

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
    public void ShowTask(string text, bool done, string? tag = null)
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
                    Island().ShowDone(single, tag);
                }
                else
                {
                    Island().ShowRunning(single, tag);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Island", "显示任务状态失败", $"{ex.GetType().Name}: {ex.Message}");
            }
        }));
    }

    /// <summary>
    /// 新消息到达（v0.2.7 需求 2）：把灵动岛弹出来并按用户设定的时长自动收纳。
    /// 由 App 在通知真正上屏时调用（被过滤/去重的消息不会走到这里）。
    /// </summary>
    public void PokeMessage(NotifyMessage message)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Island().PokeMessage(SourceName(message.Source), message.Sender, message.Content);
            }
            catch (Exception ex)
            {
                _log.Warn("Island", "新消息弹出灵动岛失败", ex.Message);
            }
        }));
    }

    /// <summary>
    /// 新消息到达（v0.2.9 需求 3 的展示策略入口）。
    ///
    /// 返回 true = 已在岛内展示（调用方不要再弹四角卡片）；
    /// 返回 false = 交给四角弹窗（音乐与任务同时存在，岛内已经没有干净的位置）。
    /// </summary>
    public bool TryShowNoticeOnIsland(NotifyMessage message)
    {
        try
        {
            var island = _island;
            if (island is null)
            {
                // 岛还没建（启动最初几秒）：老实用四角弹窗，别让通知凭空消失
                return false;
            }

            if (!_dispatcher.CheckAccess())
            {
                var box = new StrongBox<bool>(false);
                _dispatcher.Invoke(() => box.Value = TryShowNoticeOnIsland(message));
                return box.Value;
            }

            // 音乐 + 任务同时跑 → 岛内已有两行内容，通知走四角（需求 3 明确要求）
            if (!IslandWindow.NoticeGoesToIsland(island.HasMusicContent, island.HasTaskContent))
            {
                _log.Write(
                    LogLevel.Debug,
                    "Island",
                    "通知走四角弹窗（音乐+任务同时在跑）",
                    $"{message.SourceText} {message.Sender}");
                return false;
            }

            island.ShowNotice(message);
            _sound.PlayMessage(_config.Current);
            _log.Info("通知", "岛内展示", $"{message.SourceText} · {message.Sender}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("Island", "岛内展示通知失败（回退四角弹窗）", ex.Message);
            return false;
        }
    }

    /// <summary>岛内通知被点击：打开对应会话 / 网页（与四角卡片的点击行为保持一致）。</summary>
    private void OnIslandNoticeClicked(object? sender, NotifyMessage message)
    {
        try
        {
            if (message.Source == MessageSource.Ntfy && !string.IsNullOrWhiteSpace(message.LinkUrl))
            {
                _log.AddEvent(message, EventStatus.Info, "岛内点击：打开 topic 页面");
                BrowserLauncher.Open(_config.Current.BrowserPath, message.LinkUrl);
                return;
            }

            _log.AddEvent(message, EventStatus.Info, "岛内点击定位");
            var log = _log;
            Task.Run(() => WindowActivator.TryOpenConversation(message, log));
        }
        catch (Exception ex)
        {
            _log.Warn("Island", "打开岛内通知失败", ex.Message);
        }
    }

    private static string SourceName(MessageSource source) => source switch
    {
        MessageSource.WeChat => "微信",
        MessageSource.QQ => "QQ",
        MessageSource.Ntfy => "ntfy",
        _ => "消息",
    };

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
                        Island().ShowDone(single, "WorkBuddy");
                    }
                    else
                    {
                        Island().ShowRunning(single, "WorkBuddy");
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

        // 立刻登记：下面的 SetPresence / ApplyTheme 都会同步触发 Show/Evaluate，
        // 那些回调里如果再问 Island()，`_island` 还没赋值就会**再创建一个窗口**
        // （实测：日志里同一秒出现 5 条"进程在线状态更新"＝ 5 个岛实例，屏幕上就是几块幽灵矩形）。
        _island = island;

        island.ModeProvider = () => _config.Current.IslandMode;
        island.MusicEnabledProvider = () => _config.Current.MusicEnabled;
        island.MarqueeSpeedProvider = () => _config.Current.MarqueeSpeed;
        island.LyricsEnabledProvider = () => _config.Current.LyricsEnabled;
        island.LyricsFontSizeProvider = () => _config.Current.LyricsFontSize;
        island.AutoCollapseEnabledProvider = () => _config.Current.AutoCollapseEnabled;
        island.AutoCollapseSecondsProvider = () => _config.Current.AutoCollapseSeconds;
        island.KeepOpenWhilePlayingProvider = () => _config.Current.KeepOpenWhilePlaying;

        // v0.2.9 需求 6：两个滑块（拖的时候读预览副本，松手保存后读真配置）
        island.IslandWidthPercentProvider = () => (_preview ?? _config.Current).IslandWidthPercent;
        island.IslandSplitRatioProvider = () => (_preview ?? _config.Current).IslandSplitRatio;

        // 字号依赖 provider，必须在注入之后刷一次（构造函数里读不到配置）
        island.ApplyLyricsTypography();
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

        island.PlayPauseRequested += () =>
        {
            // v0.2.14 需求 2：播放器还没注册 SMTC 会话时（冷启动后用户手动点播放之前），
            // 指令会被挂起等会话——这时把岛继续撑住，用户才能再按一次/按别的键，
            // 不会一松鼠标岛就收成窄条、按钮再也点不到。
            if (!_media.IsAvailable || !_media.HasActiveSession)
            {
                island.NoteUserLaunch();
            }

            _ = _media.TogglePlayPauseAsync();
        };

        island.NextRequested += () => _ = _media.NextAsync();
        island.PreviousRequested += () => _ = _media.PreviousAsync();

        // v0.2.9 需求 3：岛内通知点击 → 打开对应会话 / 网页
        island.NoticeClicked += OnIslandNoticeClicked;

        // v0.2.10 需求 4：岛从"隐藏/窄条"重新出现 → 重定歌词行 + 重启滚动。
        // 收起期间窗口是 Hide 的，UI 不渲染，跑马灯与行号可能停在半路；
        // 不补这一刀就会出现"展开了但歌词还是上一句 / 不跟着滚动"。
        island.IslandBecameVisible += (_, _) => ResyncLyricsAfterShow();

        // 首帧就把"谁在线"告诉岛，避免启动后头一秒还是待机条
        island.SetPresence(_presence.MusicRunning, _presence.WorkBuddyRunning, _presence.MusicProcessName);

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
    ///
    /// v0.2.9 需求 4：这里不再"一次定生死"。旧版把 <c>_lyricKey</c> 一设，
    /// 后续除非换歌就再也不取词——如果那一刻歌名还没就绪、或歌词开关刚被关掉，
    /// 这首歌就永远没有歌词。现在把「状态重置」和「是否发起请求」拆开，
    /// 由 <see cref="EnsureLyricsRequested"/> + 看门狗反复兜底。
    /// </summary>
    private void OnMediaForLyrics(MediaInfo? info)
    {
        var previousKey = _lyricKey;
        var key = info is null ? string.Empty : $"{info.Title}|{info.Artist}";

        _currentMedia = info;

        if (string.Equals(key, previousKey, StringComparison.Ordinal))
        {
            // 同一首歌：只可能"上次没取到"需要补一刀，交给 EnsureLyricsRequested 判断
            EnsureLyricsRequested();
            return;
        }

        _lyricKey = key;
        _lyricLines = null;
        _lyricIndex = int.MinValue;
        _lyricAttempts = 0;
        _lyricLastAttemptUtc = DateTime.MinValue;
        _playbackMisses = 0;

        if (info is null || string.IsNullOrWhiteSpace(info.Title))
        {
            _lyricTimer.Stop();
            Island().ShowLyrics(null, null);
            return;
        }

        // 关掉歌词总开关时连取词都不做（省流量，也绝不留占位）
        if (!_config.Current.LyricsEnabled)
        {
            _lyricTimer.Stop();
            Island().ShowLyrics(null, null);
            return;
        }

        Island().ShowLyrics(null, null);
        EnsureLyricsRequested();
    }

    /// <summary>
    /// 幂等地"确保这首歌的歌词正在取/已取到"。
    ///
    /// 触发时机：换歌、开关被重新打开、看门狗每 2 秒巡检、进度连续读不到时。
    /// 只在以下条件全部满足时才真正发请求：
    ///   * 有一首有名字的歌；
    ///   * 歌词总开关是开的；
    ///   * 还没拿到时间轴；
    ///   * 本曲尝试次数 &lt; 3；
    ///   * 距上次尝试 ≥ 8 秒（避免网络卡住时把接口打爆）。
    /// </summary>
    private void EnsureLyricsRequested()
    {
        var info = _currentMedia;
        if (info is null || string.IsNullOrWhiteSpace(info.Title))
        {
            return;
        }

        if (!_config.Current.LyricsEnabled)
        {
            return;
        }

        if (_lyricLines is { Count: > 0 })
        {
            return;
        }

        // 永久缓存为"没有歌词"的歌（确实搜不到/纯音乐）不再重试
        if (_lyrics.IsKnownNoLyric(info.Title, info.Artist))
        {
            return;
        }

        if (_lyricAttempts >= MaxLyricAttempts)
        {
            return;
        }

        if ((DateTime.UtcNow - _lyricLastAttemptUtc).TotalSeconds < LyricRetryIntervalSeconds
            && _lyricAttempts > 0)
        {
            return;
        }

        _lyricAttempts++;
        _lyricLastAttemptUtc = DateTime.UtcNow;

        _log.Write(
            LogLevel.Debug,
            "Lyrics",
            $"发起取词 第 {_lyricAttempts}/{MaxLyricAttempts} 次: {info.Title} - {info.Artist}");

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

            if (lines is { Count: > 0 })
            {
                _lyricLines = lines;
                _lyricIndex = int.MinValue;
                _playbackMisses = 0;

                // 立刻按当前进度定一次行号，然后开始跟随
                SyncLyricLine();

                if (!_lyricTimer.IsEnabled)
                {
                    _lyricTimer.Start();
                }

                _log.Write(
                    LogLevel.Debug,
                    "Lyrics",
                    $"歌词上屏: {title}（{lines.Count} 行，当前行={_lyricIndex}）");
                return;
            }

            // 这一轮没拿到：不清空已有时间轴（可能是重试失败），也不停 timer 太久。
            // 看门狗会在 8 秒后再试；三次都失败就认了（多半是这首确实没有词或没网）。
            _lyricLines = null;
            Island().ShowLyrics(null, null);

            _log.Write(
                LogLevel.Debug,
                "Lyrics",
                $"本轮无词（第 {_lyricAttempts}/{MaxLyricAttempts} 次）: {title} - {artist}");

            // 若还有重试机会，保持 timer 关闭（没有时间轴可跟），由看门狗唤醒
            if (_lyricAttempts >= MaxLyricAttempts)
            {
                _lyricTimer.Stop();
            }
        }));
    }

    /// <summary>
    /// 按播放进度切换歌词行；行号没变就不动 UI（跑马灯不重排）。
    ///
    /// v0.2.9 需求 5：
    ///   * 进度取自 TryGetPlayback（PlaybackClock 外推 + 上报年龄补偿），不再有固定滞后；
    ///   * 轮询 90ms，切换发生在"目标行开始时间 + 90ms 内"，满足 ≤300ms 的要求；
    ///   * 每次切换打一条 Debug 日志（目标时间 / 实际进度 / 偏差），便于实测核对。
    /// </summary>
    private void SyncLyricLine(bool force = false)
    {
        if (_lyricLines is not { Count: > 0 })
        {
            return; // 等看门狗补词，不要在这里停 timer（停了就再也不会恢复）
        }

        if (!_media.TryGetPlayback(out var position, out _, out _))
        {
            // 会话暂时读不到（切歌间隙 / 播放器退出）：连续多次读不到就暂停轮询并请看门狗介入
            if (++_playbackMisses >= 30)
            {
                _playbackMisses = 0;
                _lyricTimer.Stop();
                EnsureLyricsRequested();
            }

            return;
        }

        _playbackMisses = 0;

        // 用户级时间微调（v0.2.12 需求 1）：个别播放器/歌词源有固有偏差，
        // 正数 = 歌词提前。夹在合法范围内，绝不接受离谱值。
        var offsetMs = Math.Clamp(
            _config.Current.LyricOffsetMs, AppConfig.MinLyricOffsetMs, AppConfig.MaxLyricOffsetMs);
        if (offsetMs != 0)
        {
            position = Math.Max(0, position + (offsetMs / 1000.0));
        }

        var index = LyricsService.IndexAt(_lyricLines, position);
        if (index == _lyricIndex && !force)
        {
            return;
        }

        _lyricIndex = index;

        // 偏差诊断：目标行起始时间 vs 实际切到这一刻的进度（正数=晚切）。
        // 只记"异常偏差"（>400ms）：正常换句的 90ms 内偏差每句都记一遍就是流水（v0.2.12 需求 5）。
        if (index >= 0 && index < _lyricLines.Count)
        {
            var target = _lyricLines[index].Time.TotalSeconds;
            var delta = (position - target) * 1000;
            if (Math.Abs(delta) > 400)
            {
                _log.Write(
                    LogLevel.Debug,
                    "Lyrics",
                    $"换句 #{index} 偏差 {delta:F0}ms",
                    $"pos={position:F3}s target={target:F3}s text={_lyricLines[index].Text}");
            }
        }

        Island().ShowLyrics(TextAt(index), TextAt(index + 1), TransAt(index));
    }

    /// <summary>
    /// 岛重新可见后重放一次歌词状态（v0.2.10 需求 4）。
    ///
    /// 只做三件事，都是"补状态"而不是"改策略"，所以常驻/非常驻两种模式行为一致：
    ///   1. 跟随 timer 没跑就拉起来（收起期间被停过的情况）；
    ///   2. 按当前播放进度**强制**重定一次行号（force=true 会跳过"行号没变就返回"）；
    ///   3. 让三行跑马灯重新起跑（清 `_scrolling` / `_offset`）。
    /// </summary>
    private void ResyncLyricsAfterShow()
    {
        try
        {
            _log.Write(
                LogLevel.Debug,
                "Lyrics",
                "展开后重定歌词",
                $"时间轴={( _lyricLines is { Count: > 0 } ? _lyricLines.Count + " 行" : "无")} " +
                $"行号={_lyricIndex} timer={_lyricTimer.IsEnabled} 歌={_currentMedia?.Title ?? "-"}");

            if (_lyricLines is { Count: > 0 })
            {
                if (!_lyricTimer.IsEnabled)
                {
                    _lyricTimer.Start();
                }

                SyncLyricLine(force: true);

                _log.Write(
                    LogLevel.Debug,
                    "Lyrics",
                    "重定行完成",
                    $"行={_lyricIndex}/{_lyricLines.Count}");
            }
            else
            {
                // 还没有时间轴：给一次补取机会（看门狗 2s 内也会再兜一次）
                EnsureLyricsRequested();
            }

            Island().RestartLyricScroll();
        }
        catch (Exception ex)
        {
            _log.Warn("Lyrics", "展开后重定歌词失败", ex.Message);
        }
    }

    /// <summary>
    /// 歌词看门狗：每 2 秒巡检一次。它兜住的是"所有偶发"：
    ///   · 取词失败后没人再来一次 → 补一次（有次数与间隔上限）；
    ///   · 时间轴有了但 timer 没跑 → 重新启动；
    ///   · 有歌在播、歌词开着、行号却一直没定 → 重新定一次；
    ///   · 开关从关到开（用户中途改设置）→ 重新取。
    /// </summary>
    private void OnLyricWatchdog()
    {
        var info = _currentMedia;
        var enabled = _config.Current.LyricsEnabled;

        if (info is null || string.IsNullOrWhiteSpace(info.Title))
        {
            return;
        }

        if (!enabled)
        {
            return;
        }

        if (_lyricLines is { Count: > 0 })
        {
            // 有时间轴：确保跟随 timer 在跑
            if (!_lyricTimer.IsEnabled)
            {
                _lyricTimer.Start();
            }

            if (_lyricIndex == int.MinValue)
            {
                SyncLyricLine();
            }

            return;
        }

        // 没时间轴：按退避策略补取
        EnsureLyricsRequested();
    }

    private string? TextAt(int index)
        => _lyricLines is { } lines && index >= 0 && index < lines.Count ? lines[index].Text : null;

    /// <summary>当前行的译文（歌词源自带才有；没有返回 null，岛上那一行就不占位）。</summary>
    private string? TransAt(int index)
    {
        if (_lyricLines is not { } lines || index < 0 || index >= lines.Count)
        {
            return null;
        }

        var line = lines[index];
        return line.HasTranslation ? line.Translation : null;
    }

    /// <summary>当前展示的第 n 行（1=当前行，2=下一行），配置变更后重放一次用。</summary>
    private string? CurrentLyricText(int offset) => TextAt(_lyricIndex + offset - 1);

    private string? CurrentLyricTrans() => TransAt(_lyricIndex);

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

    /// <summary>
    /// 设置页滑块的实时预览（v0.2.9 需求 6）：用内存副本立刻重排岛，不落盘。
    /// 岛当前在细条里也没关系——会先把岛弹出来，用户能直接看到宽度变化。
    /// </summary>
    public void PreviewLayout(AppConfig preview)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _preview = preview;

                var island = _island;
                if (island is null)
                {
                    return;
                }

                // 先让岛有内容可看（没内容时宽度是"细条"的宽度，看不出滑块效果）
                island.PokeActivity();
                island.ApplyLyricsTypography();
                island.RefreshMedia();
            }
            catch (Exception ex)
            {
                _log.Warn("Island", "预览岛布局失败", ex.Message);
            }
        }));
    }

    /// <summary>当前灵动岛窗口矩形（DIP）；岛没建或不可见返回 null。Bug 快照记录用。</summary>
    public Rect? IslandBounds
    {
        get
        {
            var island = _island;
            if (island is null || island.ActualWidth < 1)
            {
                return null;
            }

            return new Rect(island.Left, island.Top, island.ActualWidth, island.ActualHeight);
        }
    }

    /// <summary>配置真正保存后清掉预览态，后续读真配置（v0.2.9 需求 6）。</summary>
    public void ClearPreview() => _preview = null;

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _preview = null; // 保存后以真配置为准

                Island().ApplyModeChange();

                // v0.2.6：主题（纯色/毛玻璃 + 文字配色）保存后立即生效
                Island().ApplyThemeWithBackdrop(config);

                // v0.2.7：歌词字号 / 总开关 / 收纳时长保存后立即生效
                Island().ApplyLyricsTypography();

                // v0.2.9 需求 4：歌词开关从关到开时，重置取词退避并立刻补一次
                // （否则旧 key 上"没取到"的状态会一直挂着，表现就是"开关开了也不出词"）
                if (config.LyricsEnabled && _lyricLines is null)
                {
                    _lyricAttempts = 0;
                    _lyricLastAttemptUtc = DateTime.MinValue;
                    _playbackMisses = 0;
                    EnsureLyricsRequested();
                }

                Island().ShowLyrics(CurrentLyricText(1), CurrentLyricText(2), CurrentLyricTrans());

                // 音乐开关/跑马灯速度等变更即时生效：
                // 需求 8——此前只调 ApplyModeChange，音乐组可见性不会重算，
                // 表现为“取消勾选保存后不消失、重新勾选反而消失”。
                Island().RefreshMedia();
                Island().PokeActivity();
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
        _lyricWatch.Stop();
        _media.Dispose();
        _fullscreen.Dispose();
        _workbuddy?.Dispose();
        _publisher.Dispose();
        _presence.Dispose();

        // v0.2.15 需求 3：退出时把岛（以及它持有的细条）一起关掉。
        // OnClosed 里已经 Stop 掉所有计时器并 Dispose 细条，这里只负责"别再留着窗口"。
        try
        {
            _island?.Close();
        }
        catch
        {
            // 窗口可能已被关闭
        }

        _island = null;

        if (Current == this)
        {
            Current = null;
        }
    }
}
