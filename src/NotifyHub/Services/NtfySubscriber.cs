using System.IO;
using System.Net.Http;
using System.Text;
using NotifyHub.Models;
using NotifyHub.Services.Parsing;

namespace NotifyHub.Services;

/// <summary>
/// ntfy 订阅（v0.2.4 多订阅，需求 1）：一个实例对应一个订阅地址，
/// 流式读取 /sse，断线按指数退避重连。
/// 关闭/改地址时由外部调用 <see cref="Stop"/>，读取中的 ReadLineAsync 会被取消。
/// </summary>
public sealed class NtfySubscriber : IDisposable
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly NtfySubscription _subscription;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile string _status = "未启动";
    private long _lastEventUnix;
    private bool _everConnected;

    public NtfySubscriber(LogService log, ConfigService config, NtfySubscription subscription)
    {
        _log = log;
        _config = config;
        _subscription = subscription;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NotifyHub/1.0");
    }

    public event EventHandler<NotifyMessage>? MessageReceived;

    public string Status => _status;

    /// <summary>该订阅的展示名（设置页列表显示 + 日志用）。</summary>
    public string Label => string.IsNullOrWhiteSpace(_subscription.Name)
        ? _subscription.Url
        : $"{_subscription.Name}（{_subscription.Url}）";

    public bool Paused { get; set; }

    public void Start()
    {
        if (_loop is { IsCompleted: false })
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
        _log.Info("ntfy", "订阅线程已启动", Label);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略
        }

        _cts?.Dispose();
        _cts = null;
        _status = "已停止";
        _log.Info("ntfy", "订阅已停止", Label);
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }

    /// <summary>把用户填的地址规范化成 /sse 端点；非法返回 null。</summary>
    public static string? BuildStreamUrl(string? raw)
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

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            return null;
        }

        var streamUrl = path.EndsWith("/sse", StringComparison.OrdinalIgnoreCase)
            ? uri.GetLeftPart(UriPartial.Path)
            : $"{uri.GetLeftPart(UriPartial.Authority)}{path}/sse";

        return string.IsNullOrEmpty(uri.Query) ? streamUrl : streamUrl + uri.Query;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(2);

        while (!token.IsCancellationRequested)
        {
            var config = _config.Current;

            if (!config.NtfyEnabled || !_subscription.Enabled)
            {
                _status = "未启用";
                await SafeDelay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                continue;
            }

            var url = BuildStreamUrl(_subscription.Url);
            if (url is null)
            {
                _status = "地址无效";
                _log.Warn("ntfy", "订阅地址无效", _subscription.Url);
                await SafeDelay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                continue;
            }

            try
            {
                _status = "连接中";

                // 断线重连时带 since= 上次收到消息的时间，把断线窗口内积压的消息补回来；
                // 全新进程启动一律带 since=当前时间——ntfy 服务端会缓存历史消息，
                // 不带 since 就会把老早之前的消息全部重弹一遍（需求 7：启动不回溯历史）。
                var lastEvent = Interlocked.Read(ref _lastEventUnix);
                var sinceUnix = _everConnected && lastEvent > 0
                    ? lastEvent
                    : (long)(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds;

                var connectUrl = url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?")
                                 + "since=" + sinceUnix;

                using var request = new HttpRequestMessage(HttpMethod.Get, connectUrl);
                request.Headers.Accept.ParseAdd("text/event-stream");
                NtfyAuth.Apply(request, _subscription.Url);

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                _status = "已连接";
                _everConnected = true;
                backoff = TimeSpan.FromSeconds(2);
                _log.Info("ntfy", "已订阅", connectUrl);

                using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var parser = new SseFrameParser();

                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    var frame = parser.Feed(line);
                    if (frame is not null)
                    {
                        HandleFrame(frame);
                    }
                }

                _status = "连接结束";
                _log.Warn("ntfy", "服务端关闭了连接，准备重连", Label);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _status = "连接异常";
                _log.Warn("ntfy", "订阅异常，稍后重连", $"{Label} | {ex.GetType().Name}: {ex.Message}");
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            await SafeDelay(backoff, token).ConfigureAwait(false);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }

        _status = "已停止";
    }

    private void HandleFrame(string frame)
    {
        if (!NtfySseParser.TryParseEvent(frame, out var message) || message is null)
        {
            return;
        }

        // 记录最后一条消息的服务端时间，供断线重连时用 since= 补齐缺口
        Interlocked.Exchange(ref _lastEventUnix, (long)(message.Time.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds);

        if (message.Id is not null)
        {
            lock (_seenIds)
            {
                if (!_seenIds.Add(message.Id))
                {
                    return;
                }

                _seenOrder.Enqueue(message.Id);
                while (_seenOrder.Count > 200)
                {
                    _seenIds.Remove(_seenOrder.Dequeue());
                }
            }
        }

        if (Paused)
        {
            _log.Info("ntfy", "监听已暂停，丢弃消息", $"{Label} | {message.Content}");
            return;
        }

        _log.Info("ntfy", $"收到消息: {message.Sender} → {LogService.Truncate(message.Content, 60)}", Label);

        MessageReceived?.Invoke(this, new NotifyMessage
        {
            Source = MessageSource.Ntfy,
            Sender = message.Sender,
            Content = message.Content,
            Time = message.Time,

            // 需求 2：多订阅分色 + 来源标签（如“ntfy · 家庭”）
            AccentIndex = _subscription.ColorIndex,
            SourceLabel = string.IsNullOrWhiteSpace(_subscription.Name)
                ? string.Empty
                : $"ntfy · {_subscription.Name}",

            // 点击通知 → 打开该 topic 的 Web UI 页面（需求 3，不是消息里的链接）
            LinkUrl = BrowserLauncher.BuildTopicUrl(_subscription.Url) ?? string.Empty,
        });
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
    }
}

/// <summary>
/// ntfy 认证（v0.2.4）：订阅地址里带 user:password@ 时转成 Basic Auth 头，
/// 同时把地址还原成干净 URL 再发起连接。
/// </summary>
public static class NtfyAuth
{
    public static void Apply(HttpRequestMessage request, string rawUrl)
    {
        var text = (rawUrl ?? string.Empty).Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return;
        }

        var userInfo = uri.UserInfo;
        if (string.IsNullOrEmpty(userInfo) || !userInfo.Contains(':'))
        {
            return;
        }

        var colon = userInfo.IndexOf(':');
        var user = Uri.UnescapeDataString(userInfo[..colon]);
        var pass = Uri.UnescapeDataString(userInfo[(colon + 1)..]);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
    }
}
