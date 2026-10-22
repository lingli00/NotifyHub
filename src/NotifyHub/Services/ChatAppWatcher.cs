using System.Windows.Automation;
using NotifyHub.Models;
using NotifyHub.Services.Parsing;
using NotifyHub.Services.Uia;

namespace NotifyHub.Services;

/// <summary>
/// 聊天软件监听器公共部分：定位窗口 → 读会话列表 → 差分 → 触发通知。
/// 微信/QQ 只负责声明进程名与窗口类名，以及各自的降级策略。
/// </summary>
public abstract class ChatAppWatcher : PollingWatcherBase
{
    private readonly SessionDiffTracker _tracker = new();

    protected ChatAppWatcher(string name, string[] processNames, string[] classNames, LogService log, ConfigService config)
        : base(name, log, config)
    {
        ProcessNames = processNames;
        ClassNames = classNames;
    }

    protected string[] ProcessNames { get; }

    protected string[] ClassNames { get; }

    protected abstract MessageSource Source { get; }

    /// <summary>记录一次基线建立（子类可覆写做额外诊断）。</summary>
    protected virtual void OnBaseline(int rowCount)
    {
    }

    protected AutomationElement? TryGetWindow(out IntPtr handle)
    {
        handle = WindowFinder.FindMainWindow(ProcessNames, ClassNames);

        if (handle == IntPtr.Zero)
        {
            _tracker.Invalidate();
            LogThrottled(LogLevel.Debug, $"未找到主窗口（进程 {string.Join("/", ProcessNames)} 未运行或窗口类名变化）", TimeSpan.FromSeconds(60));
            return null;
        }

        try
        {
            var element = AutomationElement.FromHandle(handle);
            if (element is null)
            {
                LogThrottled(LogLevel.Warn, "AutomationElement.FromHandle 返回 null", TimeSpan.FromSeconds(60));
            }

            return element;
        }
        catch (Exception ex)
        {
            LogThrottled(LogLevel.Warn, "获取窗口 AutomationElement 失败: " + ex.Message, TimeSpan.FromSeconds(30));
            return null;
        }
    }

    /// <summary>把本轮会话列表与上一轮对比，产出新消息。</summary>
    protected void ApplyRows(IReadOnlyList<SessionRow> rows, string diagnostic)
    {
        if (rows.Count == 0)
        {
            LogThrottled(LogLevel.Debug, "会话列表为空: " + diagnostic, TimeSpan.FromSeconds(30));
            return;
        }

        var changes = _tracker.Diff(rows, DateTime.Now, out var baseline);

        if (baseline)
        {
            Log.Info(Name, $"建立基线：{rows.Count} 个会话（本轮不通知）");
            OnBaseline(rows.Count);

            // 基线轮也可能带出“恢复期补发”（读列表中断超过 30 秒后与旧快照的差集），
            // 这些是真实到达过的消息，不能跟着基线一起丢掉
            EmitChanges(changes);
            return;
        }

        EmitChanges(changes);
    }

    private void EmitChanges(IReadOnlyList<SessionRow> changes)
    {
        var config = CurrentConfig;

        foreach (var row in changes)
        {
            if (string.IsNullOrWhiteSpace(row.Summary))
            {
                continue;
            }

            // 接收阶段过滤（不弹窗、不响铃、不进事件列表）
            if (!ShouldEmit(row, config))
            {
                continue;
            }

            Log.Info(Name, $"检测到新消息: {row.Contact} → {LogService.Truncate(row.Summary, 60)}");
            Emit(new NotifyMessage
            {
                Source = Source,
                Sender = row.Contact,
                Content = row.Summary,
                Time = DateTime.Now,
            });
        }
    }

    /// <summary>
    /// 是否放行该会话（子类可覆写做过滤）。默认全部放行（QQ 不做过滤）。
    /// </summary>
    protected virtual bool ShouldEmit(SessionRow row, AppConfig config) => true;
}
