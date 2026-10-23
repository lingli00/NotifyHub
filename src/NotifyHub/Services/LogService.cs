using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using NotifyHub.Models;
using EventLogEntry = NotifyHub.Models.EventLogEntry;

namespace NotifyHub.Services;

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warn,
    Error,
}

/// <summary>控制台展示用的一行日志（带级别，供着色）。</summary>
public sealed record ConsoleLine(DateTime Time, LogLevel Level, string Module, string Text);

/// <summary>
/// 日志 + 最近事件环形缓冲 + 控制台日志缓冲。日志写入绝不抛异常
/// （日志本身失败不能拖垮业务）。事件日志同时落盘到 events.json。
/// </summary>
public sealed class LogService
{
    public const int MaxRecentEvents = 50;
    public const int MaxConsoleLines = 600;

    private const long MaxLogBytes = 4L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly List<EventLogEntry> _recent = new(MaxRecentEvents + 1);
    private readonly List<ConsoleLine> _console = new(MaxConsoleLines + 1);
    private string _logFile = string.Empty;
    private string _eventFile = AppPaths.EventFile;
    private string _logDir = AppPaths.LogDir;
    private bool _initialized;
    private bool _dirty;

    public static LogService Shared { get; } = new();

    /// <summary>事件列表变化（可能来自后台线程，订阅方需自行切到 UI 线程）。</summary>
    public event EventHandler? RecentChanged;

    /// <summary>控制台日志变化（可能来自后台线程，订阅方需自行切到 UI 线程）。</summary>
    public event EventHandler? ConsoleChanged;

