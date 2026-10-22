using System.Runtime.InteropServices;

namespace NotifyHub.Interop;

/// <summary>
/// 系统文件夹选择对话框（IFileDialog + FOS_PICKFOLDERS）——需求 8：
/// 用户点“编辑”按钮时弹出资源管理器风格的目录选择器，任选日志保存目录。
/// 比 FolderBrowserDialog 现代化，且不需要引用 WinForms。
/// </summary>
internal static class FolderPicker
{
    /// <summary>
    /// 弹出目录选择框。返回用户选中的目录；取消/失败返回 null。
    /// </summary>
    public static string? Pick(string? initialPath, string title = "选择日志保存目录")
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);

            if (!string.IsNullOrWhiteSpace(initialPath) && System.IO.Directory.Exists(initialPath))
            {
                var item = ShellItemForPath(initialPath);
                if (item is not null)
                {
                    dialog.SetFolder(item);
                }
            }

            dialog.SetTitle(title);

            var hr = dialog.Show(IntPtr.Zero);
            if (hr != 0) // S_OK
            {
                return null;
            }

            dialog.GetResult(out var result);
            if (result is null)
            {
                return null;
            }

            result.GetDisplayName(SIGDN_FILESYSPATH, out var pathPtr);
            if (pathPtr == IntPtr.Zero)
            {
                return null;
            }

            var path = Marshal.PtrToStringUni(pathPtr);
            Marshal.FreeCoTaskMem(pathPtr);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static IShellItem? ShellItemForPath(string path)
    {
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
        return hr == 0 ? (IShellItem?)item : null;
    }

    private const int FOS_PICKFOLDERS = 0x0000_0020;
    private const int FOS_FORCEFILESYSTEM = 0x0000_0040;
    private const int FOS_PATHMUSTEXIST = 0x0000_0800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20A5EF")]
    private class FileOpenDialog
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135625")]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);

        // IFileDialog
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] int GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}
