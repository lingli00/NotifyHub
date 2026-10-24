using System.Diagnostics;
using Windows.Media.Control;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 当前媒体会话快照（SMTC 聚合，任意线程回调）。
/// <paramref name="AppId"/> 是 SMTC 的 SourceAppUserModelId（exe 路径或 UWP 包名），
/// <paramref name="ProcessName"/> 是白名单命中的进程名关键词（供图标提取用）。
/// </summary>
public sealed record MediaInfo(
    string Title,
    string Artist,
    string AppName,
    bool IsPlaying,
    string AppId = "",
    string ProcessName = "",
    double PositionSeconds = 0,
    double DurationSeconds = 0);

/// <summary>
/// 音乐播放器监控：基于 Windows 系统媒体传输控制（SMTC）聚合所有播放器，
/// 但只响应内置音乐 App 名单内的进程——
/// 浏览器（chrome/edge/firefox/brave…）刷视频/直播同样会上报媒体会话，
/// 不在名单内就会被忽略，不会再误弹音乐控制栏（需求 6）。
/// v0.2.4：名单改为内置固定（需求 6 删除用户可配白名单），不再暴露配置入口。
/// 读取播放状态并暴露 播放/暂停/上一首/下一首 控制；断续器事件推送给灵动岛。
/// </summary>
public sealed class MediaPlayerMonitor : IDisposable
{
    /// <summary>
    /// 内置音乐 App 名单（进程名关键词，小写包含匹配，不区分 .exe）。
    /// 覆盖用户点名要求的：QQ音乐、酷狗、网易云、酷我、咪咕、foobar2000、FOLIA；
    /// 另附常见播放器。用户机器上 FOLIA 的进程名已实测确认为 Folia.exe。
    /// </summary>
    public static readonly string[] MusicApps =
    {
        // 用户点名
        "qqmusic",      // QQ音乐（QQMusic.exe）
        "kugou",        // 酷狗音乐（KuGou.exe）
        "cloudmusic",   // 网易云音乐（cloudmusic.exe）
        "kuwo",         // 酷我音乐（kuwo.exe）
        "migu",         // 咪咕音乐（MiguMusic.exe / migu.exe）
        "foobar",       // foobar2000（foobar2000.exe）
        "folia",        // FOLIA（Folia.exe，本机实测：AppData\Local\Programs\Folia\Folia.exe）

        // 常见补充
        "spotify",      // Spotify
        "aimp",         // AIMP
        "hiby",         // 海贝音乐（HiByMusic）
        "soda",         // 汽水音乐
        "musicplayer",  // 部分本地播放器进程名
        "groove",       // Windows 媒体播放器（UWP AUMID 含 groove）
        "zune",         // Windows 媒体播放器（旧 AUMID）
        "itunes",       // iTunes
        "applemusic",   // Apple Music（Win 版）
    };

    /// <summary>名单关键词 → 友好展示名（音乐行显示用）。</summary>
    private static readonly (string Key, string Name)[] DisplayNames =
    {
        ("qqmusic", "QQ音乐"),
        ("kugou", "酷狗音乐"),
        ("cloudmusic", "网易云音乐"),
        ("kuwo", "酷我音乐"),
        ("migu", "咪咕音乐"),
        ("foobar", "foobar2000"),
        ("folia", "FOLIA"),
        ("spotify", "Spotify"),
        ("aimp", "AIMP"),
        ("hiby", "海贝音乐"),
        ("soda", "汽水音乐"),
        ("groove", "媒体播放器"),
        ("zune", "媒体播放器"),
        ("itunes", "iTunes"),
        ("applemusic", "Apple Music"),
    };
    private readonly LogService _log;
    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = new();
    private readonly PlaybackClock _clock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private MediaInfo? _last;
    private int _refreshing;
    private string _trackKey = string.Empty;

    /// <summary>换曲时刻（Stopwatch tick，v0.2.13）：用来识别"切歌瞬间播放器还报上一首进度"。</summary>
    private long _trackChangedAtTicks;

    /// <summary>
    /// 本次连续播放的起点（<see cref="Stopwatch.GetTimestamp"/>）。
    /// 仅用于"播放器不提供 timeline"时的位置兜底，暂停/停止归零。
    /// </summary>
    private long _playingSinceTicks;

    public MediaPlayerMonitor(LogService log) => _log = log;

