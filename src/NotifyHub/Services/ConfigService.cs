using System.IO;
using System.Text.Json;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 配置读写。读取失败一律降级为默认配置并把坏文件另存备份，
/// 绝不让一个坏 JSON 阻止程序启动。
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private readonly LogService _log;
    private AppConfig _current = new();

    public ConfigService(LogService log)
    {
        _log = log;
        _current.Normalize();
    }

    /// <summary>配置变更（保存成功后触发，可能在任意线程）。</summary>
    public event EventHandler<AppConfig>? Changed;

    public string FilePath => AppPaths.ConfigFile;

    /// <summary>当前配置。后台线程只读，保存时整体替换实例。</summary>
    public AppConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public AppConfig Snapshot() => Current.Clone();

    public void Load()
    {
        AppConfig? loaded = null;

        try
        {
            AppPaths.EnsureCreated();
            if (File.Exists(AppPaths.ConfigFile))
            {
                var json = File.ReadAllText(AppPaths.ConfigFile);
                loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Config", "配置文件解析失败，使用默认配置", ex.Message);
            TryBackupBrokenFile();
        }

        var config = loaded ?? new AppConfig();
        config.Normalize();

        lock (_gate)
        {
            _current = config;
        }

        _log.Info("Config", $"配置已加载: 微信={config.WeChatEnabled} QQ={config.QQEnabled} ntfy={config.NtfyEnabled} 间隔={config.PollIntervalMs}ms 时长={config.NotificationDurationMs}ms");

        // 首次运行或文件缺失时立刻落盘一份，用户能直接看到文件
        if (!File.Exists(AppPaths.ConfigFile))
        {
            Save(config, raiseChanged: false);
        }
    }

    public bool Save(AppConfig config, bool raiseChanged = true)
    {
        var next = config.Clone();
        next.Normalize();

        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(next, JsonOptions);
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Error("Config", "配置保存失败", ex.Message);
            return false;
        }

        lock (_gate)
        {
            _current = next;
        }

        _log.Info("Config", "配置已保存");
        if (raiseChanged)
        {
            Changed?.Invoke(this, next);
        }

        return true;
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            var backup = Path.Combine(AppPaths.Root, $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(AppPaths.ConfigFile, backup, overwrite: true);
            _log.Warn("Config", "坏配置已备份", backup);
        }
        catch
        {
            // 忽略
        }
    }
}
