using System.Diagnostics;
using System.Windows;

namespace NotifyHub.Services;

/// <summary>
/// 浏览器唤起（需求 3）：点击 ntfy 通知时打开该 topic 的网页地址
/// （通知列表 Web UI），而不是消息正文里附带的链接。
/// 默认用系统默认浏览器；设置页可指定浏览器可执行文件路径。
/// </summary>
public static class BrowserLauncher
{
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
            var browser = (browserPath ?? string.Empty).Trim();

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
