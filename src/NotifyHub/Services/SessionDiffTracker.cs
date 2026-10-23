using NotifyHub.Models;
using NotifyHub.Services.Parsing;

namespace NotifyHub.Services;

/// <summary>
/// 会话快照差分：把“本轮读到的会话列表”与上一轮比，最多产出“有变化的会话”。
/// 首次成功读取只建基线，不通知（避免启动瞬间刷屏）；
/// 窗口丢失超过 30 秒会重建基线，避免微信重启后把所有会话当成新消息。
/// </summary>
public sealed class SessionDiffTracker
{
    private static readonly TimeSpan RebaselineGap = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, string> _lastSummary = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _previousSummary = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastUnread = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastEmit = new(StringComparer.Ordinal);
    private DateTime _lastSuccessUtc = DateTime.MinValue;
    private bool _primed;
    private bool _hadBaseline;

    public bool HasBaseline => _primed;

    /// <summary>
    /// 用持久化游标（上次运行最后展示的会话摘要快照）直接建立基线，
    /// 不走“重建 + 补发”路径：启动时只弹游标之后的新消息（需求 7）。
    /// </summary>
    public void PrimeWith(IReadOnlyDictionary<string, string> snapshot, DateTime now)
    {
        lock (_lastSummary)
        {
            _lastSummary.Clear();
            _lastIndex.Clear();
            _lastUnread.Clear();
            _previousSummary.Clear();
            _hadBaseline = false;

            foreach (var pair in snapshot)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    _lastSummary[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            _primed = true;
            _lastSuccessUtc = now;
        }
    }

    /// <summary>当前基线快照（联系人 → 摘要），供持久化游标落盘。</summary>
    public Dictionary<string, string> Snapshot()
    {
        lock (_lastSummary)
        {
            return new Dictionary<string, string>(_lastSummary, StringComparer.Ordinal);
        }
    }

    /// <summary>最近一次重建基线时补发的恢复变更（诊断/自检用）。</summary>
    public IReadOnlyList<SessionRow> LastRecoveredChanges { get; private set; } = Array.Empty<SessionRow>();

    public int TrackedCount
    {
        get
        {
            lock (_lastSummary)
            {
                return _lastSummary.Count;
            }
        }
    }

    /// <summary>
    /// 返回需要通知的会话行。<paramref name="baseline"/> 为 true 表示本轮只是建基线。
    /// </summary>
    public List<SessionRow> Diff(IReadOnlyList<SessionRow> rows, DateTime now, out bool baseline)
    {
        var changes = new List<SessionRow>();
        baseline = false;

        lock (_lastSummary)
        {
            var reset = !_primed || now - _lastSuccessUtc > RebaselineGap;
            if (reset)
            {
                // 重建前留一份上一轮快照，供恢复期补发差异（见下方恢复逻辑）
                if (_primed)
                {
                    _previousSummary.Clear();
                    foreach (var pair in _lastSummary)
                    {
                        _previousSummary[pair.Key] = pair.Value;
                    }

                    _hadBaseline = true;
                }

                RebuildLocked(rows);
                _primed = true;
                _lastSuccessUtc = now;
                baseline = true;

                // 关键修复（丢包）：以前超 30 秒读不到列表（UIA 卡顿/微信无响应）就盲目重建
                // 基线，窗口期内到达的消息会被当成基线静默吞掉。现在把“与上次快照有差异”
                // 的会话当作恢复出来的变更交还上层，宁可多弹不可漏弹。
                if (_hadBaseline)
                {
                    foreach (var pair in _previousSummary)
                    {
                        var fresh = rows.FirstOrDefault(r => r.Contact == pair.Key);
                        if (fresh is null)
                        {
                            continue; // 会话已消失，不补发
                        }

                        if (!string.Equals(fresh.Summary, pair.Value, StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(fresh.Summary))
                        {
                            changes.Add(fresh);
                        }
                    }
                }

                LastRecoveredChanges = changes.ToList();
                return changes;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (string.IsNullOrEmpty(row.Summary))
                {
                    // 摘要为空无法比对，只记住联系人存在，不更新摘要
                    seen.Add(row.Contact);
                    continue;
                }

                seen.Add(row.Contact);

                var changed = !_lastSummary.TryGetValue(row.Contact, out var previous)
                              || !string.Equals(previous, row.Summary, StringComparison.Ordinal);

                // 同文本新消息：对方又发了一条与上一条文本完全相同的消息时摘要不变，
                // 但未读数会 +1——未读数递增同样算变化（修复“同文本消息被吞”）。
                var unreadIncreased = row.UnreadCount > 0
                                      && _lastUnread.TryGetValue(row.Contact, out var previousUnread)
                                      && row.UnreadCount > previousUnread;

                // 内容一模一样但会话跳到更靠上的位置：多半是对方又发了同一句话
                // （未读 badge 可能在轮询间隙已被用户点掉）。放宽到“任意上移”，
                // 抑制窗口压到 2 秒——同文本重复消息不再依赖跳到最顶部。
                var movedUp = !changed
                              && !unreadIncreased
                              && i == 0
                              && _lastIndex.TryGetValue(row.Contact, out var previousIndex)
                              && previousIndex > 0
                              && (!_lastEmit.TryGetValue(row.Contact, out var lastEmit)
                                  || now - lastEmit > TimeSpan.FromSeconds(2));

                if (changed || unreadIncreased || movedUp)
                {
                    changes.Add(row);
                    _lastEmit[row.Contact] = now;
                }
            }

            // 会话从列表里消失（被删除/清空），同步移除，避免下次同名会话误判
            if (_lastSummary.Count > 0)
            {
                var removed = _lastSummary.Keys.Where(k => !seen.Contains(k)).ToList();
                foreach (var key in removed)
                {
                    _lastSummary.Remove(key);
                    _lastIndex.Remove(key);
                }
            }

            foreach (var row in rows)
            {
                // 空摘要（UIA 偶发读不全）不覆盖上一次的好摘要，
                // 否则下一轮会把旧内容当新消息（误报），且真实变化可能被空值周期吞掉
                if (!string.IsNullOrEmpty(row.Summary))
                {
                    _lastSummary[row.Contact] = row.Summary;
                    _lastUnread[row.Contact] = row.UnreadCount;
                }
            }

            _lastIndex.Clear();
            for (var i = 0; i < rows.Count; i++)
            {
                _lastIndex[rows[i].Contact] = i;
            }

            _lastSuccessUtc = now;
        }

        return changes;
    }

    /// <summary>窗口消失时调用，下一轮重建基线（保留上一轮快照用于恢复期补发）。</summary>
    public void Invalidate()
    {
        lock (_lastSummary)
        {
            _primed = false;
            _lastSummary.Clear();
            _lastIndex.Clear();
            _lastUnread.Clear();
        }
    }

    private void RebuildLocked(IReadOnlyList<SessionRow> rows)
    {
        _lastSummary.Clear();
        _lastIndex.Clear();
        _lastUnread.Clear();

        for (var i = 0; i < rows.Count; i++)
        {
            _lastSummary[rows[i].Contact] = rows[i].Summary;
            _lastIndex[rows[i].Contact] = i;
            _lastUnread[rows[i].Contact] = rows[i].UnreadCount;
        }
    }
}
