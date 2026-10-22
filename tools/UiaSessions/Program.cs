// 探测微信/QQ 会话列表每个 ListItem 的完整 UIA 子树（不受 ControlView 深度限制）。
// 用法: UiaSessions <app> [maxItems]   app = wechat | qq
using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: UiaSessions <wechat|qq> [maxItems]");
            return 1;
        }

        var app = args[0].ToLowerInvariant();
        var maxItems = args.Length > 1 && int.TryParse(args[1], out var m) ? m : 12;
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

        string[] processes = app == "qq" ? new[] { "QQ" } : new[] { "WeChat", "Weixin" };
        string[] classes = app == "qq"
            ? new[] { "Chrome_WidgetWin_1" }
            : new[] { "WeChatMainWndForPC", "WeChatMainWndForPC1", "mmui::MainWindow" };

        IntPtr main = IntPtr.Zero;
        var titleBuf = new StringBuilder(256);
        EnumWindows((h, _) =>
        {
            if (main != IntPtr.Zero) return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            string? proc = null;
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
            if (proc is null || !processes.Contains(proc, StringComparer.OrdinalIgnoreCase)) return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            var clsName = cls.ToString();
            if (!classes.Contains(clsName)
                && !clsName.StartsWith("WeChatMainWnd", StringComparison.OrdinalIgnoreCase)
                && !clsName.StartsWith("Qt51514", StringComparison.OrdinalIgnoreCase)
                && !clsName.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)) return true;
            if (!IsWindowVisible(h)) return true;
            titleBuf.Clear();
            GetWindowText(h, titleBuf, 256);
            var title = titleBuf.ToString();
            if (title.Length == 0) return true;
            if (app != "qq" && !title.Contains("微信") && !title.Contains("WeChat")) return true;
            // 排除工具保存位窗口（Weixin 那个不可见辅助窗）
            if (clsName.Contains("ToolSaveBits", StringComparison.OrdinalIgnoreCase)) return true;
            main = h;
            return false;
        }, IntPtr.Zero);

        if (main == IntPtr.Zero)
        {
            Console.WriteLine("main window not found");
            return 2;
        }

        var win = AutomationElement.FromHandle(main);
        if (win is null)
        {
            Console.WriteLine("FromHandle null");
            return 3;
        }

        Console.WriteLine($"main hwnd=0x{main.ToInt64():X}");

        AutomationElement? best = null;
        var bestScore = int.MinValue;
        var lists = win.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
        Console.WriteLine($"List candidates: {lists.Count}");
        for (var i = 0; i < lists.Count; i++)
        {
            try
            {
                var c = lists[i];
                var name = c.Current.Name ?? "";
                var count = c.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;
                var score = count;
                if (name.Contains("会话") || name.Contains("聊天") || name.Contains("消息")) score += 10000;
                Console.WriteLine($"  [{i}] name=\"{name}\" items={count} class={c.Current.ClassName} score={score}");
                if (score > bestScore) { bestScore = score; best = c; }
            }
            catch { }
        }

        if (best is null)
        {
            Console.WriteLine("no List found");
            return 4;
        }

        Console.WriteLine($"best list: name=\"{best.Current.Name}\" class={best.Current.ClassName}");
        var items = best.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        Console.WriteLine($"items: {items.Count}");

        for (var i = 0; i < items.Count && i < maxItems; i++)
        {
            Console.WriteLine();
            Console.WriteLine($"===== ITEM {i} =====");
            var sb = new StringBuilder();
            Walk(items[i], 0, sb);
            Console.Write(sb.ToString());
        }

        return 0;
    }

    private static void Walk(AutomationElement e, int depth, StringBuilder sb)
    {
        string type, name, cls, aid;
        try
        {
            type = e.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "?";
            name = e.Current.Name ?? "";
            cls = e.Current.ClassName ?? "";
            aid = e.Current.AutomationId ?? "";
        }
        catch { return; }

        sb.Append(' ', depth * 2)
            .Append($"[{type}] \"{name}\"")
            .Append(string.IsNullOrEmpty(cls) ? "" : $" class={cls}")
            .Append(string.IsNullOrEmpty(aid) ? "" : $" id={aid}")
            .AppendLine();

        AutomationElement? child;
        try { child = TreeWalker.RawViewWalker.GetFirstChild(e); }
        catch { return; }
        while (child is not null)
        {
            Walk(child, depth + 1, sb);
            try { child = TreeWalker.RawViewWalker.GetNextSibling(child); }
            catch { child = null; }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder s, int max);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
}
