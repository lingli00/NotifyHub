using System.Windows.Automation;
using NotifyHub.Interop;
using NotifyHub.Models;
using NotifyHub.Services.Uia;

namespace NotifyHub.Services;

/// <summary>
/// 点击通知后的“定位聊天”：激活目标窗口 → 找到搜索框（ControlType.Edit）→
/// ValuePattern 填入发件人 → SendInput 回车。ValuePattern 不可用时退化为
/// 聚焦 + Unicode 模拟输入；再不行用 Ctrl+F 全局搜索兜底。
/// 全部在后台线程调用，不阻塞 UI。
/// </summary>
public static class WindowActivator
{
    private static readonly string[] WeChatProcesses = { "WeChat", "Weixin" };
    private static readonly string[] WeChatClasses = { "WeChatMainWndForPC", "WeChatMainWndForPC1", "mmui::MainWindow" };
    private static readonly string[] QQProcesses = { "QQ", "QQNT" };
    private static readonly string[] QQClasses = { "Chrome_WidgetWin_1" };

    public static bool TryOpenConversation(NotifyMessage message, LogService log)
    {
        if (message.Source == MessageSource.Ntfy)
        {
            log.Info("定位", "ntfy 消息没有对应聊天窗口，忽略定位请求");
            return false;
        }

        var isWeChat = message.Source == MessageSource.WeChat;
        var processNames = isWeChat ? WeChatProcesses : QQProcesses;
        var classNames = isWeChat ? WeChatClasses : QQClasses;

        try
        {
            var hwnd = WindowFinder.FindMainWindow(processNames, classNames);
            if (hwnd == IntPtr.Zero)
            {
                log.Warn("定位", $"未找到{(isWeChat ? "微信" : "QQ")}主窗口，无法定位聊天");
                return false;
            }

            if (!NativeMethods.ActivateWindow(hwnd))
            {
                log.Warn("定位", "窗口置前失败，仍尝试写入搜索框");
            }

            Thread.Sleep(220);

            var ok = TryFillSearchBoxAndEnter(hwnd, message.Sender, log);
            if (!ok)
            {
                log.Info("定位", "未找到可用搜索框，尝试 Ctrl+F 兜底");
                ok = TryGlobalSearch(hwnd, message.Sender, log);
            }

            if (ok)
            {
                log.Info("定位", $"已尝试跳转到聊天: {message.Sender}");
            }
            else
            {
                log.Warn("定位", $"定位失败（目标 UI 结构可能已变化）: {message.Sender}");
            }

            return ok;
        }
        catch (Exception ex)
        {
            log.Error("定位", "定位聊天异常", $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryFillSearchBoxAndEnter(IntPtr hwnd, string sender, LogService log)
    {
        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception ex)
        {
            log.Warn("定位", "获取窗口元素失败", ex.Message);
            return false;
        }

        if (root is null)
        {
            return false;
        }

        var candidates = new List<(AutomationElement Element, bool NameHit, double Top)>();

        try
        {
            var edits = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

            for (var i = 0; i < edits.Count && i < 40; i++)
            {
                try
                {
                    var current = edits[i].Current;
                    if (!current.IsEnabled || current.IsOffscreen)
                    {
                        continue;
                    }

                    var name = current.Name ?? string.Empty;
                    var nameHit = name.Contains("搜索", StringComparison.OrdinalIgnoreCase)
                                  || name.Contains("search", StringComparison.OrdinalIgnoreCase);

                    var rect = current.BoundingRectangle;
                    var top = double.IsNaN(rect.Y) ? double.MaxValue : rect.Y;

                    candidates.Add((edits[i], nameHit, top));
                }
                catch
                {
                    // 元素失效跳过
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("定位", "枚举 Edit 控件失败", ex.Message);
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        // 名字命中优先，其次取最靠上的（搜索框在窗口顶部，消息输入框在底部）
        var target = candidates
            .OrderByDescending(c => c.NameHit)
            .ThenBy(c => c.Top)
            .First();

        var filled = false;

        try
        {
            if (target.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                && pattern is ValuePattern valuePattern)
            {
                valuePattern.SetValue(sender);
                filled = true;
                log.Info("定位", "已通过 ValuePattern 填写搜索框");
            }
        }
        catch (Exception ex)
        {
            log.Warn("定位", "ValuePattern 填写失败，改用模拟输入", ex.Message);
        }

        if (!filled)
        {
            try
            {
                target.Element.SetFocus();
                Thread.Sleep(120);
                NativeMethods.SendText(sender);
                filled = true;
                log.Info("定位", "已通过模拟输入填写搜索框");
            }
            catch (Exception ex)
            {
                log.Warn("定位", "模拟输入失败", ex.Message);
            }
        }

        if (!filled)
        {
            return false;
        }

        Thread.Sleep(320);
        NativeMethods.SendKey(NativeMethods.VK_RETURN);
        Thread.Sleep(260);
        return true;
    }

    private static bool TryGlobalSearch(IntPtr hwnd, string sender, LogService log)
    {
        try
        {
            NativeMethods.ActivateWindow(hwnd);
            Thread.Sleep(150);
            NativeMethods.SendCombo(NativeMethods.VK_CONTROL, NativeMethods.VK_F);
            Thread.Sleep(250);
            NativeMethods.SendText(sender);
            Thread.Sleep(320);
            NativeMethods.SendKey(NativeMethods.VK_RETURN);
            Thread.Sleep(220);
            return true;
        }
        catch (Exception ex)
        {
            log.Warn("定位", "Ctrl+F 兜底失败", ex.Message);
            return false;
        }
    }
}
