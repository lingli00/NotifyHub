using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 手动「导出 Bug 快照」（v0.2.10 需求 6）。
///
/// 设计要点：
///   · **只在用户点按钮时工作**：不挂任何定时器、不驻留后台线程，闲置时零 CPU / 零内存增长；
///   · 采集内容 = 整屏截图 + 点击前 10 秒与后 10 秒的**核心模块日志** + 当前整套配置 + 环境信息；
///   · 五个核心模块（按日志的 module 标签归类）：歌词 / 灵动岛布局 / 通知渲染 / 设置 UI / 交互控制；
///   · 产物是一个自包含目录，查看页（<see cref="UI.SnapshotWindow"/>）左边截图、右边对应时段日志。
///
/// 目录结构（`%AppData%\NotifyHub\snapshots\&lt;id&gt;\`）：
///   screen.png     整屏截图
///   logs.txt       前后 20 秒核心模块日志（按时间排序，逐行带模块名）
///   config.json    点击时刻的完整配置
///   meta.json      时间 / 版本 / 环境 / 各模块行数 / 是否已补录完成
/// </summary>
public static class BugSnapshotService
{
    /// <summary>采集窗口：点击前后各 10 秒（需求原文）。</summary>
    public const int WindowSeconds = 10;

    /// <summary>五个核心模块 → 日志 module 标签的归类表（前缀/包含匹配，大小写不敏感）。</summary>
    private static readonly (string Module, string[] Tags)[] ModuleTags =
    {
        ("歌词", new[] { "Lyrics", "Lyric" }),
        ("岛布局", new[] { "Island", "IslandAuto", "IslandStrip", "IslandLayout", "IslandPos" }),
        ("通知渲染", new[] { "通知", "Notification", "NoticeShot", "Notice", "Ntfy", "WeChat", "QQ" }),
        ("设置UI", new[] { "设置", "Settings", "SettingsShot", "Theme", "App" }),
        ("交互控制", new[] { "IslandDrag", "Drag", "Mouse", "Tray", "Hotkey", "IPC", "Ipc" }),
    };

    public static string Root => Path.Combine(AppPaths.Root, "snapshots");

    /// <summary>一条快照的元信息（查看页列表用）。</summary>
    public sealed record SnapshotInfo(
        string Id,
        string Directory,
        DateTime CreatedAt,
        bool Completed,
        int LineCount,
        string Note)
    {
        public string Display => $"{CreatedAt:MM-dd HH:mm:ss} · {LineCount} 行{(Completed ? string.Empty : "（补录中）")}";
    }

    /// <summary>
    /// 立即执行一次快照（必须在 UI 线程调用：要取主窗口尺寸与灵动岛位置）。
    /// 返回快照目录；同时安排 10 秒后的"后段日志"补录。
    /// </summary>
    public static SnapshotInfo Capture(string? note = null, Func<Rect?>? islandRectProvider = null)
    {
        var now = DateTime.Now;
        var id = now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);

        // 1) 截图（先抓，避免等待日志时界面又变了）
        var shot = ScreenCapture.CaptureToPng(Path.Combine(dir, "screen.png"));

        // 2) 前 10 秒日志
        var before = ReadModuleLogs(now.AddSeconds(-WindowSeconds), now);
        WriteLogFile(Path.Combine(dir, "logs.txt"), before);
        WriteLogFile(Path.Combine(dir, "logs-before.txt"), before);

        // 3) 配置 + 元信息
        WriteConfig(dir);
        var rect = islandRectProvider?.Invoke() ?? IslandRectProvider?.Invoke();
        var meta = new SnapshotMeta
        {
            Id = id,
            CreatedAt = now,
            Completed = false,
            Version = typeof(BugSnapshotService).Assembly.GetName().Version?.ToString() ?? "?",
            Os = Environment.OSVersion.VersionString,
            Screenshot = shot is not null,
            ScreenWidth = ScreenCapture.PrimaryScreenSize.Width,
            ScreenHeight = ScreenCapture.PrimaryScreenSize.Height,
            IslandRect = rect is null
                ? null
                : new[] { Math.Round(rect.Value.X), Math.Round(rect.Value.Y), Math.Round(rect.Value.Width), Math.Round(rect.Value.Height) },
            ModuleCounts = before.GroupBy(l => l.Module).ToDictionary(g => g.Key, g => g.Count()),
            Note = note ?? string.Empty,
        };
        WriteMeta(dir, meta);

        LogService.Shared.Info(
            "Snapshot",
            "已导出 Bug 快照",
            $"{dir} 前段 {before.Count} 行{(shot is null ? "（截图失败）" : string.Empty)}");

