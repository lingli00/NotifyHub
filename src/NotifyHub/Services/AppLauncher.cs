using System.Diagnostics;
using NotifyHub.Interop;
using NotifyHub.Services.Uia;

namespace NotifyHub.Services;

/// <summary>
/// 按进程名激活（必要时启动）一个桌面应用（v0.2.10 需求 2）。
///
/// 用途：待机窄条双击左半边「打开音乐播放器」、双击右半边「打开 WorkBuddy」。
///
/// 现实约束（都实测过）：
///   · 后台进程直接 SetForegroundWindow 会被前台锁拒绝 → 复用
///     <see cref="NativeMethods.ActivateWindow"/>（内部已做 AttachThreadInput 绕过）。
///   · 播放器常最小化到托盘，主窗口可能是隐藏的 → <see cref="WindowFinder.EnumerateWindows"/>
///     不筛 IsWindowVisible，ActivateWindow 里再 SW_RESTORE/SW_SHOW 拉回来。
///   · 进程名用**包含匹配**（Folia / cloudmusic / QQMusic 都能命中），并排除自己，
///     免得把 NotifyHub 设置窗激活到前台。
/// </summary>
public static class AppLauncher
{
    /// <summary>按进程名关键词激活已有窗口；找不到返回 false。</summary>
    public static bool TryActivate(string processNameKeyword)
    {
        var keyword = Normalize(processNameKeyword);
        if (keyword.Length == 0)
        {
            return false;
        }

        var names = MatchingProcessNames(keyword);
        foreach (var name in names)
        {
            var hwnd = WindowFinder.FindMainWindow(new[] { name }, Array.Empty<string>());
            if (hwnd != IntPtr.Zero && NativeMethods.ActivateWindow(hwnd))
            {
                LogService.Shared.Info("AppLauncher", "已激活", $"{name} hwnd=0x{hwnd.ToInt64():X}");
                return true;
            }
        }

        return false;
    }

    /// <summary>先尝试激活；没有运行则从候选路径启动。返回是否成功。</summary>
    public static bool TryActivateOrLaunch(string processNameKeyword, IReadOnlyList<string>? candidatePaths = null)
    {
        if (TryActivate(processNameKeyword))
        {
            return true;
        }

        foreach (var path in candidatePaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                continue;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                LogService.Shared.Info("AppLauncher", "已启动", path);
                return true;
            }
            catch (Exception ex)
            {
                LogService.Shared.Warn("AppLauncher", "启动失败", $"{path} → {ex.Message}");
            }
        }

        LogService.Shared.Warn("AppLauncher", "未找到可用应用", $"{processNameKeyword}");
        return false;
    }

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

    /// <summary>实际进程名里包含关键词的那些（用于容忍 exe 名与显示名不一致）。</summary>
    private static List<string> MatchingProcessNames(string keyword)
    {
        var names = new List<string>();
        var selfName = "NotifyHub";

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Equals(selfName, StringComparison.OrdinalIgnoreCase))
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
            // 进程枚举失败就按原样试一次
        }

        if (names.Count == 0)
        {
            names.Add(keyword);
        }

        return names;
    }

    private static string Normalize(string processNameKeyword)
    {
        var keyword = (processNameKeyword ?? string.Empty).Trim();
        return keyword.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? keyword[..^4] : keyword;
    }
}
