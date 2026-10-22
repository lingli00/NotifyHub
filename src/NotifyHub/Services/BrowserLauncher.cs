using System.Diagnostics;
using System.IO;
using System.Windows;

namespace NotifyHub.Services;

/// <summary>
/// 浏览器唤起（需求 3 / v0.2.4 需求 7）：点击 ntfy 通知时打开该 topic 的网页地址
/// （通知列表 Web UI），而不是消息正文里附带的链接。
///
/// v0.2.4 起浏览器选择为四个选项：Google Chrome / Microsoft Edge / 夸克浏览器 / 其他。
/// 前三项由 <see cref="Locate"/> 在常见安装路径里探测；"其他"支持用户拖入快捷方式
/// （.lnk）或直接粘贴 exe 路径，<see cref="ResolveShortcut"/> 负责把 .lnk 解析成 exe。
/// </summary>
public static class BrowserLauncher
{
    /// <summary>固定浏览器探测路径（按优先级从后往前合并，先命中者优先）。</summary>
    private static readonly (string Choice, string[] Paths)[] KnownBrowsers =
    {
        new("Chrome", new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe"),
        }),
        new("Edge", new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        }),
        new("Quark", new[]
        {
            @"C:\Program Files\Quark\Quark.exe",
            @"C:\Program Files (x86)\Quark\Quark.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Quark\Quark.exe"),
        }),
    };

    /// <summary>
    /// 从订阅地址推导 topic 的 Web UI 地址：
    ///   https://ntfy.sh/notifyhub-test        → https://ntfy.sh/notifyhub-test
    ///   https://ntfy.sh/notifyhub-test/sse    → https://ntfy.sh/notifyhub-test
    ///   http://192.168.1.9:8080/alerts        → http://192.168.1.9:8080/alerts
    /// 查询参数（since=/auth=）一律剥掉——Web UI 不需要它们。
    /// </summary>
    public static string? BuildTopicUrl(string? ntfyUrl)
    {
        var text = (ntfyUrl ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/sse", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        path = path.TrimEnd('/');
        if (path.Length == 0)
        {
            return null;
        }

        return $"{uri.GetLeftPart(UriPartial.Authority)}{path}";
    }

    /// <summary>
    /// 探测固定浏览器的可执行文件路径；未安装返回 null。
    /// </summary>
    public static string? Locate(string choice)
    {
        foreach (var (name, paths) in KnownBrowsers)
        {
            if (!string.Equals(name, choice, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// 把用户提供的快捷方式（.lnk）或 exe 路径解析成可执行的 exe 路径。
    /// 拖入桌面快捷方式图标时 WPF 拿到的是 .lnk 文本，这里用 WScript.Shell COM
    /// （IDispatch 后期绑定，不依赖 vtable 顺序）读取 TargetPath。
    /// 解析失败时原样返回（让上层按原路径尝试，失败了也能给出明确报错）。
    /// </summary>
    public static string ResolveShortcut(string? raw)
    {
        var text = (raw ?? string.Empty).Trim().Trim('"');
        if (text.Length == 0)
        {
            return string.Empty;
        }

        // 拖入的可能是 "file:///C:/...lnk" 形式
        if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            text = Uri.UnescapeDataString(text["file:///".Length..]).Replace('/', '\\');
        }

        if (!text.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return text;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return text;
            }

            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { text });

            if (shortcut is null)
            {
                return text;
            }

            var target = shortcut.GetType().InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;

            return string.IsNullOrWhiteSpace(target) ? text : target.Trim();
        }
        catch
        {
            // 解析不了就用原路径（可能是 UWP 应用快捷方式，目标不是 exe）
            return text;
        }
    }

    /// <summary>
    /// 打开地址。<paramref name="browserPath"/> 为空走系统默认浏览器（UseShellExecute）；
    /// 指定了路径则用该浏览器打开。返回是否成功发起。
    /// </summary>
    public static bool Open(string? browserPath, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            var browser = ResolveShortcut(browserPath);

            if (browser.Length == 0)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            Process.Start(new ProcessStartInfo(browser, url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"打开浏览器失败：{ex.Message}\n\n地址：{url}",
                "NotifyHub",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }
}
