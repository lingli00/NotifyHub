using System.IO;
using System.Text;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>本地歌词命中的结果（文件路径 + 读出来的文本）。</summary>
public sealed record LocalLyricMatch(string Path, string Text);

/// <summary>
/// 本地歌词库（v0.2.12 需求 1）：**优先读播放器自己带的歌词**，不再靠联网搜。
///
/// 为什么这么做：SMTC 只给「歌名 + 歌手 + 进度」，不暴露媒体文件路径，
/// 于是旧版只能拿歌名去网易云搜 —— 同名/翻唱/串烧一搜一个错，时间轴也跟着错
/// （用户看到的"歌词时间对不上、内容也不对，某些歌才复现"就是这个）。
/// 播放器把歌词存在哪儿是固定的：歌曲同目录的 `.lrc`，或播放器自己的缓存目录。
/// 这里把这些目录扫成一张「文件名 → 路径」索引，按歌名/歌手匹配。
///
/// 设计要点：
///   · **懒扫描 + 5 分钟 TTL**：启动不扫盘、闲置零开销；有新歌才可能触发一次索引；
///   · 深度限制 4 层、文件数上限 6000，避免用户把整个 D 盘塞进来时卡住；
///   · 只认 `.lrc`（带时间轴）——`.txt` 没有时间轴，拿来会出现"整段不跟着走"；
///   · 编码自动识别：UTF-8(BOM) → 严格 UTF-8 → GB18030（老歌词文件大量是 GBK）。
/// </summary>
public sealed class LocalLyricsLibrary
{
    private const int MaxDepth = 4;
    private const int MaxFiles = 6000;

    private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(5);

    private readonly LogService _log;
    private readonly Func<AppConfig> _config;
    private readonly object _gate = new();

    private Dictionary<string, List<string>>? _index;
    private DateTime _indexedAt = DateTime.MinValue;
    private int _indexedFiles;
    private static bool _codePagesReady;

    public LocalLyricsLibrary(LogService log, Func<AppConfig> config)
    {
        _log = log;
        _config = config;
    }

    /// <summary>索引里的文件数（诊断用；未建索引时为 0）。</summary>
    public int IndexedFiles
    {
        get
        {
            lock (_gate)
            {
                return _indexedFiles;
            }
        }
    }

    /// <summary>
    /// 按歌名 + 歌手找本地歌词。找不到返回 null（调用方决定是否走网络兜底）。
    /// </summary>
    public LocalLyricMatch? Find(string title, string artist)
    {
        var normTitle = LyricsService.Normalize(title);
        if (normTitle.Length == 0)
        {
            return null;
        }

        var normArtist = LyricsService.Normalize(artist);
        var index = GetIndex();

        string? bestPath = null;
        var bestScore = 0.0;
        var bestWrite = DateTime.MinValue;

        foreach (var (key, paths) in index)
        {
            double score;
            if (key == normTitle)
            {
                score = 1.0;
            }
            else if (key.Contains(normTitle, StringComparison.Ordinal))
            {
                score = 0.7;
            }
            else if (normTitle.Contains(key, StringComparison.Ordinal) && key.Length >= 2)
            {
                score = 0.6;
            }
            else
            {
                continue;
            }

            // 文件名里带歌手 → 明显更可信（"孤勇者 - 陈奕迅.lrc" 优先于 "孤勇者.lrc"）
            if (normArtist.Length > 0 && key.Contains(normArtist, StringComparison.Ordinal))
            {
                score += 0.3;
            }

            foreach (var path in paths)
            {
                DateTime write;
                try
                {
                    write = File.GetLastWriteTimeUtc(path);
                }
                catch
                {
                    continue;
                }

                if (score > bestScore + 1e-6 || (Math.Abs(score - bestScore) < 1e-6 && write > bestWrite))
                {
                    bestScore = score;
                    bestPath = path;
                    bestWrite = write;
                }
            }
        }

        // 太低就当没有：宁可没歌词，也不给用户看一首不相干的歌的词（需求 1 的原话）
        if (bestPath is null || bestScore < 0.6)
        {
            return null;
        }

        try
        {
            var text = ReadText(bestPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return new LocalLyricMatch(bestPath, text);
        }
        catch (Exception ex)
        {
            _log.Warn("Lyrics", "读取本地歌词失败", $"{Path.GetFileName(bestPath)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>强制下次扫描重建索引（换了歌词目录 / 新下了歌时用）。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _index = null;
            _indexedAt = DateTime.MinValue;
        }
    }

    private Dictionary<string, List<string>> GetIndex()
    {
        lock (_gate)
        {
            if (_index is not null && DateTime.UtcNow - _indexedAt < IndexTtl)
            {
                return _index;
            }

            var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var files = 0;

            foreach (var root in EnumerateRoots())
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    Walk(root, 0, index, ref files);
                }
                catch
                {
                    // 单个目录没权限只跳过它，不影响整体
                }
            }

            _index = index;
            _indexedFiles = files;
            _indexedAt = DateTime.UtcNow;

            if (files > 0)
            {
                _log.Routine("Lyrics", "本地歌词索引已建立", $"{files} 个 .lrc / {index.Count} 个歌名");
            }

            return index;
        }
    }

