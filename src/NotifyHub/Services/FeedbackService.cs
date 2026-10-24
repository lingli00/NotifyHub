using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 一条"最近被拦截"的记录（v0.2.17 工单A：误拦截反馈的数据来源）。
/// 从运行日志的"已过滤微信会话"行还原，用户可一键选中它来上报。
/// </summary>
public sealed record BlockedEntry(
    DateTime At,
    string Contact,
    string Reason,
    string SourceTag,
    string Content)
{
    public string Display =>
        $"{At:MM-dd HH:mm}  {Contact}  —  {Reason}（{Content}）";
}

/// <summary>
/// 误拦截反馈（v0.2.17 工单A 优化建议 3）：
/// 用户觉得某条消息被错误拦截时，从这里选一条（或手动填写）提交。
/// 产物 = 反馈目录下一个 JSON（时间/会话/原因/渠道标签/内容类型/用户补充/版本），
/// 同时复制到剪贴板——直接贴给开发者就能定位，不需要用户自己翻日志。
/// </summary>
public static class FeedbackService
{
    /// <summary>日志里"已过滤微信会话"行的格式（v0.2.17 起带" | 渠道=… 内容=…"详情）。。</summary>
    private static readonly Regex BlockedLine = new(
        @"^(?<at>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+ \[[A-Z]+\] \[微信监听\] 已过滤微信会话: (?<contact>.+?)（(?<reason>.+)）(?: \| 渠道=(?<tag>\S+) 内容=(?<content>\S+))?\s*$",
        RegexOptions.Compiled);

    /// <summary>最近被拦截的记录（默认 50 条，按时间倒序；同一会话+原因只留最新一条）。。</summary>
    public static List<BlockedEntry> RecentBlocks(int max = 50)
    {
        var result = new List<BlockedEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var file in LogFilesNewestFirst())
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                // 文件里是时间正序，倒着读更快拿到"最近"
                for (var i = lines.Length - 1; i >= 0 && result.Count < max; i--)
                {
                    var match = BlockedLine.Match(lines[i]);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var contact = match.Groups["contact"].Value.Trim();
                    var reason = match.Groups["reason"].Value.Trim();
                    if (contact.Length == 0 || !seen.Add($"{contact}|{reason}"))
                    {
                        continue;
                    }

                    if (!DateTime.TryParse(match.Groups["at"].Value, out var at))
                    {
                        at = DateTime.MinValue;
                    }

                    result.Add(new BlockedEntry(
                        at,
                        contact,
                        reason,
                        match.Groups["tag"].Success ? match.Groups["tag"].Value : "未知",
                        match.Groups["content"].Success ? match.Groups["content"].Value : "文本"));
                }
            }
        }
        catch
        {
            // 读不到日志就返回空列表，反馈窗允许手动填写
        }

        return result;
    }

    /// <summary>提交一条误拦截反馈，返回产物文件路径（失败返回 null）。。</summary>
    public static string? Submit(
        string contact,
        string reason,
        string? comment = null,
        string? sourceTag = null,
        string? content = null)
    {
        try
        {
            contact = (contact ?? string.Empty).Trim();
            reason = (reason ?? string.Empty).Trim();

            if (contact.Length == 0 && reason.Length == 0)
            {
                return null;
            }

            Directory.CreateDirectory(AppPaths.FeedbackDir);

            var payload = new
            {
                kind = "misblock",
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                version = typeof(FeedbackService).Assembly.GetName().Version?.ToString() ?? "unknown",
                contact,
                reason,
                sourceTag = string.IsNullOrWhiteSpace(sourceTag) ? null : sourceTag,
                content = string.IsNullOrWhiteSpace(content) ? null : content,
                comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
                note = "由 NotifyHub 误拦截反馈入口自动生成，可直接发给开发者",
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            var path = Path.Combine(
                AppPaths.FeedbackDir,
                $"misblock-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");

            File.WriteAllText(path, json, new UTF8Encoding(false));
            TryCopyToClipboard(json);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>反馈目录下已有多少条反馈（状态栏用）。。</summary>
    public static int SubmittedCount()
    {
        try
        {
            if (!Directory.Exists(AppPaths.FeedbackDir))
            {
                return 0;
            }

            return Directory.GetFiles(AppPaths.FeedbackDir, "misblock-*.json").Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>打开反馈目录（资源管理器）。。</summary>
    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.FeedbackDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { AppPaths.FeedbackDir },
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开失败不阻塞（路径已在提交结果里给出）
        }
    }

    /// <summary>反馈目录路径（UI 展示用）。。</summary>
    public static string FolderPath => AppPaths.FeedbackDir;

    /// <summary>今天 + 昨天的日志文件，新的在前。</summary>
    private static IEnumerable<string> LogFilesNewestFirst()
    {
        var dir = LogService.Shared.LogDir;
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        var names = new[]
        {
            $"NotifyHub-{DateTime.Now:yyyyMMdd}.log",
            $"NotifyHub-{DateTime.Now.AddDays(-1):yyyyMMdd}.log",
        };

        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static void TryCopyToClipboard(string text)
    {
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                }
                catch
                {
                    // 剪贴板不可用（远程会话等）时静默失败，文件已经落盘
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }
        catch
        {
            // 忽略：剪贴板只是便利，不是必需
        }
    }
}
