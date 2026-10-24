using System.Diagnostics;
using NotifyHub.Interop;
using NotifyHub.Services.Uia;

namespace NotifyHub.Services;

/// <summary>打开结果（v0.2.11 需求 1）：给上层区分"激活成功 / 进程在跑但没窗口 / 真的启动了一个"。</summary>
public enum OpenOutcome
{
    /// <summary>找到窗口并成功拉到前台。</summary>
    Activated,

    /// <summary>进程在跑，但没有可用窗口（托盘程序 / 窗口未创建）——不重复启动。</summary>
    RunningWithoutWindow,

    /// <summary>进程不在，已用隐藏方式启动（无控制台窗口）。</summary>
    Launched,

    /// <summary>进程不在、也没有可用的启动路径。</summary>
    Failed,
}

/// <summary>
/// 按进程名激活（必要时启动）一个桌面应用（v0.2.10 引入，v0.2.11 重写）。
///
/// 用途：待机窄条双击左半边「打开音乐播放器」、双击右半边「打开 WorkBuddy」。
///
/// v0.2.11 修掉的两个真实事故（来自用户导出的 Bug 快照 + 现场枚举）：
///   1. **重复启动**：上一版把"激活失败"当成"没在跑"，于是又 `Process.Start` 一个实例 ——
///      WorkBuddy 的第二个实例会立刻创建几个辅助窗口（任务栏多出一个点不开的图标、
///      屏幕左上角冒出一个 16×16 空白方框），而原实例其实好好开着。
///      现在：**只要目标进程存在就绝不启动**；激活不成功也返回"进程在跑"，宁可让用户自己点一下。
///   2. **黑命令行窗口**：上一版用 `UseShellExecute=true`（走 ShellExecute），
///      控制台子系统的 exe 会先弹一个黑窗口。现在用 `UseShellExecute=false` +
///      `CreateNoWindow=true` + `WindowStyle=Hidden`，并在拿到句柄后立刻 `Dispose()`
///      （需求 1：拉起后直接销毁，全程不向用户展示）。
/// </summary>
public static class AppLauncher
{
    /// <summary>
    /// 本次运行内启动过的进程（关键字 → 启动时刻 TickCount64）。
    ///
    /// v0.2.15 需求 1：这里**必须记时间**，不能只记"启动过"。
    /// 旧版是一个 HashSet，写进去就再也不删：用户用待机条双击拉起 Folia / WorkBuddy、
    /// 用完手动关掉、再双击 —— 命中的是"已启动过，等待窗口"，直接返回 Launched
    /// 而**什么都不做**，表现就是"待机条双击彻底失灵，只有重启 NotifyHub 才好"。
    /// 2026-10-24 的日志实证：10:47:29 真启动 Folia，之后 10:47:53 / 10:48:37 / 10:50:53
    /// / 10:51:28 全是"已启动过，等待窗口 | Folia" + outcome=Launched（用户早就把它关了）。
    /// </summary>
    private static readonly Dictionary<string, long> LaunchedAtMs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 刚拉起不到这么久（毫秒）就算"还在启动中"，容忍进程表尚未出现。
    /// 2 秒足够覆盖 Electron 应用"先起壳再起真身"的间隙；
    /// 再长就会让"关掉后立刻双击"白等，实测（2026-10-24）3 秒时会出现
    /// "返回 Launched 但进程没起来"的假成功。
    /// </summary>
    private const int LaunchGraceMs = 2000;

    /// <summary>最近一次唤起的时刻，用来吞掉 WPF 重复派发的双击（实测一次双击会触发两次 ClickCount==2）。</summary>
    private static long _lastOpenAtMs;

    /// <summary>目标进程是否在运行（进程名包含匹配，排除自己）。</summary>
    public static bool IsRunning(string processNameKeyword)
        => MatchingProcessNames(Normalize(processNameKeyword)).Count > 0;