    private void Walk(string dir, int depth, Dictionary<string, List<string>> index, ref int files)
    {
        if (depth > MaxDepth || files >= MaxFiles)
        {
            return;
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(dir, "*.lrc", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        foreach (var path in entries)
        {
            if (files >= MaxFiles)
            {
                return;
            }

            files++;
            var key = LyricsService.Normalize(Path.GetFileNameWithoutExtension(path));
            if (key.Length == 0)
            {
                continue;
            }

            if (!index.TryGetValue(key, out var list))
            {
                list = new List<string>(2);
                index[key] = list;
            }

            list.Add(path);
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // 系统/程序目录没必要扫（AppData 下有成千上万个包目录，扫了全是噪声）
                var name = Path.GetFileName(sub);
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Packages", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                Walk(sub, depth + 1, index, ref files);
            }
        }
        catch
        {
            // 枚举子目录失败忽略
        }
    }

    /// <summary>
    /// 候选根目录：用户音乐库 + 常见播放器的歌词缓存目录 + 设置里手工添加的目录。
    /// 只返回**真实存在**的目录，避免每次白跑。
    /// </summary>
    private IEnumerable<string> EnumerateRoots()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var dir in DefaultRoots(user))
        {
            yield return dir;
        }

        var extra = _config().LyricsExtraPaths ?? string.Empty;
        foreach (var part in extra.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    /// <summary>内置候选目录（顺序即优先级，但匹配分才是决定因素）。</summary>
    public static IEnumerable<string> DefaultRoots(string userProfile)
    {
        var music = Path.Combine(userProfile, "Music");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return music;
        yield return Path.Combine(userProfile, "Desktop");
        yield return Path.Combine(userProfile, "Downloads");
        yield return Path.Combine(userProfile, "Documents");

        // 常见播放器的歌词/媒体缓存
        yield return Path.Combine(appData, "Folia");
        yield return Path.Combine(localAppData, "Folia");
        yield return Path.Combine(appData, "Tencent", "QQMusic");
        yield return Path.Combine(localAppData, "Tencent", "QQMusic");
        yield return Path.Combine(localAppData, "NetEase", "CloudMusic");
        yield return Path.Combine(localAppData, "NetEase", "CloudMusic", "webdata", "lyric");
        yield return Path.Combine(appData, "Kugou8");
        yield return Path.Combine(localAppData, "KuGou8");
        yield return Path.Combine(appData, "kwmusic");

        // 有些播放器把歌词放在音乐库里
        yield return Path.Combine(music, "CloudMusic");
        yield return Path.Combine(music, "QQMusic");
        yield return Path.Combine(music, "Lyrics");
        yield return Path.Combine(music, "网易云音乐");
    }

    /// <summary>读歌词文本：BOM → 严格 UTF-8 → GB18030 兜底（大量老 .lrc 是 GBK）。</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            offset = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8：按 GB18030 解（GBK 的超集，兼容绝大多数中文歌词文件）
        }

        try
        {
            EnsureEncodings();
            return Encoding.GetEncoding(54936).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (Exception ex)
        {
            LogService.Shared.Routine("Lyrics", "歌词编码回退失败", ex.Message);
            return Encoding.Latin1.GetString(bytes, offset, bytes.Length - offset);
        }
    }

    /// <summary>
    /// 注册 GBK/GB18030 编码提供程序（.NET 8 默认只带 Unicode/ASCII，不注册就抛
    /// "No data is available for encoding 936"）。公开出来是为了让自检也能造 GBK 测试文件。
    /// </summary>
    public static void EnsureEncodings()
    {
        if (_codePagesReady)
        {
            return;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 注册失败：GB18030 取不到时上层会退到 Latin1
        }

        _codePagesReady = true;
    }
}
