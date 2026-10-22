using System.Text;
using System.Text.Json;

namespace NotifyHub.Services.Parsing;

/// <summary>ntfy 消息体。</summary>
public sealed record NtfyMessage(string Sender, string Content, DateTime Time, string? Id);

/// <summary>
/// ntfy /sse 数据帧解析。纯函数式实现：订阅器按行喂，测试可整段喂。
/// </summary>
public sealed class SseFrameParser
{
    private readonly StringBuilder _data = new();

    /// <summary>
    /// 喂一行。返回非 null 表示收齐一个事件帧（内容为 data 行拼接结果）。
    /// 空行是 SSE 的帧分隔符。
    /// </summary>
    public string? Feed(string line)
    {
        if (line.Length == 0)
        {
            return TakeFrame();
        }

        // 注释行（": keepalive"）直接忽略
        if (line[0] == ':')
        {
            return null;
        }

        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            var value = line[5..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (_data.Length > 0)
            {
                _data.Append('\n');
            }

            _data.Append(value);
        }

        // event:/id:/time: 等字段对 ntfy 的 JSON 是冗余的，忽略
        return null;
    }

    /// <summary>流结束时把残余数据取出。</summary>
    public string? Flush() => TakeFrame();

    private string? TakeFrame()
    {
        if (_data.Length == 0)
        {
            return null;
        }

        var frame = _data.ToString();
        _data.Clear();
        return frame;
    }
}

public static class NtfySseParser
{
    /// <summary>解析 ntfy 的 data JSON。只有 event=message 才算一条消息。</summary>
    public static bool TryParseEvent(string json, out NtfyMessage? message)
    {
        message = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var evt = GetString(root, "event");
            if (!string.IsNullOrEmpty(evt) && !string.Equals(evt, "message", StringComparison.Ordinal))
            {
                // open / keepalive / poll_request 之类
                return false;
            }

            var title = GetString(root, "title");
            var body = GetString(root, "message");
            var sender = string.IsNullOrWhiteSpace(title) ? "ntfy" : title!.Trim();

            var content = !string.IsNullOrWhiteSpace(body)
                ? body!.Trim()
                : (!string.IsNullOrWhiteSpace(title) ? title!.Trim() : "(空消息)");

            var unix = GetLong(root, "time");
            var time = unix is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unix.Value).LocalDateTime
                : DateTime.Now;

            message = new NtfyMessage(sender, content, time, GetString(root, "id"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>把整段 SSE 文本拆成消息列表，供离线测试使用。</summary>
    public static List<NtfyMessage> ParseStreamText(string text)
    {
        var parser = new SseFrameParser();
        var result = new List<NtfyMessage>();

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var frame = parser.Feed(line);
            if (frame is not null && TryParseEvent(frame, out var message) && message is not null)
            {
                result.Add(message);
            }
        }

        var tail = parser.Flush();
        if (tail is not null && TryParseEvent(tail, out var last) && last is not null)
        {
            result.Add(last);
        }

        return result;
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
