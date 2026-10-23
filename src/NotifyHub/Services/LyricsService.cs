using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>歌词来源（v0.2.12 需求 1：本地优先，日志与设置页都要能看到用的是哪一种）。</summary>
public enum LyricOrigin
{
    /// <summary>没有歌词。</summary>
    None = 0,

    /// <summary>本地 .lrc（播放器缓存 / 音乐库同目录）。</summary>
    LocalFile = 1,

    /// <summary>联网检索（默认关闭，仅当用户显式允许时兜底）。</summary>
    Network = 2,
}

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
/// 歌词服务（v0.2.12 需求 1 重做）：**先读播放器自己的本地歌词**，联网只作可选兜底。
///
/// 旧版为什么错：SMTC 只给标题/歌手/进度，不暴露媒体文件路径，于是只能拿歌名去
/// 网易云搜索 —— 同名、翻唱、串烧、live 版一搜就配错，用户看到的就是
/// "某些歌歌词内容和时间都对不上"。而那些歌的本地 `.lrc` 其实就在播放器缓存里。
///
/// 现在的顺序：
///   1. <see cref="LocalLyricsLibrary"/> 在音乐库 / 播放器歌词缓存里按歌名+歌手找 `.lrc`；
///   2. 找到 → 用它（含 `[offset:]` 偏移、过滤作词作曲等元信息行）；
///   3. 找不到且用户显式打开了「联网补词」→ 才走网易云；
///   4. 都没有 → 返回 null，岛内收起歌词区，**绝不显示错词**。
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
    private readonly Func<AppConfig> _config;
    private readonly LocalLyricsLibrary _local;
    private readonly Dictionary<string, IReadOnlyList<LyricLine>?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每首歌的歌词来源（诊断 / 设置页展示用）。</summary>
    private readonly Dictionary<string, LyricOrigin> _origins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 临时失败（超时/网络异常）的冻结表：key → 可以重试的时刻。
    ///
    /// v0.2.9 需求 4 的根因之一：旧版把"网络失败"也写进了 <see cref="_cache"/>，
    /// 于是同一首歌只要第一次取词赶上网络抖动，就会被**永久**记为"没有歌词"，
    /// 之后无论是重播、重开歌词开关还是暂停再播，都不会再请求——表现就是
    /// "设置里明明开着歌词，岛内只有播放器、歌词区空白"的偶发故障。
    /// 现在失败只冻结 25 秒（避免连续换歌把接口打爆），到期允许重试；
    /// 只有"确实搜不到 / 确实没有词"才进永久缓存。
    /// </summary>
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    /// <summary>失败后的重试冻结时长（秒）。</summary>
    private const int FailureRetrySeconds = 25;

    /// <summary>联网兜底的最低匹配分（0.9 = 歌名精确 + 歌手命中）。</summary>
    private const double MinNetworkMatchScore = 0.9;

    public LyricsService(LogService log, Func<AppConfig>? config = null)
    {
        _log = log;
        _config = config ?? (() => new AppConfig());
        _local = new LocalLyricsLibrary(log, () => _config());
    }

    /// <summary>本地歌词库（设置页"重新扫描本地歌词"用）。</summary>
    public LocalLyricsLibrary LocalLibrary => _local;

    /// <summary>这首歌上一次的歌词来源。</summary>
    public LyricOrigin OriginOf(string title, string artist)
    {
        lock (_gate)
        {
            return _origins.TryGetValue($"{title}|{artist}", out var origin) ? origin : LyricOrigin.None;
        }
    }

    /// <summary>是否允许联网补词（默认否：需求 1 明确要求不要网络搜词）。</summary>
    private bool NetworkAllowed()
    {
        try
        {
            return _config().LyricsNetworkFallback;
        }
        catch
        {
            return false;
        }
    }

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
    /// 取歌词。返回 null 表示"这一轮拿不到"（网络失败 / 无匹配 / 纯音乐）。
    ///
    /// 失败与"确实没有"的区别见 <see cref="_retryAfter"/>：前者会自己过期，后者永久缓存。
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

            // 上一次是网络失败且还没到重试时间：先不重复请求（上层看门狗会在到期后再来）
            if (_retryAfter.TryGetValue(key, out var retryAt) && DateTime.UtcNow < retryAt)
            {
                return null;
            }
        }

        IReadOnlyList<LyricLine>? result = null;
        var failed = false;
        var origin = LyricOrigin.None;
        var originDetail = string.Empty;

        // ---------- 1) 本地优先（v0.2.12 需求 1）----------
        // 播放器自己的歌词才是"这首歌的歌词"，联网搜的是"歌名相同的某首歌的歌词"。
        try
        {
            var local = await Task.Run(() => _local.Find(title, artist), token).ConfigureAwait(false);
            if (local is not null)
            {
                var lines = ParseLrc(local.Text);
                if (lines.Count > 0)
                {
                    result = lines;
                    origin = LyricOrigin.LocalFile;
                    originDetail = local.Path;
                }
            }
        }
        catch (OperationCanceledException)
        {
            failed = true;
        }
        catch (Exception ex)
        {
            // 本地读失败不算"这首歌没词"，允许下次重试
            failed = true;
            _log.Warn("Lyrics", $"读取本地歌词失败: {title}", ex.Message);
        }

        // ---------- 2) 联网兜底（默认关闭）----------
        if (result is null && !failed && NetworkAllowed())
        {
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
                        origin = LyricOrigin.Network;
                        originDetail = "网易云检索";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 超时：属于"这一次没成功"，允许稍后重试（旧版会永久缓存 null）
                failed = true;
            }
            catch (Exception ex)
            {
                failed = true;
                _log.Warn("Lyrics", $"取歌词失败: {title}", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        lock (_gate)
        {
            if (failed)
            {
                // 失败不进永久缓存：冻结一小段时间，到期后允许重试
                _retryAfter[key] = DateTime.UtcNow.AddSeconds(FailureRetrySeconds);
            }
            else
            {
                // 缓存上限 64 首，超了清空（简单可靠，不做 LRU）
                if (_cache.Count > 64)
                {
                    _cache.Clear();
                }

                _cache[key] = result;
                _origins[key] = result is null ? LyricOrigin.None : origin;
                _retryAfter.Remove(key);
            }
        }

        if (failed)
        {
            _log.Write(LogLevel.Debug, "Lyrics", $"取词失败（{FailureRetrySeconds}s 后可重试）: {title} - {artist}");
        }
        else if (result is null)
        {
            var why = NetworkAllowed() ? "本地与网络都没找到" : "本地没有（联网补词未开启）";
            _log.Write(LogLevel.Debug, "Lyrics", $"无歌词: {title} - {artist}", why);
        }
        else
        {
            var translatedCount = result.Count(l => l.HasTranslation);
            var suffix = translatedCount > 0 ? $"，含译文 {translatedCount} 行" : string.Empty;
            var from = origin == LyricOrigin.LocalFile ? "本地" : "网络";
            _log.Write(
                LogLevel.Debug,
                "Lyrics",
                $"歌词已就绪（{from}）: {title}（{result.Count} 行{suffix}）",
                originDetail);
        }

        return result;
    }

    /// <summary>这个 key 是否被永久缓存为"没有歌词"（诊断用）。</summary>
    public bool IsKnownNoLyric(string title, string artist)
    {
        lock (_gate)
        {
            return _cache.TryGetValue($"{title}|{artist}", out var cached) && cached is null;
        }
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

        // 严格匹配（v0.2.12 需求 1）：只接受"歌名与歌手都对上"的结果（0.75 + 0.25 = 1.0）。
        // 旧版用 0.6，会把"歌名包含"的翻唱/串烧/live 版也配进来 ——
        // 用户看到的就是"歌词内容不对、时间轴对不上，还只有部分歌复现"。
        return bestScore >= MinNetworkMatchScore ? bestId : null;
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
    /// 解析 LRC（v0.2.12 需求 1 重做）。支持：
    ///   * 一行多个时间标签 `[00:12.34][00:56.78]歌词`；
    ///   * 毫秒两位或三位、`[mm:ss]`；
    ///   * **`[offset:±毫秒]` 整体偏移**（旧版完全忽略这个标签，于是"部分歌曲"整体偏掉，
    ///     正是用户说的"某些歌时间对不上、别的歌没事"）；
    ///   * 过滤作词/作曲/编曲/制作人… 这些元信息行（旧版会把它们当歌词显示，
    ///     用户看到的第一句歌词是"作词 : 某某"，就是"歌词内容不对"）。
    /// </summary>
    public static IReadOnlyList<LyricLine> ParseLrc(string? lrc) => ParseLrc(lrc, out _);

    /// <summary>同上，另返回文件里声明的 `[offset:]`（毫秒，没有则 0）。</summary>
    public static IReadOnlyList<LyricLine> ParseLrc(string? lrc, out int offsetMs)
    {
        offsetMs = 0;
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return lines;
        }

        foreach (var raw in lrc.Split('\n'))
        {
            var text = raw.Replace("\r", string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var offsetMatch = OffsetTagRegex().Match(text);
            if (offsetMatch.Success && int.TryParse(offsetMatch.Groups["v"].Value, out var declared))
            {
                offsetMs = declared;
            }

            var matches = TimeTagRegex().Matches(text);
            if (matches.Count == 0)
            {
                continue;
            }

            // 去掉时间标签与 [ti:]/[ar:]/[by:] 等元标签后剩下的才是正文
            var content = MetaTagRegex().Replace(TimeTagRegex().Replace(text, string.Empty), string.Empty).Trim();
            if (content.Length == 0)
            {
                continue; // 纯时间标签的空行（间奏）没有可显示内容
            }

            if (IsMetadataLine(content))
            {
                continue; // "作词 : 某某" 不是歌词
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

        // 整篇偏移（[offset:+500] = 歌词比音频晚 500ms 出现 → 时间轴整体后移）
        if (offsetMs != 0)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var shifted = lines[i].Time + TimeSpan.FromMilliseconds(offsetMs);
                if (shifted < TimeSpan.Zero)
                {
                    shifted = TimeSpan.Zero;
                }

                lines[i] = lines[i] with { Time = shifted };
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>
    /// 这一行是不是"元信息"而不是歌词：作词/作曲/编曲/制作人…，
    /// 以及纯音乐提示语。它们带时间标签，但显示出来就是错的歌词。
    /// </summary>
    public static bool IsMetadataLine(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        var text = content.Trim();
        if (MetaTextRegex().IsMatch(text))
        {
            return true;
        }

        return text.Contains("纯音乐，请欣赏", StringComparison.Ordinal)
               || text.Contains("此歌曲为没有填词的纯音乐", StringComparison.Ordinal)
               || text.Contains("纯音乐请欣赏", StringComparison.Ordinal);
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

    /// <summary>`[offset:+500]` / `[offset:-200]`（大小写不敏感）。</summary>
    [GeneratedRegex(@"\[\s*offset\s*:\s*(?<v>[+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTagRegex();

    /// <summary>`[ti:xxx]` / `[ar:xxx]` / `[by:xxx]` 这类元标签。</summary>
    [GeneratedRegex(@"\[\s*[a-zA-Z]{1,10}\s*:[^\]]*\]")]
    private static partial Regex MetaTagRegex();

    /// <summary>作词/作曲… 这类元信息行（只在行首匹配，避免误伤正文）。</summary>
    [GeneratedRegex(
        @"^(作词|作曲|编曲|词|曲|编|制作人|监制|混音|母带|录音|出品|发行|和声|吉他|贝斯|鼓|弦乐|钢琴|词曲|制作|统筹|策划|企划|宣发|视觉|封面|原唱|翻唱|OP|SP)\s*[:：]",
        RegexOptions.IgnoreCase)]
    private static partial Regex MetaTextRegex();

    [GeneratedRegex(@"\b(feat|ft|live|remix|version|伴奏|纯音乐)\b")]
    private static partial Regex SuffixRegex();
}
