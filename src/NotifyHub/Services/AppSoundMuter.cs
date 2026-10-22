using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NotifyHub.Services;

/// <summary>
/// 监听应用提示音静音：把微信 / QQ 自己的通知提示音在系统音频会话层静音，
/// 只保留 NotifyHub 的提示音（用户需求：开启本应用消息提示音时，被监听应用的
/// 自带音效自动关闭；关闭时恢复）。
///
/// 实现：Windows Core Audio——枚举默认播放设备上的所有音频会话，
/// 找到属于 Weixin / WeChat / QQ 进程的会话，ISimpleAudioVolume.SetMute。
/// COM 调用全部在 MTA 后台线程执行，失败只记日志、不影响主流程。
/// </summary>
public sealed class AppSoundMuter : IDisposable
{
    /// <summary>目标进程名（不含 .exe，大小写不敏感）。微信 4.x 是 Weixin。</summary>
    private static readonly string[] TargetProcesses = { "Weixin", "WeChat", "QQ" };

    /// <summary>同样的静音状态重复申请时的最小间隔（微信/QQ 后启动的会话也要补静音）。</summary>
    private static readonly int RescanIntervalMs = 15_000;

    private readonly LogService _log;
    private readonly object _gate = new();
    private bool? _lastMute;
    private long _lastApplyMs;

    public AppSoundMuter(LogService log) => _log = log;

    /// <summary>
    /// 应用静音状态（任意线程可调；内部去重 + 节流）。
    /// mute=true：静音微信/QQ；false：恢复。
    /// </summary>
    public void Apply(bool mute)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;

            if (_lastMute == mute && now - _lastApplyMs < RescanIntervalMs)
            {
                return; // 状态没变且刚扫过
            }

            _lastMute = mute;
            _lastApplyMs = now;
        }

        var t = new Thread(() => ApplyWorker(mute))
        {
            IsBackground = true,
            Name = "AppSoundMuter",
        };
        t.Start();
    }

    private void ApplyWorker(bool mute)
    {
        try
        {
            var hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            var coInit = hr is 0 or S_FALSE; // S_FALSE = 已初始化，同样可用

            try
            {
                if (!coInit)
                {
                    _log.Warn("Muter", "CoInitialize 失败", $"0x{hr:X8}");
                    return;
                }

                var muted = SetTargetSessionsMute(mute);
                if (muted > 0)
                {
                    _log.Info("Muter", mute ? "已静音监听应用提示音" : "已恢复监听应用提示音", $"命中 {muted} 个会话");
                }
            }
            finally
            {
                if (hr == 0)
                {
                    CoUninitialize();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Muter", "音频会话静音失败", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <returns>本次实际改动的会话数。</returns>
    private int SetTargetSessionsMute(bool mute)
    {
        var changed = 0;

        var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
                             ?? throw new InvalidOperationException("MMDeviceEnumerator COM 类不可用");
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;

        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);

            var iidManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            device.Activate(ref iidManager2, CLSCTX.CLSCTX_ALL, IntPtr.Zero, out var obj);
            var manager = (IAudioSessionManager2)obj;

            manager.GetSessionEnumerator(out var sessionEnum);

            sessionEnum.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    sessionEnum.GetSession(i, out control);
                    if (control is null)
                    {
                        continue;
                    }

                    // 会话 → PID → 进程名
                    var ctrl2 = (IAudioSessionControl2)control;
                    ctrl2.GetProcessId(out var pid);
                    if (pid == 0 || !IsTargetProcess(pid))
                    {
                        continue;
                    }

                    // 同一个会话对象实现 ISimpleAudioVolume，QI 后静音
                    if (control is ISimpleAudioVolume volume)
                    {
                        volume.SetMute(mute, Guid.Empty);
                        changed++;
                    }

                    Marshal.ReleaseComObject(ctrl2);
                }
                catch
                {
                    // 单个会话失败不影响其余
                }
                finally
                {
                    if (control is not null)
                    {
                        Marshal.ReleaseComObject(control);
                    }
                }
            }

            Marshal.ReleaseComObject(sessionEnum);
            Marshal.ReleaseComObject(manager);
            Marshal.ReleaseComObject(device);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return changed;
    }

    private static bool IsTargetProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;

            foreach (var target in TargetProcesses)
            {
                if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 进程已退出
        }

        return false;
    }

    public void Dispose()
    {
        // 退出时恢复（尽力而为，不阻塞退出）
        try
        {
            Apply(false);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>退出前用：同步恢复（后台线程 + 最多等 2 秒），确保不带着静音退出。</summary>
    public void RestoreSync()
    {
        lock (_gate)
        {
            _lastMute = null;
            _lastApplyMs = 0;
        }

        var t = new Thread(() => ApplyWorker(false))
        {
            IsBackground = true,
            Name = "AppSoundMuterRestore",
        };
        t.Start();
        t.Join(2000);
    }

    // ---------------- Core Audio COM interop（只声明用到的一小部分） ----------------

    private const int COINIT_MULTITHREADED = 0x0;
    private const int S_FALSE = 1;

    private enum EDataFlow
    {
        eRender,
        eCapture,
        eAll,
    }

    private enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications,
    }

    [Flags]
    private enum CLSCTX
    {
        CLSCTX_ALL = 0x17,
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    // 注意：COM 接口的 vtable 槽位必须与真实定义逐一对齐（包括不用的方法），
    // 错位会跳到错误的函数指针上直接把进程干崩（0xC0000005）。

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // slot 1: EnumAudioEndpoints（不用）
        [PreserveSig]
        int EnumAudioEndpoints(IntPtr dataFlow, IntPtr stateMask, out IntPtr devices);

        // slot 2: GetDefaultAudioEndpoint
        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

        // slot 3-5: GetDevice / RegisterNotification / UnregisterNotification（不用）
        [PreserveSig]
        int GetDevice(IntPtr id, out IntPtr device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, CLSCTX clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager 基接口
        // slot 1: GetAudioSessionControl（不用）
        [PreserveSig]
        int GetAudioSessionControl(IntPtr guid, out IntPtr control);

        // slot 2: GetSimpleAudioVolume（不用）
        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr guid, out IntPtr volume);

        // IAudioSessionManager2
        // slot 3: GetSessionEnumerator
        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);

        // slot 4-7: Register/UnregisterSessionNotification、Register/UnregisterDuckNotification（不用）
        [PreserveSig]
        int RegisterSessionNotification(IntPtr notify);

        [PreserveSig]
        int UnregisterSessionNotification(IntPtr notify);

        [PreserveSig]
        int RegisterDuckNotification(IntPtr sessionID, IntPtr duckNotify);

        [PreserveSig]
        int UnregisterDuckNotification(IntPtr sessionID);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetSession(int index, out IAudioSessionControl session);
    }

    /// <summary>IAudioSessionControl（真实 vtable 恰好 9 个方法，只用于 QI 跳板）。</summary>
    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
    }

    /// <summary>IAudioSessionControl2 查询用（GetProcessId 在 vtable 第 11 位）。</summary>
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, Guid ctx);
        [PreserveSig] int GetMute(out bool mute);
    }
}
