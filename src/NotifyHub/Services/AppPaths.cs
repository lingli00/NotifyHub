using System.IO;

namespace NotifyHub.Services;

/// <summary>
/// 统一管理落盘路径。
/// 便携模式：exe 同目录存在 config.json（绿色包自带）→ 所有数据都放在程序目录，
/// 换目录/换机器整包拷走配置不丢；否则回落到 %AppData%\NotifyHub。
/// </summary>
public static class AppPaths
{
    private static readonly string? PortableRoot = DetectPortableRoot();

    public static string Root => PortableRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NotifyHub");

    public static bool IsPortable => PortableRoot is not null;

    public static string ConfigFile => Path.Combine(Root, "config.json");

    public static string EventFile => Path.Combine(Root, "events.json");

    public static string DedupFile => Path.Combine(Root, "dedup.json");

    public static string LogDir => Path.Combine(Root, "logs");

    public static string DumpDir => Path.Combine(Root, "dump");

    public static string SoundDir => Path.Combine(Root, "sounds");

    public static void EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(DumpDir);
            Directory.CreateDirectory(SoundDir);
        }
        catch
        {
            // 目录不可写时降级，不阻塞启动
        }
    }

    private static string? DetectPortableRoot()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;

            if (string.IsNullOrEmpty(exeDir))
            {
                return null;
            }

            // 绿色包自带 config.json（或显式放 portable.txt）即视为便携模式
            if (File.Exists(Path.Combine(exeDir, "config.json"))
                || File.Exists(Path.Combine(exeDir, "portable.txt")))
            {
                return exeDir;
            }
        }
        catch
        {
            // 探测失败按常规模式
        }

        return null;
    }
}
