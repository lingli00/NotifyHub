using System.Net;
using System.Net.Http;
using System.Text;

namespace NotifyHub.Services;

/// <summary>
/// ntfy 下行推送（v0.2.4，需求 3）：把灵动岛里 WorkBuddy 正在执行的中文过程信息
/// 原样推送到手机端（手机 ntfy App 订阅同一 topic 即可以“通知”形式收到）。
/// 内容与岛内显示完全一致（调用方传入的就是上屏那一行文本）。
///
/// 失败只记日志、不弹窗——下行是附属能力，不能打扰主流程。
/// </summary>
public sealed class NtfyPublisher : IDisposable
{
    private readonly LogService _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string _lastSent = string.Empty;
    private long _lastSentMs;

    public NtfyPublisher(LogService log)
    {
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NotifyHub/1.0");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>目标地址（订阅地址格式，如 https://ntfy.sh/myphone）。空 = 未配置。</summary>
    public static string? BuildPushUrl(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
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

        // 与订阅侧一致：剥掉 /sse 后缀与查询参数，POST 到 topic 根地址
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/sse", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4].TrimEnd('/');
        }

        if (path.Length == 0)
        {
            return null;
        }

        return $"{uri.GetLeftPart(UriPartial.Authority)}{path}";
    }

    /// <summary>
    /// 推送一条文本到手机端。相同内容 3 秒内不重复发（岛内文本可能高频刷新）。
    /// </summary>
    public void Send(string? pushUrl, string text)
    {
        var url = BuildPushUrl(pushUrl);
        if (url is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (string.Equals(text, _lastSent, StringComparison.Ordinal) && now - _lastSentMs < 3000)
        {
            return;
        }

        _lastSent = text;
        _lastSentMs = now;

        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                NtfyAuth.Apply(request, pushUrl ?? string.Empty);
                request.Content = new StringContent(text, Encoding.UTF8, "text/plain");
                // 注意：HTTP 头只允许 ASCII——标题用英文，中文过程信息全放正文（UTF-8）
                request.Headers.TryAddWithoutValidation("X-Title", "WorkBuddy Task");
                request.Headers.TryAddWithoutValidation("X-Priority", "3");

                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _log.Info("ntfy下行", "已推送到手机端", LogService.Truncate(text, 50));
                }
                else
                {
                    _log.Warn("ntfy下行", $"推送失败 HTTP {(int)response.StatusCode}", url);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("ntfy下行", "推送异常", $"{ex.GetType().Name}: {ex.Message}");
            }
        });
    }
}
