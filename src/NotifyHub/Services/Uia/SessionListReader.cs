using System.Text;
using System.Windows.Automation;
using NotifyHub.Services.Parsing;

namespace NotifyHub.Services.Uia;

/// <summary>
/// 从微信 / QQ 主窗口的 UIA 树里读出“会话列表”。
/// 策略：先找 ControlType.List（按名字/条数打分挑最像会话列表的那个），
/// 再用一次 Subtree 缓存把整张列表拉回本地，避免逐项跨进程取值。
/// 任何一步失败都返回空结果 + 诊断字符串，由上层决定是否降级。
/// </summary>
public static class SessionListReader
{
    public sealed record Result(List<SessionRow> Rows, int RawItemCount, string Diagnostic)
    {
        public static Result Empty(string diagnostic) => new(new List<SessionRow>(), 0, diagnostic);
    }

    private static readonly string[] ListNameHints = { "会话", "聊天", "消息", "最近", "Session", "Chat", "Message" };

    public static Result Read(AutomationElement window)
    {
        var rows = new List<SessionRow>();

        AutomationElement? bestList = null;
        var bestScore = int.MinValue;
        var listCount = 0;

        try
        {
            var listCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List);
            var request = CreateRequest(TreeScope.Element);

            using (request.Activate())
            {
                var lists = window.FindAll(TreeScope.Descendants, listCondition);
                listCount = lists.Count;

                for (var i = 0; i < lists.Count && i < 24; i++)
                {
                    try
                    {
                        var candidate = lists[i];
                        var name = SafeCachedName(candidate) ?? string.Empty;
                        var score = 0;

                        if (ListNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                        {
                            score += 1000;
                        }

                        var itemCount = candidate.FindAll(
                            TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;

                        score += Math.Min(itemCount, 200);

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestList = candidate;
                        }
                    }
                    catch
                    {
                        // 单个候选列表失效忽略
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return Result.Empty($"枚举 List 失败: {ex.GetType().Name} {ex.Message}");
        }

        if (bestList is null)
        {
            return Result.Empty($"未找到 ControlType.List（候选 {listCount} 个）");
        }

        AutomationElementCollection items;
        try
        {
            // Subtree 缓存：一次跨进程调用拿回整张会话列表
            var request = CreateRequest(TreeScope.Subtree);
            using (request.Activate())
            {
                items = bestList.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            }
        }
        catch (Exception ex)
        {
            return Result.Empty($"读取会话项失败: {ex.GetType().Name} {ex.Message}");
        }

        var rawCount = items.Count;
        for (var i = 0; i < items.Count && i < 80; i++)
        {
            try
            {
                var texts = new List<string>(8);
                CollectTexts(items[i], texts, 0, 12);

                // 额外收集“信号文本”：图标/按钮的名称、说明、自动化 ID，
                // 用于识别免打扰（“消息免打扰”图标）与公众号/服务通知标识
                var signals = new List<string>(16);
                CollectSignals(items[i], signals, 0);

                var row = MessageTextParser.Parse(texts);
                if (row is not null && row.Contact.Length > 0)
                {
                    var signalText = string.Join("|", signals);
                    if (signalText.Length > 800)
                    {
                        signalText = signalText[..800];
                    }

                    var muted = signalText.Contains("免打扰", StringComparison.Ordinal)
                                || signalText.Contains("静音", StringComparison.Ordinal)
                                || signalText.Contains("mute", StringComparison.OrdinalIgnoreCase);

                    rows.Add(row with { Muted = muted, Signals = signalText });
                }
            }
            catch
            {
                // 单项失败忽略，继续下一项
            }
        }

        var diag = new StringBuilder();
        diag.Append($"List=\"{SafeCachedName(bestList)}\" 候选={listCount} 原始项={rawCount} 解析成功={rows.Count}");
        return new Result(rows, rawCount, diag.ToString());
    }

    private static CacheRequest CreateRequest(TreeScope scope)
    {
        var request = new CacheRequest
        {
            TreeFilter = Automation.ControlViewCondition,
            TreeScope = scope,
            AutomationElementMode = AutomationElementMode.Full,
        };

        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.ClassNameProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.AutomationIdProperty);

        try
        {
            request.Add(AutomationElement.HelpTextProperty);
        }
        catch
        {
            // 某些 provider 不支持 HelpText 缓存，忽略
        }

        return request;
    }

    /// <summary>
    /// 收集“信号文本”：任意类型元素的 Name + HelpText + AutomationId。
    /// 微信会话项里的免打扰状态是以图标形式给出的，其 Name 常为“消息免打扰”。
    /// </summary>
    private static void CollectSignals(AutomationElement element, List<string> sink, int depth)
    {
        if (depth > 4 || sink.Count >= 40)
        {
            return;
        }

        try
        {
            var name = SafeCachedName(element);
            if (!string.IsNullOrWhiteSpace(name))
            {
                sink.Add(name!);
            }

            var help = SafeCachedHelpText(element);
            if (!string.IsNullOrWhiteSpace(help))
            {
                sink.Add(help!);
            }

            var automationId = SafeCachedAutomationId(element);
            if (!string.IsNullOrWhiteSpace(automationId))
            {
                sink.Add(automationId!);
            }

            AutomationElementCollection children;
            try
            {
                children = element.CachedChildren;
            }
            catch
            {
                return;
            }

            for (var i = 0; i < children.Count && sink.Count < 40; i++)
            {
                CollectSignals(children[i], sink, depth + 1);
            }
        }
        catch
        {
            // 元素失效忽略
        }
    }

    private static string? SafeCachedHelpText(AutomationElement element)
    {
        try
        {
            return element.Cached.HelpText;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeCachedAutomationId(AutomationElement element)
    {
        try
        {
            return element.Cached.AutomationId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>收集一个会话项里的文本（含自身 Name 与后代 Text/Edit）。</summary>
    private static void CollectTexts(AutomationElement element, List<string> sink, int depth, int maxTexts)
    {
        if (depth > 5 || sink.Count >= maxTexts)
        {
            return;
        }

        try
        {
            var name = SafeCachedName(element);
            var type = SafeCachedControlType(element);

            if (!string.IsNullOrWhiteSpace(name)
                && (type == ControlType.Text || type == ControlType.Edit || type == ControlType.ListItem))
            {
                sink.Add(name!);
            }

            AutomationElementCollection children;
            try
            {
                children = element.CachedChildren;
            }
            catch
            {
                return;
            }

            for (var i = 0; i < children.Count && sink.Count < maxTexts; i++)
            {
                CollectTexts(children[i], sink, depth + 1, maxTexts);
            }
        }
        catch
        {
            // 元素失效忽略
        }
    }

    private static string? SafeCachedName(AutomationElement element)
    {
        try
        {
            return element.Cached.Name;
        }
        catch
        {
            try
            {
                return element.Current.Name;
            }
            catch
            {
                return null;
            }
        }
    }

    private static ControlType? SafeCachedControlType(AutomationElement element)
    {
        try
        {
            return element.Cached.ControlType;
        }
        catch
        {
            return null;
        }
    }
}
