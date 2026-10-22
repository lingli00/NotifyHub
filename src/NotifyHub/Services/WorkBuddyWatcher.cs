using System.IO;
using System.Text;
using System.Text.Json;

namespace NotifyHub.Services;

/// <summary>
/// 自动读取 WorkBuddy 桌面端的任务进度。
///
/// WorkBuddy 会把 AI 会话的每一步实时追加写到本地会话记录：
///   %USERPROFILE%\.workbuddy\projects\&lt;项目&gt;\&lt;会话&gt;.jsonl
/// 行类型：
///   function_call        AI 正在执行的动作（name + arguments）
///   message(assistant)   AI 的可见回复（content[].output_text）
///   session-meta/…       会话标题等元信息
///
/// 本监视器增量 tail 最新会话文件（每 0.8s），把「AI 正在做什么」提炼成
/// 一行动作文字推给灵动岛；当最后一条事件是 AI 回复且安静 8 秒无新动作，
/// 判定回合结束，报完成态（绿勾 + 完成提示音）。3 分钟无任何事件则不再显示。
/// </summary>
public sealed class WorkBuddyWatcher : IDisposable
{
    /// <summary>(一行动作文字, 是否完成态)。后台线程触发。</summary>
    public event Action<string, bool>? TaskChanged;

    private readonly LogService _log;
    private Thread? _thread;
    private volatile bool _stop;

    private readonly object _gate = new();
    private string? _path;      // 当前跟踪的会话文件
    private long _offset;       // 已读到的字节偏移
    private string _emitKey = "";  // 上次推送的内容（去重）

    // 最近事件状态（只在 _gate 内访问）
    private string _lastAction = "";
    private long _lastActionTs;
    private string _lastReply = "";
    private long _lastReplyTs;
    private long _lastAnyTs;

    private const int QuietMs = 8_000;        // 回复后安静 8s → 判定完成
    private const int StaleMs = 3 * 60_000;   // 3 分钟无事件 → 不再显示
    private const long TailBytes = 256 * 1024; // 首次接入只回看尾部 256KB

