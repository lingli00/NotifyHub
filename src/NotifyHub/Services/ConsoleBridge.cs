using System.Runtime.InteropServices;

namespace NotifyHub.Services;

/// <summary>
/// 自包含 WinExe 没有自己的控制台，从命令行启动时尝试挂到父进程控制台，
/// 便于 --selftest / --dump 输出结果；挂不上就依赖日志文件。
/// </summary>
internal static class ConsoleBridge
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    public static void TryAttach()
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // 忽略：没有控制台不影响功能
        }
    }

    public static void WriteLine(string text)
    {
        try
        {
            Console.WriteLine(text);
        }
        catch
        {
            // 忽略
        }
    }
}