    /// <summary>当前日志保存目录（默认 AppPaths.LogDir，可在设置页自定义）。</summary>
    public string LogDir
    {
        get { lock (_gate) { return _logDir; } }
    }

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                AppPaths.EnsureCreated();
                _logDir = AppPaths.LogDir;
                _logFile = Path.Combine(_logDir, $"NotifyHub-{DateTime.Now:yyyyMMdd}.log");
                _eventFile = AppPaths.EventFile;
                LoadEventsLocked();
            }
            catch
            {
                // 目录不可写也不能崩，退化为仅内存日志
            }

            _initialized = true;
        }

        Write(LogLevel.Info, "App", $"日志初始化完成，目录={_logDir}");
    }

    /// <summary>
    /// 切换日志保存目录（v0.2.4 需求 8）：立即把后续日志写到新目录，
    /// 事件列表文件（events.json）一并迁移到新目录。失败返回 false。
    /// </summary>
    public bool SetLogDir(string? dir)
    {
        var target = (dir ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(target);
        }
        catch
        {
            return false;
        }

        lock (_gate)
        {
            _logDir = target;
            _logFile = Path.Combine(target, $"NotifyHub-{DateTime.Now:yyyyMMdd}.log");

            var newEventFile = Path.Combine(target, "events.json");
            if (!string.Equals(newEventFile, _eventFile, StringComparison.OrdinalIgnoreCase))
            {
                // 旧事件文件搬过来（不覆盖已存在的目标），然后从新位置重新加载
                try
                {
                    if (File.Exists(_eventFile) && !File.Exists(newEventFile))
                    {
                        File.Move(_eventFile, newEventFile);
                    }
                }
                catch
                {
                    // 搬运失败不影响切换，事件从新目录重新开始
                }

                _eventFile = newEventFile;
                _recent.Clear();
                LoadEventsLocked();
            }
        }

        Write(LogLevel.Info, "Log", "日志目录已切换", _logDir);

        try
        {
            RecentChanged?.Invoke(this, EventArgs.Empty);
            ConsoleChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 忽略
        }

        return true;
    }

    public void Info(string module, string message, string? detail = null) => Write(LogLevel.Info, module, message, detail);

    public void Success(string module, string message, string? detail = null) => Write(LogLevel.Success, module, message, detail);

    public void Warn(string module, string message, string? detail = null) => Write(LogLevel.Warn, module, message, detail);

    public void Error(string module, string message, string? detail = null) => Write(LogLevel.Error, module, message, detail);

    public void Write(LogLevel level, string module, string message, string? detail = null)
    {
        var detailText = detail is null ? string.Empty : " | " + detail;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToTag()}] [{module}] {message}{detailText}";

        Debug.WriteLine(line);

        try
        {
            lock (_gate)
            {
                // 控制台缓冲（内存环形，最多 600 行）
                _console.Add(new ConsoleLine(DateTime.Now, level, module, message + detailText));
                while (_console.Count > MaxConsoleLines)
                {
                    _console.RemoveAt(0);
                }

                if (_logFile.Length == 0)
                {
                    return;
                }

                RotateIfNeededLocked();
                File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 忽略：日志写失败不允许影响主流程
        }

        try
        {
            ConsoleChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 订阅方异常不影响日志
        }
    }

    /// <summary>当前正在写的日志文件（v0.2.10 需求 6：Bug 快照要按时间窗回读它）。</summary>
    public string CurrentLogFile
    {
        get
        {
            lock (_gate)
            {
                return _logFile;
            }
        }
    }

    /// <summary>控制台日志快照（最新在末尾）。</summary>
    public IReadOnlyList<ConsoleLine> ConsoleSnapshot()
    {
        lock (_gate)
        {
            return _console.ToList();
        }
    }

    /// <summary>
    /// 一键清除全部日志：内存控制台缓冲、最近事件、日志目录下的日志文件、events.json。
    /// 【不】删除 config.json，【不】删除 dedup.json（去重记录必须保留）。
    /// </summary>
    public (int DeletedFiles, bool Success) ClearAll()
    {
        lock (_gate)
        {
            _recent.Clear();
            _console.Clear();
            _dirty = false;
        }

        var deleted = 0;
        var success = true;

        try
        {
            if (Directory.Exists(_logDir))
            {
                foreach (var file in Directory.GetFiles(_logDir, "*.log"))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch
                    {
                        success = false;
                    }
                }
            }
        }
        catch
        {
            success = false;
        }

        try
        {
            if (File.Exists(_eventFile))
            {
                File.Delete(_eventFile);
                deleted++;
            }
        }
        catch
        {
            success = false;
        }

        // 重新指向当天的日志文件（前面可能刚被删掉）
        lock (_gate)
        {
            try
            {
                _logFile = Path.Combine(_logDir, $"NotifyHub-{DateTime.Now:yyyyMMdd}.log");
            }
            catch
            {
                _logFile = string.Empty;
            }
        }

        Write(LogLevel.Info, "Log", "全部日志已清除（配置与去重记录保留）", $"删除文件 {deleted} 个");

        try
        {
            RecentChanged?.Invoke(this, EventArgs.Empty);
            ConsoleChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 忽略
        }

        return (deleted, success);
    }

    /// <summary>记录一条业务事件（进最近 50 条 + 落盘）。</summary>
    public void AddEvent(NotifyMessage message, EventStatus status, string? note = null)
    {
        var entry = new EventLogEntry
        {
            Time = message.Time,
            Source = message.SourceText,
            Sender = Truncate(message.Sender, 60),
            Content = Truncate(message.Content, 120),
            Status = status,
            Note = Truncate(note ?? string.Empty, 120),
        };

        lock (_gate)
        {
            _recent.Insert(0, entry);
            while (_recent.Count > MaxRecentEvents)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }

            _dirty = true;
        }

        Write(
            status == EventStatus.Error ? LogLevel.Warn : LogLevel.Info,
            "Event",
            $"{entry.Source} | {entry.Sender} | {entry.Content} | {entry.StatusText}",
            string.IsNullOrEmpty(entry.Note) ? null : entry.Note);

        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddInfoEvent(string source, string content)
    {
        AddEvent(
            new NotifyMessage
            {
                Source = MessageSource.Ntfy,
                Sender = source,
                Content = content,
                Time = DateTime.Now,
            },
            EventStatus.Info);
    }

    public IReadOnlyList<EventLogEntry> RecentSnapshot()
    {
        lock (_gate)
        {
            return _recent.ToList();
        }
    }

    /// <summary>有变更则落盘（由 UI 定时器调用，避免每写一条都读磁盘）。</summary>
    public void FlushIfDirty()
    {
        string? json = null;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            try
            {
                json = JsonSerializer.Serialize(_recent, JsonOptions);
            }
            catch
            {
                return;
            }
        }

        try
        {
            var tmp = _eventFile + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _eventFile, overwrite: true);
        }
        catch
        {
            // 忽略
        }
    }

    public static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? string.Empty;
        }

        return value[..max] + "…";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private void RotateIfNeededLocked()
    {
        if (_logFile.Length == 0)
        {
            return;
        }

        var info = new FileInfo(_logFile);
        if (!info.Exists || info.Length < MaxLogBytes)
        {
            return;
        }

        var backup = Path.Combine(_logDir, $"NotifyHub-{DateTime.Now:yyyyMMdd-HHmmss}.old.log");
        try
        {
            File.Move(_logFile, backup, overwrite: true);
        }
        catch
        {
            // 忽略
        }
    }

    private void LoadEventsLocked()
    {
        try
        {
            if (!File.Exists(_eventFile))
            {
                return;
            }

            var list = JsonSerializer.Deserialize<List<EventLogEntry>>(File.ReadAllText(_eventFile), JsonOptions);
            if (list is null)
            {
                return;
            }

            foreach (var item in list.Take(MaxRecentEvents))
            {
                _recent.Add(item);
            }
        }
        catch
        {
            // 事件文件损坏就当没有，不影响启动
        }
    }
}

internal static class LogLevelExtensions
{
    public static string ToTag(this LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Success => "SUC",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "???",
    };
}