    public WorkBuddyWatcher(LogService log)
    {
        _log = log;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "WorkBuddyWatcher",
        };
        _thread.Start();
        _log.Info("WorkBuddy", "任务进度监视已启动", Root);
    }

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".workbuddy", "projects");

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                _log.Warn("WorkBuddy", "轮询失败", ex.Message);
            }

            Thread.Sleep(800);
        }
    }

    private void Poll()
    {
        var latest = LatestSessionFile();
        if (latest is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!string.Equals(latest, _path, StringComparison.OrdinalIgnoreCase))
            {
                _path = latest;
                _offset = TailOffset(latest);
                _emitKey = "";
                _log.Info("WorkBuddy", "跟踪会话记录", Path.GetFileName(latest));
            }

            var (text, done) = ReadNewEvents(latest);
            if (text.Length > 0)
            {
                Emit(text, done);
            }
        }
    }

    /// <summary>projects 下所有会话文件里 mtime 最新的那个（必须还在 10 分钟内更新过）。</summary>
    private static string? LatestSessionFile()
    {
        if (!Directory.Exists(Root))
        {
            return null;
        }

        string? best = null;
        var bestTime = DateTime.MinValue;
        var cutoff = DateTime.Now.AddMinutes(-10);

        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                {
                    var time = File.GetLastWriteTime(file);
                    if (time > bestTime)
                    {
                        bestTime = time;
                        best = file;
                    }
                }
            }
            catch
            {
                // 单个项目目录读不动就跳过
            }
        }

        return bestTime >= cutoff ? best : null;
    }

    /// <summary>从文件尾部向前找最后一个完整行的行首。</summary>
    private static long TailOffset(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);

            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _ = reader.ReadLine(); // 这一半行多半是残行，丢掉
            return fs.Position;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>增量读取新行并解析，返回当前应显示的状态。</summary>
    private (string Text, bool Done) ReadNewEvents(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (fs.Length < _offset)
        {
            _offset = 0; // 文件被重建/轮转
        }

        if (fs.Length > _offset)
        {
            fs.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var chunk = reader.ReadToEnd();

            var nl = chunk.LastIndexOf('\n');
            if (nl < 0)
            {
                return CurrentState(); // 还没有完整行，等下一轮
            }

            chunk = chunk[..(nl + 1)];
            _offset += Encoding.UTF8.GetByteCount(chunk);

            foreach (var line in chunk.Split('\n'))
            {
                if (line.Length > 2)
                {
                    ParseLine(line);
                }
            }
        }

        return CurrentState();
    }

    /// <summary>根据最近事件决定当前显示：AI 反馈文字 &gt; 动作兜底 &gt; 完成态（安静 8s）。</summary>
    private (string Text, bool Done) CurrentState()
    {
        if (_lastAnyTs == 0)
        {
            return ("", false);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (now - _lastAnyTs > StaleMs)
        {
            return ("", false); // 会话停了：不再显示，让岛上现有状态自然收起
        }

        // 用户要求：展示 AI 的「反馈文字」，而不是正在执行的代码。
        // 只要有过 AI 文字回复，就以它为准；动作描述只在没有任何回复时兜底。
        var text = _lastReply.Length > 0 ? _lastReply : _lastAction;
        if (text.Length == 0)
        {
            return ("", false);
        }

        // 回复后安静 8s 且最后事件不是动作 → 判定回合结束
        var quiet = now - _lastAnyTs >= QuietMs;
        if (quiet && _lastActionTs < _lastReplyTs)
        {
            return (text, true);
        }

        return (text, false);
    }

    private void Emit(string text, bool done)
    {
        var key = (done ? "D|" : "R|") + text;
        if (key == _emitKey)
        {
            return;
        }

        _emitKey = key;
        TaskChanged?.Invoke(text, done);
    }

    private void ParseLine(string line)
    {
        long ts = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("timestamp", out var tsEl) && tsEl.TryGetInt64(out ts))
            {
                if (ts > _lastAnyTs)
                {
                    _lastAnyTs = ts;
                }
            }
            else
            {
                return; // 没有时间戳的行无法参与排序
            }

            if (!root.TryGetProperty("type", out var typeEl))
            {
                return;
            }

            switch (typeEl.GetString())
            {
                case "function_call" when root.TryGetProperty("name", out var nameEl):
                {
                    var name = nameEl.GetString() ?? "";
                    var args = root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
                        ? a.GetString() ?? ""
                        : "";
                    var describe = Describe(name, args);
                    if (describe.Length > 0 && ts >= _lastActionTs)
                    {
                        _lastAction = describe;
                        _lastActionTs = ts;
                    }

                    break;
                }

                case "message":
                {
                    if (!root.TryGetProperty("role", out var roleEl) || roleEl.GetString() != "assistant")
                    {
                        return;
                    }

                    var text = ExtractText(root);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    if (ts >= _lastReplyTs)
                    {
                        _lastReply = Brief(text);
                        _lastReplyTs = ts;
                    }

                    break;
                }
            }
        }
        catch
        {
            // 单行解析失败直接跳过（比如被截断的半行）
        }
    }

    /// <summary>取 assistant 消息里最后一个 output_text 块的文本。</summary>
    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? last = null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is "output_text" or "text" && block.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                var text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    last = text;
                }
            }
        }

        return last;
    }

    /// <summary>把工具调用提炼成「动词 + 关键参数」的一行动作文字。</summary>
    private static string Describe(string name, string argsJson)
    {
        string path = "", str = "";
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("file_path", out var f) && f.ValueKind == JsonValueKind.String)
            {
                path = f.GetString() ?? "";
            }

            foreach (var key in new[] { "command", "pattern", "query", "url", "subject", "description", "skill" })
            {
                if (root.TryGetProperty(key, out var s) && s.ValueKind == JsonValueKind.String)
                {
                    str = s.GetString() ?? "";
                    break;
                }
            }
        }
        catch
        {
            // 参数不是合法 JSON 时只显示工具名
        }

        var fileName = string.IsNullOrEmpty(path)
            ? ""
            : path.Replace('\\', '/').Split('/')[^1];

        return name switch
        {
            "Bash" or "PowerShell" => "运行 " + CleanCommand(str),
            "Read" => "查看 " + fileName,
            "Write" => "写入 " + fileName,
            "Edit" or "MultiEdit" or "NotebookEdit" => "修改 " + fileName,
            "Grep" => "搜索 " + Cut(str, 30),
            "Glob" => "查找 " + Cut(str, 30),
            "WebSearch" => "检索 " + Cut(str, 40),
            "WebFetch" => "打开 " + Cut(str, 50),
            "TaskCreate" => "新建任务 " + Cut(str, 40),
            "TaskUpdate" => "更新任务",
            "Agent" or "Task" => "派出助手 " + Cut(str, 40),
            "Skill" => "调用技能 " + Cut(str, 30),
            "AskUserQuestion" => "等待用户选择",
            "" => "",
            _ => name,
        };
    }

    /// <summary>命令行摘要：丢掉纯 cd 段、压平空白、截断。</summary>
    private static string CleanCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "命令";
        }

        var parts = command.Split("&&");
        var kept = parts.Where(p =>
        {
            var t = p.Trim();
            return t.Length > 0 && !t.StartsWith("cd ", StringComparison.OrdinalIgnoreCase);
        });

        var joined = string.Join(" && ", kept).Trim();
        if (joined.Length == 0)
        {
            joined = parts[^1].Trim();
        }

        return Cut(joined, 60);
    }

    /// <summary>AI 回复摘要：第一个非空行，去掉 markdown 前缀符号。</summary>
    private static string Brief(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0)
            {
                continue;
            }

            t = t.TrimStart('#', '*', '-', '>', ' ').Trim();
            return t.Length == 0 ? "任务完成" : Cut(t, 60);
        }

        return "任务完成";
    }

    private static string Cut(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        _stop = true;
    }
}