    /// <summary>
    /// 打开目标应用：先激活已有窗口；进程在跑但没窗口就直接返回（不启动第二个实例）；
    /// 只有进程真的不在时才隐藏启动一个。
    /// </summary>
    public static OpenOutcome Open(string processNameKeyword, IReadOnlyList<string>? candidatePaths = null)
    {
        var keyword = Normalize(processNameKeyword);
        if (keyword.Length == 0)
        {
            return OpenOutcome.Failed;
        }

        // 双击会被 WPF 派发两次（各一次 ClickCount==2），600ms 内的第二次直接吞掉
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastOpenAtMs) < 600)
        {
            LogService.Shared.Write(LogLevel.Debug, "AppLauncher", "重复唤起已忽略", keyword);
            return OpenOutcome.Activated;
        }

        Interlocked.Exchange(ref _lastOpenAtMs, now);

        var names = MatchingProcessNames(keyword);
        if (names.Count > 0)
        {
            var hwnd = FindBestWindow(names);
            if (hwnd != IntPtr.Zero && NativeMethods.ActivateWindow(hwnd))
            {
                LogService.Shared.Info("AppLauncher", "已激活", $"{keyword} hwnd=0x{hwnd.ToInt64():X}");
                return OpenOutcome.Activated;
            }

            // 进程在、窗口找不到或没能置前：**绝不启动第二个实例**（幽灵图标的来源）
            LogService.Shared.Write(
                LogLevel.Info,
                "AppLauncher",
                "进程已在运行，跳过启动",
                $"{keyword} names={string.Join(",", names)}");
            return OpenOutcome.RunningWithoutWindow;
        }

        return LaunchHidden(keyword, candidatePaths ?? Array.Empty<string>());
    }

    /// <summary>按进程名关键词激活已有窗口；找不到返回 false（不启动）。</summary>
    public static bool TryActivate(string processNameKeyword)
    {
        var names = MatchingProcessNames(Normalize(processNameKeyword));
        if (names.Count == 0)
        {
            return false;
        }

        var hwnd = FindBestWindow(names);
        return hwnd != IntPtr.Zero && NativeMethods.ActivateWindow(hwnd);
    }

    /// <summary>兼容旧调用：进程在跑 / 已启动都算成功，只有彻底失败才 false。</summary>
    public static bool TryActivateOrLaunch(string processNameKeyword, IReadOnlyList<string>? candidatePaths = null)
        => Open(processNameKeyword, candidatePaths) != OpenOutcome.Failed;

    /// <summary>
    /// 内置播放器名单（进程名关键字 + 安装路径），按优先级排列。
    ///
    /// v0.2.15 需求 1：旧版只有一张"路径数组"，按 Folia 关键字去匹配进程——
    /// 用户装的是网易云时，`MatchingProcessNames("Folia")` 永远为空，于是每次双击都
    /// 走到"启动第一个存在的路径"，一旦某台机器上 Folia 不存在、网易云在跑，
    /// 就会**再拉一个网易云**（正是 v0.2.11 花大力气干掉的重复实例）。
    /// 现在把关键字和路径绑成一组，逐个判"跑没跑"，跑着就激活、没跑才启动。
    /// </summary>
    public static IReadOnlyList<(string Keyword, string Path)> MusicTargets()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return new[]
        {
            ("Folia", System.IO.Path.Combine(local, "Programs", "Folia", "Folia.exe")),
            ("Folia", System.IO.Path.Combine(pf, "Folia", "Folia.exe")),
            ("cloudmusic", System.IO.Path.Combine(pf86, "Netease", "CloudMusic", "cloudmusic.exe")),
            ("cloudmusic", System.IO.Path.Combine(pf, "Netease", "CloudMusic", "cloudmusic.exe")),
            ("QQMusic", System.IO.Path.Combine(pf86, "Tencent", "QQMusic", "QQMusic.exe")),
            ("QQMusic", System.IO.Path.Combine(pf, "Tencent", "QQMusic", "QQMusic.exe")),
            ("QQMusic", System.IO.Path.Combine(roaming, "Tencent", "QQMusic", "QQMusic.exe")),
        };
    }

    /// <summary>常见音乐播放器安装路径（兼容旧调用；新代码请用 <see cref="OpenMusic"/>）。</summary>
    public static IReadOnlyList<string> MusicAppCandidates()
        => MusicTargets().Select(t => t.Path).ToArray();

    /// <summary>
    /// 打开"音乐播放器"（待机条左段双击，v0.2.15 需求 1 重写）：
    ///   1) 有在播会话 → 直接按它的进程名激活；
    ///   2) 名单里任一播放器进程在跑 → 激活它（**绝不重复启动第二个实例**）；
    ///   3) 都不在跑 → 按名单顺序拉起第一个真实存在的可执行文件。
    /// 返回 Failed 表示"既没找到可激活的，也没有可启动的路径"。
    /// </summary>
    public static OpenOutcome OpenMusic(string? currentProcessName)
    {
        if (!string.IsNullOrWhiteSpace(currentProcessName))
        {
            var bySession = Open(currentProcessName);
            if (bySession != OpenOutcome.Failed)
            {
                return bySession;
            }
        }

        // 进程在跑就只激活，不启动：哪怕它和 Folia 不是同一个播放器
        foreach (var (keyword, _) in MusicTargets())
        {
            if (!IsRunning(keyword))
            {
                continue;
            }

            var activated = Open(keyword);
            if (activated != OpenOutcome.Failed)
            {
                return activated;
            }
        }

        // 都不在跑：按名单顺序找一个装了的拉起来
        foreach (var (keyword, path) in MusicTargets())
        {
            if (!System.IO.File.Exists(path))
            {
                continue;
            }

            var outcome = Open(keyword, new[] { path });
            if (outcome != OpenOutcome.Failed)
            {
                return outcome;
            }
        }

        LogService.Shared.Warn("AppLauncher", "未找到已安装的音乐播放器", "名单内的路径都不存在");
        return OpenOutcome.Failed;
    }

    /// <summary>WorkBuddy 常见安装路径。</summary>
    public static IReadOnlyList<string> WorkBuddyCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        return new[]
        {
            System.IO.Path.Combine(local, "Programs", "WorkBuddy", "WorkBuddy.exe"),
            System.IO.Path.Combine(pf, "WorkBuddy", "WorkBuddy.exe"),
        };
    }

    /// <summary>
    /// 启动目标应用（v0.2.15 需求 1 重写"已启动过"的判定）。
    ///
    /// 旧逻辑只要本进程启动过一次就直接返回，导致"用户关掉软件后再双击没反应"。
    /// 现在的判据是**目标进程还活不活**：
    ///   · 进程在跑（刚拉起 / 已经建好窗口）→ 不重复启动，返回 Launched；
    ///   · 进程不在、但上一次启动还不到 3 秒 → 可能进程表还没刷新，再等一等；
    ///   · 进程不在、上次启动已超过 3 秒 → 用户确实关掉了，**清掉记录重新拉起**。
    /// </summary>
    private static OpenOutcome LaunchHidden(string keyword, IReadOnlyList<string> candidatePaths)
    {
        var now = Environment.TickCount64;

        if (LaunchedAtMs.TryGetValue(keyword, out var launchedAt))
        {
            var running = IsRunning(keyword);

            if (ShouldSuppressLaunch(launchedAt, now, running))
            {
                // 进程已经在跑（或刚拉起、还没进进程表）：给系统一点时间把窗口建出来，
                // 绝不连着拉第二个实例（v0.2.11 的幽灵图标就是这么来的）
                LogService.Shared.Write(
                    LogLevel.Info,
                    "AppLauncher",
                    "已启动过，等待窗口",
                    $"{keyword} age={now - launchedAt}ms running={running}");
                return OpenOutcome.Launched;
            }

            // 进程已经退出：这是"用户关掉之后又想拉开"，必须允许重新启动
            LaunchedAtMs.Remove(keyword);
            LogService.Shared.Info("AppLauncher", "上次拉起的进程已退出，允许重新拉起", keyword);
        }

        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                continue;
            }

            try
            {
                var psi = BuildHiddenStartInfo(path);
                using var process = Process.Start(psi);

                // 需求 1：拉起后直接销毁句柄，不在这边留任何后台进程
                process?.Dispose();
                LaunchedAtMs[keyword] = now;

                LogService.Shared.Info("AppLauncher", "已隐藏启动", path);
                return OpenOutcome.Launched;
            }
            catch (Exception ex)
            {
                LogService.Shared.Warn("AppLauncher", "启动失败", $"{path} → {ex.Message}");
            }
        }

        LogService.Shared.Warn("AppLauncher", "未找到可用应用", keyword);
        return OpenOutcome.Failed;
    }

    /// <summary>清掉"启动过"的记录（自检复位用，正常流程靠进程存活判定自动清理）。</summary>
    public static void ForgetLaunched(string processNameKeyword)
        => LaunchedAtMs.Remove(Normalize(processNameKeyword));

    /// <summary>宽限期（毫秒），自检/日志用。</summary>
    public static int LaunchGraceLimitMs => LaunchGraceMs;

    /// <summary>
    /// "本次运行内启动过"要不要拦住这次启动（v0.2.15 需求 1，纯函数便于自检）。
    ///
    /// 只有两种情况该拦：
    ///   1) 目标进程确实还在跑 —— 再拉一个就是重复实例（幽灵图标 / 16×16 空白窗）；
    ///   2) 距上次启动还不到 <see cref="LaunchGraceMs"/> —— 进程表可能还没刷新，
    ///      此刻放行容易在同一个双击里拉起两个。
    /// 其余情况（进程已经退出、且过了宽限期）一律**放行**：
    /// 用户关掉软件后再双击，就是要重新打开它。
    /// </summary>
    public static bool ShouldSuppressLaunch(long launchedAtMs, long nowMs, bool processRunning)
    {
        if (processRunning)
        {
            return true;
        }

        return nowMs - launchedAtMs < LaunchGraceMs;
    }

    /// <summary>
    /// 构造"不弹黑窗"的启动参数（需求 1）。三个开关缺一不可：
    ///   UseShellExecute=false → 不经过 ShellExecute（控制台程序经 Shell 启动会先开一个黑窗口）
    ///   CreateNoWindow=true   → 不分配新的控制台
    ///   WindowStyle=Hidden    → 万一它自己建窗口也保持隐藏
    /// 抽成独立方法是为了让自检能直接断言这三个值，而不是靠"看起来没问题"。
    /// </summary>
    public static ProcessStartInfo BuildHiddenStartInfo(string path) => new(path)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
    };

    /// <summary>在该进程的所有顶层窗口里挑最像主窗口的那个（有标题、面积大优先）。</summary>
    private static IntPtr FindBestWindow(IReadOnlyList<string> processNames)
    {
        var best = IntPtr.Zero;
        var bestScore = double.MinValue;

        foreach (var name in processNames)
        {
            foreach (var window in WindowFinder.EnumerateWindows(new[] { name }))
            {
                // 最小化窗口面积记 0，但仍是合法候选（ActivateWindow 会 SW_RESTORE）
                var score = window.Minimized ? 1 : Math.Min(window.Area, 1_000_000);
                if (window.Title.Length > 0)
                {
                    score += 2_000_000;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = window.Handle;
                }
            }
        }

        return best;
    }

    /// <summary>实际进程名里包含关键词的那些（容忍 exe 名与显示名不一致）。</summary>
    private static List<string> MatchingProcessNames(string keyword)
    {
        var names = new List<string>();
        if (keyword.Length == 0)
        {
            return names;
        }

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Equals("NotifyHub", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                        && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // 进程枚举失败就当作没在跑
        }

        return names;
    }

    private static string Normalize(string processNameKeyword)
    {
        var keyword = (processNameKeyword ?? string.Empty).Trim();
        return keyword.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? keyword[..^4] : keyword;
    }
}
