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
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private MediaInfo? _last;
    private int _refreshing;

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

    /// <summary>播放/暂停切换（当前活跃的白名单会话）。</summary>
    public async Task TogglePlayPauseAsync()
    {
        var session = PickSession();
        if (session is null)
        {
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
            return;
        }

        try
        {
            await session.TrySkipNextAsync();
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
            return;
        }

        try
        {
            await session.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            _log.Warn("Media", "切上一首失败", ex.Message);
        }
    }

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
    /// </summary>
    public double GetPositionSeconds()
    {
        try
        {
            var session = PickSession();
            if (session is null)
            {
                return -1;
            }

            var timeline = session.GetTimelineProperties();
            return Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);
        }
        catch
        {
            return -1;
        }
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
