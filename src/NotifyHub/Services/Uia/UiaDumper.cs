using System.IO;
using System.Text;
using System.Windows.Automation;
using NotifyHub.Services.Parsing;

namespace NotifyHub.Services.Uia;

/// <summary>
/// UIA 探测模式：把微信 / QQ 的窗口树完整写到 dump 目录，
/// 用于首次接入时确认控件结构（类名、ControlType、Name 的排列顺序）。
/// </summary>
public static class UiaDumper
{
    private const int MaxDepth = 10;
    private const int MaxNodes = 2500;

    public sealed record DumpResult(string FilePath, int NodeCount, string Summary);

    public static DumpResult DumpAll(LogService log)
    {
        AppPaths.EnsureCreated();

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(AppPaths.DumpDir, $"uia-dump-{stamp}.txt");
        var builder = new StringBuilder();
        var nodes = 0;

        builder.AppendLine("NotifyHub UIA 探测报告");
        builder.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"系统: {Environment.OSVersion.VersionString} / 64bit={Environment.Is64BitOperatingSystem}");
        builder.AppendLine($"进程: pid={Environment.ProcessId}");
        builder.AppendLine();

        AppendTarget(builder, "微信", new[] { "WeChat", "Weixin" },
            new[] { "WeChatMainWndForPC", "WeChatMainWndForPC1", "mmui::MainWindow" }, log, ref nodes);

        builder.AppendLine(new string('-', 100));
        builder.AppendLine();

        AppendTarget(builder, "QQ", new[] { "QQ" },
            new[] { "Chrome_WidgetWin_1" }, log, ref nodes);

        builder.AppendLine(new string('-', 100));
        builder.AppendLine();
        builder.AppendLine("提示：若上面提示“未找到进程”，说明目标程序没运行，或者进程名与预期不同；");
        builder.AppendLine("     若找到了窗口但会话列表解析成功=0，请把本文件里的 List/ListItem 片段作为适配依据。");

        try
        {
            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            log.Info("UiaDump", "UIA 树已导出", path);
        }
        catch (Exception ex)
        {
            log.Error("UiaDump", "写 dump 文件失败", ex.Message);
        }

        var summary = $"节点 {nodes} 个 → {path}";
        return new DumpResult(path, nodes, summary);
    }

    private static void AppendTarget(StringBuilder builder, string label, string[] processNames, string[] preferredClasses, LogService log, ref int nodes)
    {
        builder.AppendLine($"==== {label} ====");

        var processIds = WindowFinder.GetProcessIds(processNames);
        builder.AppendLine($"进程候选: {string.Join(", ", processNames)} → 命中 {processIds.Count} 个");
        if (processIds.Count > 0)
        {
            builder.AppendLine($"PID: {string.Join(", ", processIds)}");
        }

        var windows = WindowFinder.EnumerateWindows(processNames);
        builder.AppendLine($"顶层窗口: {windows.Count} 个");
        foreach (var window in windows)
        {
            builder.AppendLine("  " + window.Describe());
        }

        var main = WindowFinder.FindMainWindow(processNames, preferredClasses);
        builder.AppendLine($"选定主窗口: {(main == IntPtr.Zero ? "(无)" : $"0x{main.ToInt64():X} class={WindowFinder.GetWindowClass(main)} title=\"{WindowFinder.GetWindowTitle(main)}\"")}");
        builder.AppendLine();

        if (main == IntPtr.Zero)
        {
            builder.AppendLine("→ 跳过树 dump。");
            builder.AppendLine();
            return;
        }

        try
        {
            var element = AutomationElement.FromHandle(main);
            if (element is null)
            {
                builder.AppendLine("→ AutomationElement.FromHandle 返回 null。");
                builder.AppendLine();
                return;
            }

            builder.AppendLine("会话列表解析结果:");
            try
            {
                var read = SessionListReader.Read(element);
                builder.AppendLine($"  诊断: {read.Diagnostic}");
                for (var i = 0; i < read.Rows.Count; i++)
                {
                    builder.AppendLine($"  [{i}] 联系人=\"{read.Rows[i].Contact}\" 摘要=\"{LogService.Truncate(read.Rows[i].Summary, 80)}\"");
                }

                if (read.Rows.Count == 0)
                {
                    builder.AppendLine("  （空：请在下方树里找 ControlType=List 的节点，确认 ListItem 的 Name 结构）");
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine($"  解析异常: {ex.GetType().Name} {ex.Message}");
            }

            builder.AppendLine();
            builder.AppendLine("UIA 树:");

            var local = 0;
            Walk(element, 0, builder, ref local);
            nodes += local;
            builder.AppendLine($"（本窗口节点数: {local}）");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"→ dump 失败: {ex.GetType().Name} {ex.Message}");
            log.Warn("UiaDump", $"{label} dump 失败", ex.Message);
        }

        builder.AppendLine();
    }

    private static void Walk(AutomationElement element, int depth, StringBuilder builder, ref int count)
    {
        if (count >= MaxNodes || depth > MaxDepth)
        {
            return;
        }

        count++;

        string type;
        string name;
        string className;
        string automationId;
        string rectText;
        bool offscreen;

        try
        {
            var current = element.Current;
            type = current.ControlType?.ProgrammaticName?.Replace("ControlType.", string.Empty) ?? "?";
            name = current.Name ?? string.Empty;
            className = current.ClassName ?? string.Empty;
            automationId = current.AutomationId ?? string.Empty;
            var rect = current.BoundingRectangle;
            rectText = double.IsNaN(rect.Width) ? "-" : $"{(int)rect.X},{(int)rect.Y},{(int)rect.Width}x{(int)rect.Height}";
            offscreen = current.IsOffscreen;
        }
        catch
        {
            return;
        }

        builder.Append(' ', depth * 2)
            .Append($"[{count}] {type} \"{LogService.Truncate(name, 60)}\"")
            .Append(string.IsNullOrEmpty(className) ? string.Empty : $" class={className}")
            .Append(string.IsNullOrEmpty(automationId) ? string.Empty : $" id={automationId}")
            .Append($" rect={rectText}")
            .AppendLine(offscreen ? " offscreen" : string.Empty);

        AutomationElement? child;
        try
        {
            child = TreeWalker.ControlViewWalker.GetFirstChild(element);
        }
        catch
        {
            return;
        }

        while (child is not null && count < MaxNodes)
        {
            Walk(child, depth + 1, builder, ref count);

            try
            {
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            catch
            {
                child = null;
            }
        }
    }
}
