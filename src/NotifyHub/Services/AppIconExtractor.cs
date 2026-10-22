using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotifyHub.Services;

/// <summary>
/// 播放器进程图标提取（需求 5）：按进程名找到正在运行的音乐 App，
/// 从它的 exe 里提取透明图标（.ico 自带 alpha），供灵动岛最左侧贴一个
/// 圆角小图标。结果按进程名缓存，同一播放器只提一次。
/// </summary>
public static class AppIconExtractor
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    /// <summary>
    /// 取进程名对应的播放器图标（进程名如 "folia" / "qqmusic"，不含 .exe）。
    /// 找不到进程 / 提不到图标时返回 null（调用方把图标位收起即可）。
    /// </summary>
    public static ImageSource? GetIcon(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var key = processName.Trim();

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        ImageSource? icon = null;

        try
        {
            var path = FindExecutablePath(key);
            if (path is not null)
            {
                icon = ExtractFromFile(path);
            }
        }
        catch
        {
            // 提图标失败按“无图标”处理，绝不影响音乐行显示
            icon = null;
        }

        lock (Gate)
        {
            Cache[key] = icon;
        }

        return icon;
    }

    /// <summary>在运行中的进程里找带可执行路径的那个（FOLIA 等多进程App取第一个有路径的）。</summary>
    private static string? FindExecutablePath(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return null;
        }

        foreach (var process in processes)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            catch
            {
                // 32/64 位或权限受限的进程拿不到 MainModule，试下一个
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    /// <summary>从 exe 提取小图标（16px 系统小图标，保留 alpha 通道）。</summary>
    private static ImageSource? ExtractFromFile(string path)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(
            path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // 跨线程/动画安全
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = DestroyIcon(info.hIcon);
        }
    }
}
