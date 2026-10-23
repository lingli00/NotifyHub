using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NotifyHub.Services;

/// <summary>
/// 一行歌词（时间点 + 原文 + 可选译文）。
/// 译文来自歌词资源自带的翻译（网易云接口的 tlyric 字段），不是机翻。
/// </summary>
public sealed record LyricLine(TimeSpan Time, string Text, string Translation = "")
{
    /// <summary>这一行是否带可用译文（与原文相同视为没有）。</summary>
    public bool HasTranslation => Translation.Length > 0;
}

/// <summary>
/// 歌词服务（v0.2.6 需求 5）：按「歌名 + 歌手」在线取 LRC 并解析成带时间轴的行。
///
/// 为什么走在线：Windows 的 SMTC 只给标题/歌手/进度，不暴露媒体文件路径，
/// 本地 .lrc 无从定位；因此在能上网时用网易云公开接口按名字检索。
/// 取不到（歌名对不上/网络不通）就返回 null——岛内自动收起歌词区，绝不显示错词。
///
/// 设计要点：
///   1. 结果按「歌名|歌手」缓存（含失败结果），换歌不重复请求；
///   2. 请求有 6 秒超时，且上游异常一律吞掉（歌词是锦上添花，不能拖累 UI）；
///   3. 解析与匹配都是纯函数，便于自检覆盖。
/// </summary>
public sealed partial class LyricsService
{
    private const string SearchApi = "https://music.163.com/api/search/get/web";
    private const string LyricApi = "https://music.163.com/api/song/lyric";

    private static readonly HttpClient Http = CreateClient();

    private readonly LogService _log;
    private readonly Dictionary<string, IReadOnlyList<LyricLine>?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public LyricsService(LogService log) => _log = log;

    /// <summary>缓存命中数（诊断用）。</summary>
    public int CacheCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>
    /// 取歌词。返回 null 表示没有（网络失败 / 无匹配 / 纯音乐）。
    /// </summary>
    public async Task<IReadOnlyList<LyricLine>?> GetAsync(string title, string artist, CancellationToken token = default)
    {
        title = (title ?? string.Empty).Trim();
        artist = (artist ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            return null;
        }

        var key = $"{title}|{artist}";
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        IReadOnlyList<LyricLine>? result = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            var songId = await SearchSongIdAsync(title, artist, timeout.Token).ConfigureAwait(false);
            if (songId is not null)
            {
                var (lrc, translated) = await FetchLyricAsync(songId.Value, timeout.Token).ConfigureAwait(false);
                var lines = ParseLrc(lrc);
                if (lines.Count > 0)
                {
                    // v0.2.8 需求 2：歌词资源自带翻译时自动附带（外文歌才有，中文歌接口返回空）
                    result = MergeTranslation(lines, translated);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 超时：静默（不写告警，避免每次换歌刷日志）
        }
        catch (Exception ex)
        {
            _log.Warn("Lyrics", $"取歌词失败: {title}", $"{ex.GetType().Name}: {ex.Message}");
        }

        lock (_gate)
        {
            // 缓存上限 64 首，超了清空（简单可靠，不做 LRU）
            if (_cache.Count > 64)
            {
                _cache.Clear();
            }

            _cache[key] = result;
        }

        if (result is null)
        {
            _log.Write(LogLevel.Debug, "Lyrics", $"无歌词: {title} - {artist}");
        }
        else
        {
            var translatedCount = result.Count(l => l.HasTranslation);
            var suffix = translatedCount > 0 ? $"，含译文 {translatedCount} 行" : string.Empty;
            _log.Write(LogLevel.Debug, "Lyrics", $"歌词已就绪: {title}（{result.Count} 行{suffix}）");
        }

        return result;
    }

    // ---------------- 网络 ----------------

    private static async Task<long?> SearchSongIdAsync(string title, string artist, CancellationToken token)
    {
        var query = Uri.EscapeDataString(artist.Length > 0 ? $"{title} {artist}" : title);
        var url = $"{SearchApi}?s={query}&type=1&limit=8&offset=0";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");

        using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        long? bestId = null;
        var bestScore = 0.0;

        foreach (var song in songs.EnumerateArray())
        {
            var name = song.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var singers = new List<string>();
            if (song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in artists.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var an))
                    {
                        singers.Add(an.GetString() ?? string.Empty);
                    }
                }
            }

            var score = MatchScore(title, artist, name, string.Join("/", singers));
            if (score > bestScore && song.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
            {
                bestScore = score;
                bestId = id;
            }
        }

