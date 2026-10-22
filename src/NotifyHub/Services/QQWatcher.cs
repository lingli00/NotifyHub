using NotifyHub.Models;
using NotifyHub.Services.Uia;

namespace NotifyHub.Services;

/// <summary>
/// QQ 监听：QQ 9.x/NT 是 Chromium 外壳（Chrome_WidgetWin_1），
/// 无障碍树是否可读取决于 QQ 自身的可访问性实现，读不到时按需求降级为
/// “QQ 收到一条新消息”，绝不抛异常、绝不阻塞其他模块。
/// </summary>
public sealed class QQWatcher : ChatAppWatcher
{
    private static readonly string[] Processes = { "QQ", "QQNT" };
    private static readonly string[] Classes = { "Chrome_WidgetWin_1" };

    /// <summary>降级通知最短间隔，避免把 UI 细节变化当成消息刷屏。</summary>
    private static readonly TimeSpan GenericCooldown = TimeSpan.FromSeconds(5);

    private string _lastTitle = string.Empty;
    private int _lastRawCount = -1;
    private DateTime _lastGenericAt = DateTime.MinValue;
    private bool _fallbackWarned;

    public QQWatcher(LogService log, ConfigService config)
        : base("QQ监听", Processes, Classes, log, config)
    {
    }

    protected override MessageSource Source => MessageSource.QQ;

    protected override bool IsEnabled(AppConfig config) => config.QQEnabled;

    protected override void PollOnce(AppConfig config)
    {
        var window = TryGetWindow(out var handle);
        if (window is null)
        {
            _lastTitle = string.Empty;
            _lastRawCount = -1;
            return;
        }

        var read = SessionListReader.Read(window);

        if (read.Rows.Count > 0)
        {
            if (_fallbackWarned)
            {
                Log.Info(Name, "QQ 会话列表已可读，退出降级模式");
                _fallbackWarned = false;
            }

            ApplyRows(read.Rows, read.Diagnostic);

            // 同步降级模式的参照值，避免下次切回降级时误报
            _lastTitle = WindowFinder.GetWindowTitle(handle);
            _lastRawCount = read.RawItemCount;
            return;
        }

        if (!_fallbackWarned)
        {
            Log.Warn(Name, "QQ 会话内容不可读，降级为“收到一条新消息”模式", read.Diagnostic);
            _fallbackWarned = true;
        }

        var title = WindowFinder.GetWindowTitle(handle);
        var titleChanged = _lastTitle.Length > 0 && !string.Equals(title, _lastTitle, StringComparison.Ordinal);
        var countChanged = _lastRawCount >= 0 && read.RawItemCount > 0 && read.RawItemCount != _lastRawCount;

        _lastTitle = title;
        _lastRawCount = read.RawItemCount;

        if (!titleChanged && !countChanged)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastGenericAt < GenericCooldown)
        {
            return;
        }

        _lastGenericAt = now;

        var reason = titleChanged ? $"标题变化: \"{LogService.Truncate(title, 40)}\"" : $"会话项数量变化: {_lastRawCount}";
        Log.Info(Name, "降级通知触发", reason);

        Emit(new NotifyMessage
        {
            Source = MessageSource.QQ,
            Sender = "QQ",
            Content = "QQ 收到一条新消息",
            Time = DateTime.Now,
            LowFidelity = true,
        });
    }
}
