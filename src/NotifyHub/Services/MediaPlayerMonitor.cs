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
    string ProcessName = "");

/// <summary>
/// 音乐播放器监控：基于 Windows 系统媒体传输控制（SMTC）聚合所有播放器，
/// 但只响应 <see cref="MusicAppWhitelist"/> 白名单内的进程——
/// 浏览器（chrome/edge/firefox/brave…）刷视频/直播同样会上报媒体会话，
/// 不过白名单就会被忽略，不会再误弹音乐控制栏（需求 6）。
/// 读取播放状态并暴露 播放/暂停/上一首/下一首 控制；断续器事件推送给灵动岛。
/// </summary>
public sealed class MediaPlayerMonitor : IDisposable
{
    private readonly LogService _log;
    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private MediaInfo? _last;
    private int _refreshing;

    public MediaPlayerMonitor(LogService log) => _log = log;

    /// <summary>媒体状态变化（任意线程回调，订阅方负责切 UI 线程）。null=无白名单播放。</summary>
    public event Action<MediaInfo?>? MediaChanged;

    /// <summary>生效的音乐 App 白名单（设置页可改，留空=内置默认名单）。</summary>
    public Func<IReadOnlyList<string>>? WhitelistProvider { get; set; }

    /// <summary>SMTC 是否初始化成功。</summary>
    public bool IsAvailable => _manager is not null;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            _log.Info("Media", "SMTC 媒体监控已就绪（仅响应音乐 App 白名单）");
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
            var info = new MediaInfo(
                props.Title ?? string.Empty,
                props.Artist ?? string.Empty,
                FriendlyName(appId),
                playing,
                appId,
                ProcessNameOf(appId));
            Publish(info);
        }
        catch (Exception ex)
        {
            _log.Warn("Media", "读取媒体属性失败", ex.Message);
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

        var whitelist = WhitelistProvider?.Invoke() ?? MusicAppWhitelist.Defaults;
        var sessions = manager.GetSessions()
            .Where(s => MusicAppWhitelist.Matches(s.SourceAppUserModelId, whitelist))
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

        var display = MusicAppWhitelist.DisplayName(aumid, MusicAppWhitelist.Defaults);
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
        foreach (var keyword in MusicAppWhitelist.Defaults)
        {
            if (lowered.Contains(keyword, StringComparison.Ordinal))
            {
                return keyword;
            }
        }

        return string.Empty;
    }
}