        // 匹配度太低就当没找到（避免给用户看一首完全不相干的歌的歌词）
        return bestScore >= 0.6 ? bestId : null;
    }

    /// <summary>取原文与译文（tv=-1 = 要求返回翻译歌词，没有翻译时 tlyric.lyric 为空串）。</summary>
    private static async Task<(string? Lrc, string? Translation)> FetchLyricAsync(long songId, CancellationToken token)
    {
        var url = $"{LyricApi}?id={songId}&lv=-1&kv=-1&tv=-1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");

        using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, null);
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        string? lrc = null;
        if (document.RootElement.TryGetProperty("lrc", out var lrcNode)
            && lrcNode.TryGetProperty("lyric", out var lyric))
        {
            lrc = lyric.GetString();
        }

        string? translation = null;
        if (document.RootElement.TryGetProperty("tlyric", out var transNode)
            && transNode.TryGetProperty("lyric", out var transLyric))
        {
            translation = transLyric.GetString();
        }

        return (lrc, translation);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new LoopbackDirectProxy(),
            UseProxy = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return client;
    }

    // ---------------- 解析与匹配（纯函数，自检覆盖） ----------------

    /// <summary>
    /// 解析 LRC。支持一行多个时间标签 `[00:12.34][00:56.78]歌词`、
    /// 毫秒两位或三位、以及 `[mm:ss]`。元信息行（ti/ar/al/by/offset）自动跳过。
    /// </summary>
    public static IReadOnlyList<LyricLine> ParseLrc(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return lines;
        }

        foreach (var raw in lrc.Split('\n'))
        {
            var text = raw.Replace("\r", string.Empty);
            if (text.Length == 0)
            {
                continue;
            }

            var matches = TimeTagRegex().Matches(text);
            if (matches.Count == 0)
            {
                continue;
            }

            // 去掉所有时间标签后剩下的就是歌词正文
            var content = TimeTagRegex().Replace(text, string.Empty).Trim();
            if (content.Length == 0)
            {
                continue; // 纯时间标签的空行（间奏）没有可显示内容
            }

            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups["m"].Value);
                var seconds = int.Parse(match.Groups["s"].Value);
                var fraction = match.Groups["f"].Value;

                var milliseconds = fraction.Length switch
                {
                    0 => 0,
                    1 => int.Parse(fraction) * 100,
                    2 => int.Parse(fraction) * 10,
                    _ => int.Parse(fraction[..3]),
                };

                lines.Add(new LyricLine(
                    new TimeSpan(0, 0, minutes, seconds, milliseconds),
                    content));
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>
    /// 把译文按时间点合并进主歌词（v0.2.8 需求 2）。
    ///
    /// 规则：
    ///   * 译文按时间点匹配（LRC 的 tlyric 与 lrc 时间轴通常完全一致，留 400ms 容差兜住个别偏移）；
    ///   * 译文与原文相同（中文歌 / 接口把原文当译文返回）→ 视为没有译文，避免同一句显示两遍；
    ///   * 匹配不到的译文直接丢弃，绝不把上一句的翻译塞给下一句。
    /// </summary>
    public static IReadOnlyList<LyricLine> MergeTranslation(
        IReadOnlyList<LyricLine> lines, string? translatedLrc)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(translatedLrc))
        {
            return lines;
        }

        var translations = ParseLrc(translatedLrc);
        if (translations.Count == 0)
        {
            return lines;
        }

        var result = new List<LyricLine>(lines.Count);
        var cursor = 0;

        foreach (var line in lines)
        {
            var match = string.Empty;

            // 时间轴基本同序，用游标线性推进，避免每行二分查找
            while (cursor + 1 < translations.Count
                   && Math.Abs((translations[cursor + 1].Time - line.Time).TotalMilliseconds)
                      <= Math.Abs((translations[cursor].Time - line.Time).TotalMilliseconds))
            {
                cursor++;
            }

            if (cursor < translations.Count
                && Math.Abs((translations[cursor].Time - line.Time).TotalMilliseconds) <= 400)
            {
                var candidate = translations[cursor].Text.Trim();
                if (candidate.Length > 0 && !LooksSame(candidate, line.Text))
                {
                    match = candidate;
                }
            }

            result.Add(line with { Translation = match });
        }

        return result;
    }

    /// <summary>两段文本是否实质相同（忽略空白、标点与大小写）。</summary>
    private static bool LooksSame(string a, string b)
    {
        static string Squash(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString();
        }

        return string.Equals(Squash(a), Squash(b), StringComparison.Ordinal);
    }

    /// <summary>
    /// 当前播放位置对应的行号：返回最后一个 Time ≤ position 的下标；
    /// 还没到第一行返回 -1。
    /// </summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, double positionSeconds)
    {
        if (lines.Count == 0)
        {
            return -1;
        }

        var position = TimeSpan.FromSeconds(Math.Max(0, positionSeconds));
        var low = 0;
        var high = lines.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (lines[mid].Time <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// 歌名/歌手匹配打分（0~1）：歌名完全一致 0.75、包含 0.55；歌手命中再加 0.25。
    /// 归一化会去掉空格、括号内容与常见后缀（feat./live/remix）。
    /// </summary>
    public static double MatchScore(string title, string artist, string candidateTitle, string candidateArtist)
    {
        var a = Normalize(title);
        var b = Normalize(candidateTitle);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var score = 0.0;
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            score = 0.75;
        }
        else if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            score = 0.55;
        }

        if (score == 0)
        {
            return 0;
        }

        var singer = Normalize(artist);
        var candidateSinger = Normalize(candidateArtist);
        if (singer.Length > 0 && candidateSinger.Length > 0
            && (singer.Contains(candidateSinger, StringComparison.Ordinal)
                || candidateSinger.Contains(singer, StringComparison.Ordinal)))
        {
            score += 0.25;
        }

        return Math.Min(1.0, score);
    }

    /// <summary>归一化：小写、去空白与标点、去括号内容、去 feat/live/remix 等后缀。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = text.ToLowerInvariant();
        value = BracketRegex().Replace(value, " ");
        value = SuffixRegex().Replace(value, " ");

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]")]
    private static partial Regex TimeTagRegex();

    [GeneratedRegex(@"\([^)]*\)|（[^）]*）|\[[^\]]*\]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\b(feat|ft|live|remix|version|伴奏|纯音乐)\b")]
    private static partial Regex SuffixRegex();
}