        // 4) 后台补录后 10 秒（只有这一次，10 秒后自己结束，不留定时器）
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(WindowSeconds)).ConfigureAwait(false);
                var after = ReadModuleLogs(now, now.AddSeconds(WindowSeconds));
                WriteLogFile(Path.Combine(dir, "logs-after.txt"), after);

                // 合并写回 logs.txt（按时间排序去重）
                var all = before.Concat(after)
                    .GroupBy(l => l.Raw)
                    .Select(g => g.First())
                    .OrderBy(l => l.Time)
                    .ToList();
                WriteLogFile(Path.Combine(dir, "logs.txt"), all);

                meta.Completed = true;
                meta.ModuleCounts = all.GroupBy(l => l.Module).ToDictionary(g => g.Key, g => g.Count());
                WriteMeta(dir, meta);

                LogService.Shared.Info("Snapshot", "快照补录完成", $"{dir} 共 {all.Count} 行");
            }
            catch
            {
                // 补录失败不影响已落盘的前段内容
            }
        });

        return new SnapshotInfo(id, dir, now, false, before.Count, meta.Note);
    }

    /// <summary>列出历史快照（新的在前）。</summary>
    public static List<SnapshotInfo> List()
    {
        var result = new List<SnapshotInfo>();

        try
        {
            if (!Directory.Exists(Root))
            {
                return result;
            }

            foreach (var dir in Directory.GetDirectories(Root))
            {
                try
                {
                    var metaPath = Path.Combine(dir, "meta.json");
                    var meta = File.Exists(metaPath)
                        ? JsonSerializer.Deserialize<SnapshotMeta>(File.ReadAllText(metaPath, Encoding.UTF8), JsonOptions)
                        : null;

                    var lines = 0;
                    var logPath = Path.Combine(dir, "logs.txt");
                    if (File.Exists(logPath))
                    {
                        lines = File.ReadAllLines(logPath, Encoding.UTF8).Count(l => l.Trim().Length > 0);
                    }

                    var created = meta?.CreatedAt ?? Directory.GetCreationTime(dir);
                    result.Add(new SnapshotInfo(
                        Path.GetFileName(dir),
                        dir,
                        created,
                        meta?.Completed ?? false,
                        lines,
                        meta?.Note ?? string.Empty));
                }
                catch
                {
                    // 单个目录损坏跳过
                }
            }
        }
        catch
        {
            // 枚举失败返回已收集
        }

        return result.OrderByDescending(s => s.CreatedAt).ToList();
    }

    /// <summary>
    /// 读取某条快照的日志行（供查看页按模块过滤）。
    /// [from, to] 可选：给了就按时间窗再筛一遍（快照文件里本来就只有窗口内的行，
    /// 这里主要给自检/外部工具做二次过滤用）。
    /// </summary>
    public static List<LogLine> ReadLines(string snapshotDir, DateTime? from = null, DateTime? to = null)
    {
        var path = Path.Combine(snapshotDir, "logs.txt");
        if (!File.Exists(path))
        {
            return new List<LogLine>();
        }

        var result = new List<LogLine>();
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = ParseLine(raw);
            if (line is null)
            {
                continue;
            }

            if (from is { } f && line.Time < f)
            {
                continue;
            }

            if (to is { } t && line.Time > t)
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    /// <summary>一条已解析的日志：时间 / 模块 / 原文。</summary>
    public sealed record LogLine(DateTime Time, string Module, string Text, string Raw);

    /// <summary>
    /// 读日志文件并只保留核心模块、且落在时间窗内的行。
    /// 直接读当天日志文件（而不是内存环形缓冲）：缓冲只有 600 行，被高频日志冲掉就丢证据了。
    /// 文件以 FileShare.ReadWrite 打开，和正在写入的进程并存。
    /// </summary>
    private static List<LogLine> ReadModuleLogs(DateTime from, DateTime to)
    {
        var result = new List<LogLine>();
        var path = LogService.Shared.CurrentLogFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            // 没有文件（日志未初始化）→ 退回内存缓冲
            foreach (var line in LogService.Shared.ConsoleSnapshot())
            {
                if (line.Time < from || line.Time > to)
                {
                    continue;
                }

                if (Classify(line.Module) is not { } module)
                {
                    continue;
                }

                var raw = $"{line.Time:yyyy-MM-dd HH:mm:ss.fff} [{line.Module}] {line.Text}";
                result.Add(new LogLine(line.Time, module, $"[{line.Module}] {line.Text}", raw));
            }

            return result;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (reader.ReadLine() is { } raw)
            {
                var parsed = ParseLine(raw);
                if (parsed is null || parsed.Time < from || parsed.Time > to)
                {
                    continue;
                }

                result.Add(parsed);
            }
        }
        catch (Exception ex)
        {
            LogService.Shared.Warn("Snapshot", "读取日志失败", ex.Message);
        }

        return result;
    }

    /// <summary>解析日志行；不在核心模块内的返回 null。</summary>
    private static LogLine? ParseLine(string raw)
    {
        try
        {
            // 格式：2026-10-23 17:51:12.239 [DBG] [Lyrics] 消息 | 详情
            if (raw.Length < 27)
            {
                return null;
            }

            if (!DateTime.TryParse(raw[..23], out var time))
            {
                return null;
            }

            var levelEnd = raw.IndexOf(']', 24);
            if (levelEnd < 0)
            {
                return null;
            }

            var moduleStart = raw.IndexOf('[', levelEnd + 1);
            var moduleEnd = raw.IndexOf(']', moduleStart + 1);
            if (moduleStart < 0 || moduleEnd < 0)
            {
                return null;
            }

            var moduleTag = raw[(moduleStart + 1)..moduleEnd];

            // 快照自身的日志不要进快照（会自我引用、越抓越多）
            if (moduleTag.Equals("Snapshot", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var module = Classify(moduleTag);
            if (module is null)
            {
                return null;
            }

            var text = raw[(moduleEnd + 1)..].Trim();
            return new LogLine(time, module, $"[{moduleTag}] {text}", raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 日志 module → 五个核心模块之一（不属于返回 null）。
    ///
    /// 匹配顺序按**标签长度从长到短**：否则 "IslandDrag"（交互控制）会被先匹配到的
    /// "Island"（岛布局）抢走 —— 这正是第一版自检抓到的错。
    /// </summary>
    public static string? Classify(string? moduleTag)
    {
        if (string.IsNullOrWhiteSpace(moduleTag))
        {
            return null;
        }

        foreach (var (tag, module) in OrderedTags)
        {
            if (moduleTag.Contains(tag, StringComparison.OrdinalIgnoreCase))
            {
                return module;
            }
        }

        return null;
    }

    /// <summary>扁平化 + 按标签长度降序的匹配表（构造一次，避免每次解析都排）。</summary>
    private static readonly (string Tag, string Module)[] OrderedTags = ModuleTags
        .SelectMany(m => m.Tags.Select(t => (Tag: t, Module: m.Module)))
        .OrderByDescending(t => t.Tag.Length)
        .ToArray();

    /// <summary>五个核心模块名（查看页筛选按钮按这个顺序排）。</summary>
    public static IReadOnlyList<string> Modules
        => ModuleTags.Select(t => t.Module).ToArray();

    private static void WriteLogFile(string path, List<LogLine> lines)
    {
        try
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            foreach (var line in lines)
            {
                writer.WriteLine(line.Raw);
            }
        }
        catch
        {
            // 写失败忽略
        }
    }

    private static void WriteConfig(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "config.json");
            var json = JsonSerializer.Serialize(ConfigServiceSnapshot(), ReadableJson);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>配置来源：优先当前运行的配置服务，取不到就退回磁盘上的 config.json 原文。</summary>
    private static object ConfigServiceSnapshot()
    {
        try
        {
            if (Current is not null)
            {
                return Current();
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            var path = AppPaths.ConfigFile;
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8)) ?? new AppConfig();
            }
        }
        catch
        {
            // 忽略
        }

        return new AppConfig();
    }

    /// <summary>由 App 在启动时注入"当前配置"的读取方式（避免这里反向依赖 ConfigService 实例）。</summary>
    public static Func<AppConfig>? Current { get; set; }

    /// <summary>由 App 注入：当前灵动岛窗口矩形（快照里记一笔，方便判断"当时岛在哪"）。</summary>
    public static Func<Rect?>? IslandRectProvider { get; set; }

    private static void WriteMeta(string dir, SnapshotMeta meta)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "meta.json"),
                JsonSerializer.Serialize(meta, ReadableJson),
                new UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>给人看的 JSON：缩进 + 中文不转义（默认会把"设置UI"写成 \u8BBE\u7F6EUI）。</summary>
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>meta.json 结构。</summary>
    public sealed class SnapshotMeta
    {
        public string Id { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public bool Completed { get; set; }

        public string Version { get; set; } = string.Empty;

        public string Os { get; set; } = string.Empty;

        public bool Screenshot { get; set; }

        public int ScreenWidth { get; set; }

        public int ScreenHeight { get; set; }

        public double[]? IslandRect { get; set; }

        public Dictionary<string, int> ModuleCounts { get; set; } = new();

        public string Note { get; set; } = string.Empty;
    }
}
