using System.Windows.Automation;
using NotifyHub.Models;
using NotifyHub.Services.Parsing;
using NotifyHub.Services.Uia;

namespace NotifyHub.Services;

/// <summary>
/// 聊天软件监听器公共部分：定位窗口 → 读会话列表 → 差分 → 触发通知。
/// 微信/QQ 只负责声明进程名与窗口类名，以及各自的降级策略。
///
/// 需求 7（消息游标）：启动时先用持久化游标（cursor.json 里“最后一条已展示消息”
/// 的边界）建基线——有游标只放行摘要发生变化的消息，且必须带未读证据
/// （微信 4.x 的“[N条]”前缀），用户早已看过的东西绝不重弹；
/// 首次运行（无游标）静默建基线，不补弹任何历史。
/// 运行期（UIA 读空超过 30 秒后的恢复补发）同样要求未读证据。
/// </summary>
public abstract class ChatAppWatcher : PollingWatcherBase
{
    private readonly SessionDiffTracker _tracker = new();
    private readonly MessageCursorStore _cursorStore;
    private bool _startupRound;

    protected ChatAppWatcher(
        string name,
        string[] processNames,
        string[] classNames,
        LogService log,
        ConfigService config,
        MessageCursorStore cursorStore)
        : base(name, log, config)
    {
        ProcessNames = processNames;
        ClassNames = classNames;
        _cursorStore = cursorStore;
    }

    protected string[] ProcessNames { get; }

    protected string[] ClassNames { get; }

    protected abstract MessageSource Source { get; }

    /// <summary>游标存储键（"wechat" / "qq"）。</summary>
    protected abstract string SourceKey { get; }

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

        var now = DateTime.Now;

        // 需求 7：启动时按持久化游标建立“已展示边界”（只弹游标之后的新消息）
        if (!_tracker.HasBaseline)
        {
            var snapshot = _cursorStore.GetSnapshot(SourceKey);
            if (snapshot is { Count: > 0 })
            {
                _tracker.PrimeWith(snapshot, now);
                _startupRound = true;
                Log.Info(Name, $"按消息游标建立基线：{snapshot.Count} 个会话（不回溯历史）");
            }
        }

        var changes = _tracker.Diff(rows, now, out var baseline);

        if (baseline)
        {
            Log.Info(Name, $"建立基线：{rows.Count} 个会话（本轮不通知）");
            OnBaseline(rows.Count);

            // 基线轮也可能带出“恢复期补发”（读列表中断超过 30 秒后与旧快照的差集）。
            // 这些可能是真实到达过的消息，但也可能是用户早已看过、只是 UIA 抽风的老消息
            // ——必须带未读证据才放行（需求 7：打开微信不重弹旧消息）。
            EmitChanges(changes, requireUnread: true);
            return;
        }

        if (_startupRound)
        {
            // 游标启动后的第一轮：放行“游标之后的新消息”，同样要求未读证据，
            // 软件没运行期间用户已看过的消息一律不补弹
            _startupRound = false;
            EmitChanges(changes, requireUnread: true);
            return;
        }

        EmitChanges(changes, requireUnread: false);
    }

    private void EmitChanges(IReadOnlyList<SessionRow> changes, bool requireUnread)
    {
        var config = CurrentConfig;

        foreach (var row in changes)
        {
            if (string.IsNullOrWhiteSpace(row.Summary))
            {
                continue;
            }

            // 恢复补发 / 启动回溯：没有未读证据的一律按“用户已看过”处理
            if (requireUnread && !row.HasUnread)
            {
                LogThrottled(LogLevel.Debug,
                    $"跳过无未读证据的旧消息: {row.Contact}", TimeSpan.FromSeconds(30));
                continue;
            }

            // 接收阶段过滤（不弹窗、不响铃、不进事件列表）
            if (!ShouldEmit(row, config))
            {
                continue;
            }

            Log.Info(Name, $"检测到新消息: {row.Contact} → {LogService.Truncate(row.Summary, 60)}");

            // 消息游标：记录“最后一条已展示消息”的边界，重启后从这里继续
            _cursorStore.Update(SourceKey, row.Contact, row.Summary);

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
