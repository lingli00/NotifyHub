using System.Diagnostics;
using System.Windows.Automation;

// UIA 可用性最小探针：分步定位“哪一步挂住/失败”
var sw = Stopwatch.StartNew();

void Step(string name, Action action)
{
    var start = sw.ElapsedMilliseconds;
    try
    {
        action();
        Console.WriteLine($"[OK {sw.ElapsedMilliseconds - start}ms] {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[EX {sw.ElapsedMilliseconds - start}ms] {name} → {ex.GetType().Name}: {ex.Message}");
    }

    Console.Out.Flush();
}

Step("RootElement", () =>
{
    var root = AutomationElement.RootElement;
    Console.WriteLine($"  root={(root is null ? "NULL" : "OK")}");
});

AutomationElement? root2 = null;
Step("root.FindAll 顶层窗口（无缓存）", () =>
{
    root2 = AutomationElement.RootElement;
    var windows = root2!.FindAll(TreeScope.Children, Condition.TrueCondition);
    Console.WriteLine($"  topWindows={windows.Count}");
});

Step("CacheRequest(Element) + FindAll 顶层窗口", () =>
{
    var request = new CacheRequest
    {
        TreeFilter = Automation.ControlViewCondition,
        TreeScope = TreeScope.Element,
        AutomationElementMode = AutomationElementMode.Full,
    };
    request.Add(AutomationElement.ProcessIdProperty);
    request.Add(AutomationElement.ClassNameProperty);
    request.Add(AutomationElement.NameProperty);
    request.Add(AutomationElement.NativeWindowHandleProperty);
    request.Add(AutomationElement.BoundingRectangleProperty);

    using (request.Activate())
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        Console.WriteLine($"  topWindows(cached)={windows.Count}");
    }
});

Step("Process.GetProcessesByName", () =>
{
    foreach (var name in new[] { "explorer" })
    {
        var list = Process.GetProcessesByName(name);
        Console.WriteLine($"  {name} count={list.Length}");
        foreach (var p in list) p.Dispose();
    }
});

Step("对第一个顶层窗口跑 SessionListReader", () =>
{
    if (root2 is null)
    {
        Console.WriteLine("  跳过");
        return;
    }

    var windows = root2.FindAll(TreeScope.Children, Condition.TrueCondition);
    if (windows.Count == 0)
    {
        Console.WriteLine("  无顶层窗口");
        return;
    }

    var first = windows[0];
    var read = NotifyHub.Services.Uia.SessionListReader.Read(first);
    Console.WriteLine($"  诊断: {read.Diagnostic}");
});

Step("UiaDumper.DumpAll（复刻自检第 12 项）", () =>
{
    var log = NotifyHub.Services.LogService.Shared;
    log.Initialize();
    var result = NotifyHub.Services.Uia.UiaDumper.DumpAll(log);
    Console.WriteLine($"  {result.Summary}");
});

Console.WriteLine($"总耗时 {sw.ElapsedMilliseconds}ms");
return 0;
