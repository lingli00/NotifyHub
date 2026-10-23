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
    /// <summary>本次运行内已经启动过的进程（避免同一条双击链路里重复拉起）。</summary>
    private static readonly HashSet<string> LaunchedKeywords = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>常见音乐播放器安装路径（没装就跳过）。</summary>
    public static IReadOnlyList<string> MusicAppCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return new[]
        {
            System.IO.Path.Combine(local, "Programs", "Folia", "Folia.exe"),
            System.IO.Path.Combine(pf, "Folia", "Folia.exe"),
            System.IO.Path.Combine(pf86, "Netease", "CloudMusic", "cloudmusic.exe"),
            System.IO.Path.Combine(pf, "Netease", "CloudMusic", "cloudmusic.exe"),
            System.IO.Path.Combine(pf86, "Tencent", "QQMusic", "QQMusic.exe"),
            System.IO.Path.Combine(pf, "Tencent", "QQMusic", "QQMusic.exe"),
            System.IO.Path.Combine(roaming, "Tencent", "QQMusic", "QQMusic.exe"),
        };
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
    /// 隐藏启动：不走 ShellExecute（那会给控制台程序弹黑窗），拿到句柄后立刻释放。
    /// </summary>
    private static OpenOutcome LaunchHidden(string keyword, IReadOnlyList<string> candidatePaths)
    {
        if (LaunchedKeywords.Contains(keyword))
        {
            // 本次运行内已经启动过一次：给系统一点时间把窗口建出来，别连着拉第二个
            LogService.Shared.Write(LogLevel.Info, "AppLauncher", "已启动过，等待窗口", keyword);
            return OpenOutcome.Launched;
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
                LaunchedKeywords.Add(keyword);

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
