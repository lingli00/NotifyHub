using System.Diagnostics;

namespace NotifyHub.Services;

/// <summary>
/// 「进程在不在」巡检（v0.2.11 需求 2）：只要音乐播放器或 WorkBuddy **进程打开了**，
/// 灵动岛就应该进入信息展示模式，而不是等到真的开始播放 / 开始跑任务。
///
/// 实现要点：
///   · 轮询而不是等事件：Windows 没有便宜的"任意进程启动"通知（WMI 太重、ETW 要管理员），
///     一次 `Process.GetProcesses()` 在百来个进程的机器上一次 ~1-3ms，1.5 秒跑一次完全可接受；
///   · 一轮只遍历一次进程表，同时判音乐与 WorkBuddy，避免两次全表扫描；
///   · 只有状态**变化**时才抛事件，避免每 1.5 秒惊动一次 UI 布局；
///   · 完全在后台线程跑，订阅方自己切回 UI 线程。
/// </summary>
public sealed class PresenceWatcher : IDisposable
{
    /// <summary>巡检间隔（毫秒）。1.5 秒：既够"立刻"，又不至于让 CPU 有存在感。</summary>
    private const int PollIntervalMs = 1500;

    private readonly LogService _log;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private bool _musicRunning;
    private bool _workBuddyRunning;
    private bool _started;

    public PresenceWatcher(LogService log)
    {
        _log = log;
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>状态变化（音乐播放器 / WorkBuddy 进程出现或消失）。参数在后台线程上触发。</summary>
    public event Action<bool, bool>? Changed;

    /// <summary>音乐播放器进程是否在运行。</summary>
    public bool MusicRunning => _musicRunning;

    /// <summary>WorkBuddy 进程是否在运行。</summary>
    public bool WorkBuddyRunning => _workBuddyRunning;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // 立即跑一次，别让启动后头 1.5 秒还是"待机"状态
        Poll();
        _timer.Change(PollIntervalMs, PollIntervalMs);
    }

    private void Poll()
    {
        bool music = false;
        bool workBuddy = false;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;

                    if (!workBuddy && name.Contains("WorkBuddy", StringComparison.OrdinalIgnoreCase))
                    {
                        workBuddy = true;
                    }

                    if (!music && MatchesMusicApp(name))
                    {
                        music = true;
                    }

                    if (music && workBuddy)
                    {
                        break;
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            // 进程枚举失败：保持上一次状态，不要因此把岛打回待机
            _log.Write(LogLevel.Debug, "Presence", "进程巡检失败", ex.Message);
            return;
        }

        lock (_gate)
        {
            if (music == _musicRunning && workBuddy == _workBuddyRunning)
            {
                return;
            }

            _musicRunning = music;
            _workBuddyRunning = workBuddy;
        }

        _log.Write(
            LogLevel.Debug,
            "Presence",
            "进程状态变化",
            $"music={music} workbuddy={workBuddy}");

        try
        {
            Changed?.Invoke(music, workBuddy);
        }
        catch (Exception ex)
        {
            _log.Warn("Presence", "进程状态回调失败", ex.Message);
        }
    }

    private static bool MatchesMusicApp(string processName)
    {
        foreach (var keyword in MediaPlayerMonitor.MusicApps)
        {
            if (processName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _started = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
    }
}
