using Windows.Media.Control;

namespace NotifyHub.Services;

/// <summary>当前媒体会话快照（SMTC 聚合，任意线程回调）。</summary>
public sealed record MediaInfo(string Title, string Artist, string AppName, bool IsPlaying);

/// <summary>
/// 音乐播放器监控：基于 Windows 系统媒体传输控制（SMTC）聚合所有播放器，
/// 兼容网易云、QQ音乐、Spotify、Folia、酷狗等一切向系统上报媒体状态的播放器。
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

    /// <summary>媒体状态变化（任意线程回调，订阅方负责切 UI 线程）。null=无播放。</summary>
    public event Action<MediaInfo?>? MediaChanged;

    /// <summary>SMTC 是否初始化成功。</summary>
    public bool IsAvailable => _manager is not null;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            _log.Info("Media", "SMTC 媒体监控已就绪");
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

    /// <summary>播放/暂停切换（当前活跃会话）。</summary>
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
            Publish(null);
            return;
        }

        try
        {
            var playing = session0.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var props = await session0.TryGetMediaPropertiesAsync();
            var info = new MediaInfo(
                props.Title ?? string.Empty,
                props.Artist ?? string.Empty,
                FriendlyName(session0.SourceAppUserModelId),
                playing);
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

    /// <summary>挑选活跃会话：优先“正在播放”的，否则取第一个。</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        var manager = _manager;
        if (manager is null)
        {
            return null;
        }

        var sessions = manager.GetSessions();
        return sessions.FirstOrDefault(s =>
                   s.GetPlaybackInfo().PlaybackStatus
                   == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
               ?? sessions.FirstOrDefault();
    }

    private void Publish(MediaInfo? info)
    {
        if (_last is not null && info is not null
            && _last.Title == info.Title
            && _last.Artist == info.Artist
            && _last.AppName == info.AppName
            && _last.IsPlaying == info.IsPlaying)
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

    /// <summary>AUMID → 友好名称。Folia / 网易云 / QQ音乐 / Spotify 等常见播放器直接映射。</summary>
    private static string FriendlyName(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return "媒体播放器";
        }

        var lowered = aumid.ToLowerInvariant();

        if (lowered.Contains("folia"))
        {
            return "Folia";
        }

        if (lowered.Contains("cloudmusic") || lowered.Contains("netease"))
        {
            return "网易云音乐";
        }

        if (lowered.Contains("qqmusic"))
        {
            return "QQ音乐";
        }

        if (lowered.Contains("spotify"))
        {
            return "Spotify";
        }

        if (lowered.Contains("kugou"))
        {
            return "酷狗音乐";
        }

        if (lowered.Contains("kuwo"))
        {
            return "酷我音乐";
        }

        if (lowered.Contains("foobar"))
        {
            return "foobar2000";
        }

        // AUMID 形如 "AppName!App" 或 "包名!入口"，取可读的末段
        var head = aumid.Split('!')[0];
        var dot = head.LastIndexOf('.');
        var name = dot >= 0 && dot < head.Length - 1 ? head[(dot + 1)..] : head;
        return string.IsNullOrWhiteSpace(name) ? "媒体播放器" : name;
    }
}
