// 用 UIA 操作指定窗口里的控件（比坐标点击稳）。
// 用法: UiaToggle <hwnd> <元素名> [toggle|invoke|query]
//   hwnd   —— 目标窗口句柄（十进制）
//   元素名 —— UIA Name，如「音乐控制模块」「保存并应用」
//   toggle —— 切换复选框（TogglePattern）
//   invoke —— 点击按钮（InvokePattern）
//   省略   —— 只查询类型/矩形/状态
using System.Windows.Automation;

if (args.Length < 2)
{
    Console.WriteLine("usage: UiaToggle <hwnd> <name> [toggle|invoke|query]");
    return 1;
}

var hwnd = new IntPtr(long.Parse(args[0]));
var name = args[1];
var win = AutomationElement.FromHandle(hwnd);
if (win is null)
{
    Console.WriteLine("ERROR: no element for handle " + args[0]);
    return 2;
}

var cond = new PropertyCondition(AutomationElement.NameProperty, name);
var el = win.FindFirst(TreeScope.Descendants, cond);
if (el is null)
{
    Console.WriteLine("NOT FOUND: " + name);
    return 3;
}

var r = el.Current.BoundingRectangle;
Console.WriteLine($"found name={el.Current.Name} type={el.Current.ControlType.ProgrammaticName} " +
                  $"rect=({r.X:F0},{r.Y:F0})-({r.Right:F0},{r.Bottom:F0})");

var action = args.Length > 2 ? args[2] : "query";

if (action == "toggle")
{
    if (el.GetCurrentPattern(TogglePattern.Pattern) is not TogglePattern toggle)
    {
        Console.WriteLine("ERROR: no TogglePattern");
        return 4;
    }
    Console.WriteLine("state before: " + toggle.Current.ToggleState);
    toggle.Toggle();
    System.Threading.Thread.Sleep(400);
    toggle = (TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern);
    Console.WriteLine("state after: " + toggle.Current.ToggleState);
}
else if (action == "invoke")
{
    if (el.GetCurrentPattern(InvokePattern.Pattern) is not InvokePattern inv)
    {
        Console.WriteLine("ERROR: no InvokePattern");
        return 5;
    }
    inv.Invoke();
    Console.WriteLine("invoked");
}
else
{
    if (el.GetCurrentPattern(TogglePattern.Pattern) is TogglePattern t)
    {
        Console.WriteLine("toggle state: " + t.Current.ToggleState);
    }
}
return 0;
