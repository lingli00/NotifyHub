using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 音乐播放器白名单：只有名单内的进程产生的媒体会话（SMTC）才会被
/// 音乐控制模块响应。浏览器（chrome/edge/firefox/brave…）刷视频/直播时
/// 也会向系统上报媒体会话，不设白名单就会误弹控制栏——需求 6 的根因。
///
/// 与需求 5（岛最左侧显示当前播放 App 图标）共用同一份名单：
/// MediaPlayerMonitor 过滤会话 + AppIconExtractor 提图标都走这里。
/// 名单可在设置页编辑（逗号分隔），留空则回落到内置默认名单。
/// </summary>
public static class MusicAppWhitelist
{
    /// <summary>
    /// 内置默认名单（进程名关键词，小写包含匹配，不区分 .exe）。
    /// 覆盖用户点名要求的：QQ音乐、酷狗、网易云、酷我、咪咕、foobar2000、FOLIA；
    /// 另附常见播放器。用户机器上 FOLIA 的进程名已实测确认为 Folia.exe。
    /// </summary>
    public static readonly string[] Defaults =
    {
        // 用户点名
        "qqmusic",      // QQ音乐（QQMusic.exe）
        "kugou",        // 酷狗音乐（KuGou.exe）
        "cloudmusic",   // 网易云音乐（cloudmusic.exe）
        "kuwo",         // 酷我音乐（kuwo.exe）
        "migu",         // 咪咕音乐（MiguMusic.exe / migu.exe）
        "foobar",       // foobar2000（foobar2000.exe）
        "folia",        // FOLIA（Folia.exe，本机实测：AppData\Local\Programs\Folia\Folia.exe）

        // 常见补充
        "spotify",      // Spotify
        "aimp",         // AIMP
        "hiby",         // 海贝音乐（HiByMusic）
        "soda",         // 汽水音乐
        "musicplayer",  // 部分本地播放器进程名
        "groove",       // Windows 媒体播放器（UWP AUMID 含 groove）
        "zune",         // Windows 媒体播放器（旧 AUMID）
        "itunes",       // iTunes
        "applemusic",   // Apple Music（Win 版）
    };

    /// <summary>
    /// 从配置读取生效名单：配置为空（或全空白）时用内置默认。
    /// </summary>
    public static IReadOnlyList<string> Effective(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Defaults;
        }

        var parsed = Parse(configured);
        return parsed.Count > 0 ? parsed : Defaults;
    }

    /// <summary>逗号/分号/竖线分隔解析成小写关键词列表。</summary>
    public static List<string> Parse(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var piece in raw.Split(new[] { ',', '，', ';', '；', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var keyword = piece.Trim().ToLowerInvariant();
            if (keyword.Length > 0 && !result.Contains(keyword))
            {
                result.Add(keyword);
            }
        }

        return result;
    }

    /// <summary>
    /// SMTC 会话的 SourceAppUserModelId 是否命中白名单。
    /// Win32 程序的 AUMID 通常是 exe 路径（…\Folia\Folia.exe），
    /// UWP 应用是包名（SpotifyAB.SpotifyMusic_zpdnejdrhbcvj），
    /// 小写包含匹配两种都能覆盖。
    /// </summary>
    public static bool Matches(string? aumid, IReadOnlyList<string> whitelist)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return false;
        }

        var lowered = aumid.ToLowerInvariant();
        foreach (var keyword in whitelist)
        {
            if (lowered.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>命中白名单时返回对应的展示名（取内置映射的第一个匹配项），否则 null。</summary>
    public static string? DisplayName(string? aumid, IReadOnlyList<string> whitelist)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return null;
        }

        var lowered = aumid.ToLowerInvariant();
        foreach (var keyword in whitelist)
        {
            if (!lowered.Contains(keyword, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (map, name) in DisplayNames)
            {
                if (string.Equals(map, keyword, StringComparison.Ordinal))
                {
                    return name;
                }
            }

            // 白名单里用户自定义的关键词：取 AUMID 末段作为展示名
            var head = aumid.Split('!')[0];
            var dot = head.LastIndexOf('.');
            var tail = dot >= 0 && dot < head.Length - 1 ? head[(dot + 1)..] : head;
            return string.IsNullOrWhiteSpace(tail) ? keyword : tail;
        }

        return null;
    }

    /// <summary>白名单关键词 → 友好展示名（音乐行显示用）。</summary>
    private static readonly (string Key, string Name)[] DisplayNames =
    {
        ("qqmusic", "QQ音乐"),
        ("kugou", "酷狗音乐"),
        ("cloudmusic", "网易云音乐"),
        ("kuwo", "酷我音乐"),
        ("migu", "咪咕音乐"),
        ("foobar", "foobar2000"),
        ("folia", "FOLIA"),
        ("spotify", "Spotify"),
        ("aimp", "AIMP"),
        ("hiby", "海贝音乐"),
        ("soda", "汽水音乐"),
        ("groove", "媒体播放器"),
        ("zune", "媒体播放器"),
        ("itunes", "iTunes"),
        ("applemusic", "Apple Music"),
    };
}
