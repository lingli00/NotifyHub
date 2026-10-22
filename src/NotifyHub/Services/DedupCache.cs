using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 消息去重：按 (来源, 发件人, 内容) 的 SHA256 前 16 位做“消息 ID”。
/// 已推送过的 ID 会落盘（dedup.json），轮询多少次、重启多少次都不会二次弹窗/二次响铃。
/// 条目数量与保留时长都有上限，长时间运行不会无限膨胀。
/// </summary>
public sealed class DedupCache
{
    private const int MaxEntries = 2000;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly string? _path;
    private bool _dirty;

    /// <summary>持久化保留时长（默认 24 小时），仅影响落盘缓存清理，不影响拦截窗口。</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    public DedupCache()
    {
    }

    public DedupCache(string? path)
    {
        _path = path;
        Load();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _seen.Count;
            }
        }
    }

    /// <summary>
    /// 放行判定：同一消息 ID 在 <paramref name="window"/> 内重复出现则拦截。
    /// </summary>
    public bool TryAccept(NotifyMessage message, TimeSpan window, out string hash)
    {
        hash = Hash(message.DedupKey);
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            PruneLocked(now);

            if (_seen.TryGetValue(hash, out var seenAt) && now - seenAt < window)
            {
                return false;
            }

            _seen[hash] = now;
            _dirty = true;
            return true;
        }
    }

    /// <summary>是否已经推送过该消息 ID（诊断/测试用）。</summary>
    public bool Contains(string hash)
    {
        lock (_gate)
        {
            return _seen.ContainsKey(hash);
        }
    }

    /// <summary>落盘（由 UI 定时器调用，避免每条消息都写磁盘）。</summary>
    public void Flush()
    {
        if (_path is null)
        {
            return;
        }

        Dictionary<string, DateTime> snapshot;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            snapshot = new Dictionary<string, DateTime>(_seen, StringComparer.Ordinal);
        }

        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 落盘失败不影响去重判定（内存里仍然有效）
        }
    }

    /// <summary>清空去重记录（一键清除日志时【不】调用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _seen.Clear();
            _dirty = true;
        }

        if (_path is not null)
        {
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>对 (来源, 发件人, 内容) 计算稳定哈希，日志里可复现。</summary>
    public static string Hash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes, 0, 8);
    }

    private void PruneLocked(DateTime now)
    {
        if (_seen.Count == 0)
        {
            return;
        }

        var expireBefore = now - Retention;
        List<string>? expired = null;
        foreach (var pair in _seen)
        {
            if (pair.Value < expireBefore)
            {
                (expired ??= new List<string>()).Add(pair.Key);
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _seen.Remove(key);
            }

            _dirty = true;
        }

        // 上限保护：按时间淘汰最旧
        if (_seen.Count > MaxEntries)
        {
            foreach (var key in _seen.OrderBy(p => p.Value).Take(_seen.Count - MaxEntries).Select(p => p.Key).ToList())
            {
                _seen.Remove(key);
            }

            _dirty = true;
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
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
            if (loaded is null)
            {
                return;
            }

            lock (_gate)
            {
                foreach (var pair in loaded)
                {
                    _seen[pair.Key] = pair.Value;
                }

                PruneLocked(DateTime.UtcNow);
            }
        }
        catch
        {
            // 缓存损坏就当空缓存，不影响启动
        }
    }
}
