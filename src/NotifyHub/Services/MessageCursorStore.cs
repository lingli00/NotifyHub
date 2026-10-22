using System.IO;
using System.Text.Json;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 消息游标持久化（需求 7）：记录每个来源“最后一条已展示消息”的边界，
/// 落盘 cursor.json，重启后不会从头拉取历史、不会把用户早看过的东西重弹一遍。
///
///   - IM 来源（微信/QQ）：存“联系人 → 最后已展示摘要”快照。启动时用它建基线，
///     只放行摘要发生变化的消息；
///   - ntfy：存最后一条消息的服务端 Unix 秒（断线重连补缺口用）；
///     全新启动不带 since，服务端缓存的历史消息一律不补弹。
///
/// 写盘走“脏标记 + 定时 Flush”（由 UI 定时器周期调用），不会每条消息都碰磁盘。
/// </summary>
public sealed class MessageCursorStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Dictionary<string, Dictionary<string, string>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private long _ntfyUnix;
    private bool _dirty;

    public MessageCursorStore(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>ntfy 游标：最后一条已处理消息的服务端 Unix 秒（0=没有）。</summary>
    public long NtfyCursor
    {
        get
        {
            lock (_gate)
            {
                return _ntfyUnix;
            }
        }
    }

    /// <summary>取某个来源的游标快照（不存在返回 null）。</summary>
    public Dictionary<string, string>? GetSnapshot(string source)
    {
        lock (_gate)
        {
            return _sources.TryGetValue(source, out var snapshot)
                ? new Dictionary<string, string>(snapshot, StringComparer.Ordinal)
                : null;
        }
    }

    /// <summary>记录一条已展示消息（更新该联系人的边界摘要）。</summary>
    public void Update(string source, string contact, string summary)
    {
        if (string.IsNullOrEmpty(contact))
        {
            return;
        }

        lock (_gate)
        {
            if (!_sources.TryGetValue(source, out var snapshot))
            {
                snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
                _sources[source] = snapshot;
            }

            snapshot[contact] = summary ?? string.Empty;
            _dirty = true;
        }
    }

    /// <summary>整表替换某个来源的快照（跟踪器基线变化时用）。</summary>
    public void ReplaceSnapshot(string source, IReadOnlyDictionary<string, string> snapshot)
    {
        lock (_gate)
        {
            _sources[source] = new Dictionary<string, string>(snapshot, StringComparer.Ordinal);
            _dirty = true;
        }
    }

    /// <summary>更新 ntfy 游标（只前进不后退）。</summary>
    public void SetNtfyCursor(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (unixSeconds <= _ntfyUnix)
            {
                return;
            }

            _ntfyUnix = unixSeconds;
            _dirty = true;
        }
    }

    /// <summary>落盘（脏标记保护，无变化不写）。</summary>
    public void Flush()
    {
        if (_path is null)
        {
            return;
        }

        Dictionary<string, Dictionary<string, string>> sourcesSnapshot;
        long ntfyUnix;

        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            sourcesSnapshot = _sources.ToDictionary(
                p => p.Key,
                p => new Dictionary<string, string>(p.Value, StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);
            ntfyUnix = _ntfyUnix;
        }

        try
        {
            var payload = new CursorFile
            {
                Sources = sourcesSnapshot,
                NtfyUnix = ntfyUnix,
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 落盘失败不影响内存游标
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<CursorFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            if (loaded is null)
            {
                return;
            }

            lock (_gate)
            {
                _sources.Clear();
                if (loaded.Sources is not null)
                {
                    foreach (var pair in loaded.Sources)
                    {
                        _sources[pair.Key] = new Dictionary<string, string>(pair.Value, StringComparer.Ordinal);
                    }
                }

                _ntfyUnix = loaded.NtfyUnix;
            }
        }
        catch
        {
            // 游标损坏当空游标：退化成“首次运行”行为（静默建基线，不补弹）
        }
    }

    /// <summary>cursor.json 的序列化形状。</summary>
    private sealed class CursorFile
    {
        public Dictionary<string, Dictionary<string, string>>? Sources { get; set; }
        public long NtfyUnix { get; set; }
    }
}
