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
    /// <summary>
    /// 巡检间隔（毫秒）。v0.2.15 需求 5：1500 → 400。
    ///
    /// 1500ms 是"进程状态可以慢慢来"的旧假设；但用户关掉播放器/WorkBuddy 之后，
    /// 岛要么继续显示残留内容、要么半天不回待机，主观感受就是"反应迟钝"。
    /// 一次 `Process.GetProcesses()` 实测 1~3ms，400ms 一轮的占用 &lt;1% 单核。
    ///
    /// **进程退出还有一条更快通路**（见 <see cref="WatchExit"/>）：
    /// 已经发现的进程会挂上 `Process.Exited` 事件，退出瞬间就补一次巡检，
    /// 所以"关掉播放器立刻回待机"不依赖这个间隔（实测端到端 0.05s 级）。
    /// </summary>
    private const int PollIntervalMs = 400;

    /// <summary>巡检间隔（毫秒，自检断言"实时性"用）。</summary>
    public static int PollInterval => PollIntervalMs;

    private readonly LogService _log;
    private readonly Timer _timer;
    private readonly object _gate = new();

    /// <summary>已挂"退出即通知"的进程（pid → Process 对象，必须留引用否则事件会丢）。</summary>
    private readonly Dictionary<int, Process> _watched = new();

    /// <summary>巡检重入保护：定时器与进程退出事件可能同时进来。</summary>
    private int _polling;

    private bool _musicRunning;
    private bool _workBuddyRunning;
    private string? _musicProcessName;
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

    /// <summary>
    /// 命中的播放器进程名（v0.2.12 需求 3）：待机态没有播放会话时，
    /// 岛左栏还要显示这个播放器的图标，只能靠这里记下来。
    /// </summary>
    public string? MusicProcessName => _musicProcessName;

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
        // 定时器与"进程退出"事件可能同时触发：一轮没跑完就别开第二轮
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        try
        {
            PollCore();
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void PollCore()
    {
        bool music = false;
        bool workBuddy = false;
        string? musicProcess = null;

        // 挂上"退出监听"的进程必须保住引用、绝不能 Dispose，其余进程一律释放句柄，
        // 否则 400ms 一轮的全表枚举会不停堆积内核句柄。
        var all = Process.GetProcesses();
        var adopted = new HashSet<int>();

        try
        {
            foreach (var process in all)
            {
                try
                {
                    var name = process.ProcessName;

                    if (!workBuddy && name.Contains("WorkBuddy", StringComparison.OrdinalIgnoreCase))
                    {
                        workBuddy = true;

                        if (WatchExit(process))
                        {
                            adopted.Add(process.Id);
                        }
                    }

                    if (!music && MatchesMusicApp(name))
                    {
                        music = true;
                        musicProcess = name;

                        if (WatchExit(process))
                        {
                            adopted.Add(process.Id);
                        }
                    }

                    if (music && workBuddy)
                    {
                        break; // 两样都找到了，剩下的不用再看
                    }
                }
                catch
                {
                    // 单个进程读名字失败（权限/已退出）跳过
                }
            }
        }
        catch (Exception ex)
        {
            // 进程枚举失败：保持上一次状态，不要因此把岛打回待机
            _log.Write(LogLevel.Debug, "Presence", "进程巡检失败", ex.Message);
            return;
        }
        finally
        {
            foreach (var process in all)
            {
                if (adopted.Contains(process.Id))
                {
                    continue; // 已托管给 Exited 事件，由它负责释放
                }

                try
                {
                    process.Dispose();
                }
                catch
                {
                    // 忽略
                }
            }
        }

        lock (_gate)
        {
            if (music == _musicRunning
                && workBuddy == _workBuddyRunning
                && string.Equals(musicProcess, _musicProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _musicRunning = music;
            _workBuddyRunning = workBuddy;
            _musicProcessName = musicProcess;
        }

        _log.Routine(
            "Presence",
            "进程状态变化",
            $"music={music}({musicProcess ?? "-"}) workbuddy={workBuddy}");

        try
        {
            Changed?.Invoke(music, workBuddy);
        }
        catch (Exception ex)
        {
            _log.Warn("Presence", "进程状态回调失败", ex.Message);
        }
    }

    /// <summary>
    /// 给"已经发现的目标进程"挂退出事件（v0.2.15 需求 5 的实时通路）。
    ///
    /// 轮询只能保证"最迟 400ms 发现"，而这个事件在进程句柄一置信号时就回调，
    /// 立刻补一次巡检 —— 用户关掉播放器/WorkBuddy 的那一刻岛就开始判断，不用等下一拍。
    /// `Process.Exited` 走线程池的注册等待，不占常驻线程；但必须保住 Process 对象引用，
    /// 否则被 GC 回收后事件就再也不会触发（这是 Win32 句柄监听的经典坑）。
    /// </summary>
    private bool WatchExit(Process process)
    {
        var pid = process.Id;

        lock (_gate)
        {
            if (_watched.ContainsKey(pid) || _watched.Count >= 32)
            {
                return false;
            }

            try
            {
                process.EnableRaisingEvents = true;
                _watched[pid] = process;
            }
            catch
            {
                return false; // 进程刚好已经退出：下一轮巡检会处理
            }
        }

        process.Exited += (_, _) =>
        {
            lock (_gate)
            {
                _watched.Remove(pid);
            }

            try
            {
                process.Dispose();
            }
            catch
            {
                // 忽略
            }

            _log.Warn("Presence", "目标进程已退出，立即刷新状态", $"pid={pid}");
            Poll();
        };

        return true;
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

        // 退出时把挂着的进程对象一起释放，别留内核句柄
        lock (_gate)
        {
            foreach (var process in _watched.Values)
            {
                try
                {
                    process.EnableRaisingEvents = false;
                    process.Dispose();
                }
                catch
                {
                    // 忽略
                }
            }

            _watched.Clear();
        }
    }
}