    /// <summary>媒体状态变化（任意线程回调，订阅方负责切 UI 线程）。null=无音乐播放。</summary>
    public event Action<MediaInfo?>? MediaChanged;

    /// <summary>SMTC 是否初始化成功。</summary>
    public bool IsAvailable => _manager is not null;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            _log.Info("Media", "SMTC 媒体监控已就绪（仅响应内置音乐 App 名单）");
            ScheduleRefresh();
        }
        catch (Exception ex)
        {
            _log.Warn("Media", "SMTC 初始化失败，音乐控制模块不可用", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_manager is not null)
        {
            try
            {
                _manager.SessionsChanged -= OnSessionsChanged;
            }
            catch
            {
                // 退出时忽略
            }
        }
    }

    /// <summary>
    /// 播放/暂停切换（当前活跃的白名单会话）。
    ///
    /// v0.2.14 需求 2：**冷启动拉起播放器后的几秒内它还没注册 SMTC 会话**，
    /// 这时 <see cref="PickSession"/> 返回 null，旧版直接静默返回——用户看到的
    /// 就是"双击唤起了播放器，但灵动岛上的播放键按了没反应"。
    /// 现在把指令挂起，后台每 300ms 重试，最长等 <see cref="PendingCommandWaitMs"/>。
    /// </summary>
    public async Task TogglePlayPauseAsync()
    {
        var session = PickSession();
        if (session is null)
        {
            QueuePending(PendingKind.Toggle);
            return;
        }

        try
        {
            if (session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                await session.TryPauseAsync();
            }
            else
            {
                await session.TryPlayAsync();
            }

            _log.Info("Media", "已切换播放/暂停", session.SourceAppUserModelId);
        }
        catch (Exception ex)
        {
            _log.Warn("Media", "切换播放/暂停失败", ex.Message);
        }
    }

    public async Task NextAsync()
    {
        var session = PickSession();
        if (session is null)
        {
            QueuePending(PendingKind.Next);
            return;
        }

        try
        {
            await session.TrySkipNextAsync();
            _log.Info("Media", "已切下一首", session.SourceAppUserModelId);
        }
        catch (Exception ex)
        {
            _log.Warn("Media", "切下一首失败", ex.Message);
        }
    }

    public async Task PreviousAsync()
    {
        var session = PickSession();
        if (session is null)
        {
            QueuePending(PendingKind.Previous);
            return;
        }

        try
        {
            await session.TrySkipPreviousAsync();
            _log.Info("Media", "已切上一首", session.SourceAppUserModelId);
        }
        catch (Exception ex)
        {
            _log.Warn("Media", "切上一首失败", ex.Message);
        }
    }

    // ---------------- 冷启动挂起指令（v0.2.14 需求 2） ----------------

    private enum PendingKind
    {
        None,
        Toggle,
        Next,
        Previous,
    }

    /// <summary>
    /// 挂起指令最长等待时间（v0.2.14 实测校准）：原定 6 秒，实测本机 Folia 冷启动后
    /// 要等用户在它自己的界面里点播放才注册 SMTC 会话，可能远超 6 秒。
    /// 放到 20 秒：既覆盖"启动慢"，又不至于让一条失效指令挂太久。
    /// </summary>
    private const int PendingCommandWaitMs = 20_000;

    /// <summary>重试间隔。</summary>
    private const int PendingRetryMs = 300;

    private readonly object _pendingGate = new();
    private PendingKind _pending;
    private long _pendingSinceTicks;
    private int _pendingRunning;

    /// <summary>当前是否拿得到可控制的播放器会话（冷启动后、真正开播前为 false）。</summary>
    public bool HasActiveSession => PickSession() is not null;

    /// <summary>当前是否有等待执行的播放指令（诊断/自检用）。</summary>
    public bool HasPendingCommand
    {
        get
        {
            lock (_pendingGate)
            {
                return _pending != PendingKind.None;
            }
        }
    }

    /// <summary>会话还没就绪：把指令挂起，并启动一次性重试任务。</summary>
    private void QueuePending(PendingKind kind)
    {
        lock (_pendingGate)
        {
            // 后来的指令覆盖前面的（用户连按两下，执行最后一次意图即可）
            _pending = kind;
            _pendingSinceTicks = Stopwatch.GetTimestamp();

            if (_pendingRunning == 1)
            {
                return; // 已有重试循环在跑
            }

            _pendingRunning = 1;
        }

        _log.Info("Media", "播放器会话未就绪，指令挂起", $"{kind}（最多等 {PendingCommandWaitMs / 1000}s）");

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(PendingRetryMs).ConfigureAwait(false);

                    PendingKind kind2;
                    long since;
                    lock (_pendingGate)
                    {
                        if (_pending == PendingKind.None)
                        {
                            return;
                        }

                        kind2 = _pending;
                        since = _pendingSinceTicks;

                        var waitedMs = (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency * 1000;
                        if (waitedMs > PendingCommandWaitMs)
                        {
                            _pending = PendingKind.None;
                            _log.Warn("Media", "播放指令超时放弃", $"{kind2}（等了 {waitedMs:F0}ms 仍无会话）");
                            return;
                        }
                    }

                    var session = PickSession();
                    if (session is null)
                    {
                        continue;
                    }

                    bool ok;
                    try
                    {
                        ok = kind2 switch
                        {
                            PendingKind.Toggle => await ToggleAsync(session).ConfigureAwait(false),
                            PendingKind.Next => await SkipAsync(session, next: true).ConfigureAwait(false),
                            PendingKind.Previous => await SkipAsync(session, next: false).ConfigureAwait(false),
                            _ => false,
                        };
                    }
                    catch
                    {
                        ok = false;
                    }

                    lock (_pendingGate)
                    {
                        // 只有当挂起的还是同一条指令时才清掉（期间用户可能又按了别的）
                        if (_pending == kind2)
                        {
                            _pending = PendingKind.None;
                        }
                    }

                    if (ok)
                    {
                        _log.Info("Media", "挂起指令已执行", $"{kind2}");
                    }

                    return;
                }
            }
            finally
            {
                lock (_pendingGate)
                {
                    _pendingRunning = 0;
                }
            }
        });
    }

    private static async Task<bool> ToggleAsync(GlobalSystemMediaTransportControlsSession session)
    {
        if (session.GetPlaybackInfo().PlaybackStatus
            == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            return await session.TryPauseAsync();
        }

        return await session.TryPlayAsync();
    }

    private static async Task<bool> SkipAsync(GlobalSystemMediaTransportControlsSession session, bool next)
        => next ? await session.TrySkipNextAsync() : await session.TrySkipPreviousAsync();

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // 轻微延迟合并瞬时连发的多个事件
                await Task.Delay(120);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _log.Warn("Media", "刷新媒体状态失败", ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private async Task RefreshAsync()
    {
        var manager = _manager;
        if (manager is null)
        {
            return;
        }

        // 订阅新增会话（已订阅的跳过）
        foreach (var session in manager.GetSessions())
        {
            Subscribe(session);
        }

        var session0 = PickSession();
        if (session0 is null)
        {
            // 没有白名单内的播放会话：控制栏不出现（需求 6）
            Publish(null);
            return;
        }

        try
        {
            var playing = session0.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var props = await session0.TryGetMediaPropertiesAsync();
            var appId = session0.SourceAppUserModelId ?? string.Empty;

            // v0.2.6 歌词同步需要播放进度（SMTC TimelineProperties）
            var position = 0.0;
            var duration = 0.0;
            try
            {
                var timeline = session0.GetTimelineProperties();
                position = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);
                duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            }
            catch
            {
                // 部分播放器不提供 timeline，忽略（歌词退化为不跟随）
            }

            var info = new MediaInfo(
                props.Title ?? string.Empty,
                props.Artist ?? string.Empty,
                FriendlyName(appId),
                playing,
                appId,
                ProcessNameOf(appId),
                position,
                duration);
            Publish(info);
        }
        catch (Exception ex)
        {
            _log.Warn("Media", "读取媒体属性失败", ex.Message);
        }
    }

    /// <summary>
    /// 读取当前播放进度（秒）。歌词同步用——SMTC 的 MediaChanged 不会随时长推送，
    /// 只能由上层定时来问。取不到返回 -1。
    ///
    /// v0.2.7：改用 <see cref="PlaybackClock"/> 估算。SMTC 的 TimelineProperties.Position
    /// 只是播放器最后一次上报的快照，正常播放期间是**陈旧**的；直接返回它会让歌词
    /// 卡死在某一行（历史 Bug）。这里以它为锚点 + 墙钟外推，得到真实进度。
    /// </summary>
    public double GetPositionSeconds()
        => TryGetPlayback(out var position, out _, out _) ? position : -1;

    /// <summary>
    /// 一次性读取播放进度 / 播放态 / 总时长（歌词同步用）。
    /// 三个值来自同一次会话读取，避免上层分三次调用读到互相矛盾的快照。
    ///
    /// v0.2.9 修掉两个导致「歌词空白 / 歌词滞后一秒」的问题：
    ///   1) 旧版把 <c>GetTimelineProperties</c> 和整体包在同一个 try 里。某些播放器
    ///      （精简版 UWP、部分国产播放器的 SMTC 实现）**不支持 timeline，直接抛异常**，
    ///      于是整个方法返回 false → 上层每秒都拿不到位置 → 歌词永远不显示。
    ///      现在 timeline 读取单独 try，失败也能用墙钟兜底推进，至少让歌词按时间走。
    ///   2) 旧版把 <c>TimelineProperties.Position</c> 当"此刻位置"，但它其实是
    ///      <c>LastUpdatedTime</c> 那一刻的快照，天然落后一个上报周期（常见 0.5~1.5s）。
    ///      现在把「上报年龄」一起交给 <see cref="PlaybackClock"/> 做锚点补偿。
    /// </summary>
    public bool TryGetPlayback(out double positionSeconds, out bool playing, out double durationSeconds)
    {
        positionSeconds = 0;
        playing = false;
        durationSeconds = 0;

        GlobalSystemMediaTransportControlsSession? session;
        try
        {
            session = PickSession();
        }
        catch
        {
            session = null;
        }

        if (session is null)
        {
            _clock.Reset();
            _trackKey = string.Empty;
            _playingSinceTicks = 0;
            return false;
        }

        try
        {
            playing = session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            // 会话刚好失效（播放器退出）：当作暂停处理，不要整条链路返回失败
            playing = false;
        }

        var nowTicks = Stopwatch.GetTimestamp();

        // 播放起点兜底：timeline 完全不可用时，用"本次开始播放的墙钟时刻"外推
        if (playing)
        {
            if (_playingSinceTicks == 0)
            {
                _playingSinceTicks = nowTicks;
            }
        }
        else
        {
            _playingSinceTicks = 0;
        }

        var reported = 0.0;
        var reportAge = 0.0;
        var hasTimeline = false;

        try
        {
            var timeline = session.GetTimelineProperties();
            reported = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);
            durationSeconds = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);

            // 这个位置是"多久以前"的快照（播放中才有意义）
            reportAge = (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;
            hasTimeline = true;
        }
        catch
        {
            // 该播放器不提供 timeline：走下面的墙钟兜底，绝不因此让歌词整段失效
        }

        if (!hasTimeline)
        {
            // 没有任何时间轴信息：按"从开始播放到现在"估算。
            // 用户中途拖动进度会不准，但总比位置恒为 0（歌词卡在第一行）好，
            // 而且这是极少见的播放器，属于兜底路径。
            var fallback = playing && _playingSinceTicks > 0
                ? (nowTicks - _playingSinceTicks) / (double)Stopwatch.Frequency
                : _clock.Position;

            positionSeconds = _clock.Update(fallback, playing, nowTicks, reportAgeSeconds: 0);
            return true;
        }

        // 切歌瞬间播放器常常还没刷新 timeline，报上来的是**上一首**的位置。
        // 真实案例：新歌 0 秒，播放器报 239s → 歌词直接跳到第 29 行（偏差 20.7 秒）。
        if (_trackChangedAtTicks > 0)
        {
            var sinceChange = (nowTicks - _trackChangedAtTicks) / (double)Stopwatch.Frequency;
            if (PlaybackClock.LooksStaleOnTrackChange(reported, sinceChange, durationSeconds))
            {
                _log.Routine(
                    "Media",
                    "忽略切歌瞬间的陈旧进度",
                    $"pos={reported:F1}s dur={durationSeconds:F1}s since={sinceChange:F1}s");

                reported = 0;
                reportAge = 0;
                _clock.Reset();
            }
        }

        positionSeconds = _clock.Update(reported, playing, nowTicks, reportAge);
        return true;
    }

    private void Subscribe(GlobalSystemMediaTransportControlsSession session)
    {
        lock (_subscribed)
        {
            if (_subscribed.Contains(session))
            {
                return;
            }

            _subscribed.Add(session);
        }

        session.PlaybackInfoChanged += (_, _) => ScheduleRefresh();
        session.MediaPropertiesChanged += (_, _) => ScheduleRefresh();
    }

    /// <summary>
    /// 挑选活跃会话：只在白名单内挑，优先“正在播放”的，否则取第一个。
    /// 浏览器等非白名单会话（含直播场景）被直接跳过，绝不弹控制栏。
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        var manager = _manager;
        if (manager is null)
        {
            return null;
        }

        var whitelist = MusicApps;
        var sessions = manager.GetSessions()
            .Where(s => Matches(s.SourceAppUserModelId, whitelist))
            .ToList();

        return sessions.FirstOrDefault(s =>
                   s.GetPlaybackInfo().PlaybackStatus
                   == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
               ?? sessions.FirstOrDefault();
    }

    private void Publish(MediaInfo? info)
    {
        // 连续“无白名单播放”不必重复推送（浏览器等媒体会话会被反复刷新）
        if (_last is null && info is null)
        {
            return;
        }

        if (_last is not null && info is not null
            && _last.Title == info.Title
            && _last.Artist == info.Artist
            && _last.AppName == info.AppName
            && _last.IsPlaying == info.IsPlaying
            && _last.AppId == info.AppId)
        {
            return;
        }

        _last = info;

        if (info is not null)
        {
            // 换曲：播放进度估算器归零，避免把上一首的位置带到新歌上
            var key = $"{info.Title}|{info.Artist}";
            if (!string.Equals(key, _trackKey, StringComparison.Ordinal))
            {
                _trackKey = key;
                _trackChangedAtTicks = Stopwatch.GetTimestamp();
                _clock.Reset();
            }

            _log.Info("Media", info.IsPlaying ? "正在播放" : "播放暂停/停止",
                $"{info.AppName}: {info.Title} - {info.Artist}");
        }

        MediaChanged?.Invoke(info);
    }

    /// <summary>AUMID → 友好名称。白名单命中走展示名映射，其余取可读末段。</summary>
    private static string FriendlyName(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return "媒体播放器";
        }

        var display = DisplayNameOf(aumid);
        if (display is not null)
        {
            return display;
        }

        // AUMID 形如 "AppName!App" 或 "包名!入口"，取可读的末段
        var head = aumid.Split('!')[0];
        var dot = head.LastIndexOf('.');
        var name = dot >= 0 && dot < head.Length - 1 ? head[(dot + 1)..] : head;
        return string.IsNullOrWhiteSpace(name) ? "媒体播放器" : name;
    }

    /// <summary>从 AUMID 里取出白名单命中的进程名关键词（图标提取按它找进程）。</summary>
    private static string ProcessNameOf(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return string.Empty;
        }

        var lowered = aumid.ToLowerInvariant();
        foreach (var keyword in MusicApps)
        {
            if (lowered.Contains(keyword, StringComparison.Ordinal))
            {
                return keyword;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// SMTC 会话的 SourceAppUserModelId 是否命中音乐 App 名单。
    /// Win32 程序的 AUMID 通常是 exe 路径（…\Folia\Folia.exe），
    /// UWP 应用是包名（SpotifyAB.SpotifyMusic_zpdnejdrhbcvj），
    /// 小写包含匹配两种都能覆盖。
    /// </summary>
    private static bool Matches(string? aumid, IReadOnlyList<string> whitelist)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return false;
        }

        var lowered = aumid.ToLowerInvariant();
        foreach (var keyword in whitelist)
        {
            if (lowered.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>命中名单时返回对应的展示名（取映射的第一个匹配项），否则 null。</summary>
    private static string? DisplayNameOf(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return null;
        }

        var lowered = aumid.ToLowerInvariant();
        foreach (var keyword in MusicApps)
        {
            if (!lowered.Contains(keyword, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (map, name) in DisplayNames)
            {
                if (string.Equals(map, keyword, StringComparison.Ordinal))
                {
                    return name;
                }
            }

            // 取 AUMID 末段作为展示名
            var head = aumid.Split('!')[0];
            var dot = head.LastIndexOf('.');
            var tail = dot >= 0 && dot < head.Length - 1 ? head[(dot + 1)..] : head;
            return string.IsNullOrWhiteSpace(tail) ? keyword : tail;
        }

        return null;
    }
}
