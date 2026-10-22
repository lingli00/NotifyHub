using System.Reflection;

var temp = Path.Combine(Path.GetTempPath(), "notifyhub-selftest-link.lnk");
var exe = Environment.ProcessPath ?? "";
Console.WriteLine($"exe={exe}");
try
{
    var shellType = Type.GetTypeFromProgID("WScript.Shell");
    Console.WriteLine($"shellType={shellType?.FullName}");
    var shell = Activator.CreateInstance(shellType!);
    var shortcut = shellType!.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell!, new object[] { temp });
    Console.WriteLine("created shortcut");
    shortcut!.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
    // 关键：必须 Save()，否则 lnk 文件不落盘
    shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    Console.WriteLine($"saved={File.Exists(temp)}");

    // 读回验证（模拟 BrowserLauncher.ResolveShortcut 的路径）
    var sc2 = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell!, new object[] { temp });
    var read = sc2!.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc2, null) as string;
    Console.WriteLine($"readback={read}");
    Console.WriteLine($"equal={string.Equals(read, exe, StringComparison.OrdinalIgnoreCase)}");
}
catch (Exception ex)
{
    Console.WriteLine($"ERR {ex.GetType().Name}: {ex.Message}");
}
