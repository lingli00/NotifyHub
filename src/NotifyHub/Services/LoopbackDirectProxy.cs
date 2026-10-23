using System.Net;

namespace NotifyHub.Services;

/// <summary>
/// 本机代理直连器（v0.2.6）：系统开着 HTTP 代理时，.NET 会把 http://127.0.0.1
/// 和 http://localhost 的请求也丢给代理，代理不认识这个地址就回 502——
/// 表现是"自建在局域网/本机的 ntfy 服务器连不上，远端却正常"。
///
/// 这里包一层：环回地址与局域网私有地址直接连，其余仍走系统代理
/// （需要代理才能访问的外网 ntfy 不受影响）。
/// </summary>
public sealed class LoopbackDirectProxy : IWebProxy
{
    private readonly IWebProxy? _inner;

    public LoopbackDirectProxy(IWebProxy? inner = null)
        => _inner = inner ?? WebRequest.GetSystemWebProxy();

    public ICredentials? Credentials
    {
        get => _inner?.Credentials;
        set
        {
            if (_inner is not null)
            {
                _inner.Credentials = value;
            }
        }
    }

    /// <summary>是否属于“应当直连”的地址（环回 + 局域网私有段）。</summary>
    public static bool IsDirectHost(Uri? uri)
    {
        if (uri is null)
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return true;
        }

        var host = uri.Host;

        // 显式本机名
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "0.0.0.0", StringComparison.Ordinal))
        {
            return true;
        }

        // 局域网私有段：10.x / 192.168.x / 172.16-31.x / 169.254.x（链路本地）
        var parts = host.Split('.');
        if (parts.Length != 4
            || !parts.All(p => byte.TryParse(p, out _)))
        {
            return false;
        }

        var a = int.Parse(parts[0]);
        var b = int.Parse(parts[1]);

        return a == 10
               || (a == 192 && b == 168)
               || (a == 172 && b >= 16 && b <= 31)
               || (a == 169 && b == 254)
               || a == 127;
    }

    public Uri? GetProxy(Uri destination) => IsDirectHost(destination) ? null : _inner?.GetProxy(destination);

    public bool IsBypassed(Uri host) => IsDirectHost(host) || (_inner?.IsBypassed(host) ?? true);
}
