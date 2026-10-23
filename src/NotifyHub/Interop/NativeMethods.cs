using System.Runtime.InteropServices;
using System.Text;

namespace NotifyHub.Interop;

/// <summary>
/// Win32 互操作。仅保留本项目用到的部分：
/// 窗口激活/置前、扩展样式（让通知窗口不抢焦点）、SendInput 模拟按键。
/// </summary>
internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;

    public const int WS_CAPTION = 0x00C0_0000;

    public const int WS_EX_TOOLWINDOW = 0x0000_0080;
    public const int WS_EX_NOACTIVATE = 0x0800_0000;

    /// <summary>MonitorFromWindow：找不到显示器时返回最近的显示器。</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>MonitorFromPoint：按坐标取显示器（0,0 + DEFAULTTOPRIMARY = 主屏）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>主屏。</summary>
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    /// <summary>系统 DPI（v0.2.8）：96 = 100%。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    /// <summary>
    /// 挪动鼠标指针（离屏截图前把指针挪到角落，避免鼠标恰好压住某个播放键，
    /// 把 hover 底色一起拍进 PNG —— v0.2.8 实拍就踩到过这个假 bug）。
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    // ---- 悬浮窗口拖拽 / 吸附所需（分层窗口不能用 WPF 的 DragMove） ----

    public const int WM_NCHITTEST = 0x0084;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;

    public const int HTTRANSPARENT = -1;

    /// <summary>WM_MOUSEACTIVATE 返回值：不激活窗口，且把这条点击继续投递给窗口。</summary>
    public const int MA_NOACTIVATE = 3;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    /// <summary>把窗口移到屏幕坐标（像素），尺寸/层级不变且不激活。</summary>
    public static void MoveWindowTo(IntPtr hWnd, int x, int y)
        => SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    public const int SW_RESTORE = 9;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint INPUT_KEYBOARD = 1;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_F = 0x46;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static long GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    public static void SetWindowLongPtr(IntPtr hWnd, int nIndex, long value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
        }
        else
        {
            SetWindowLong32(hWnd, nIndex, unchecked((int)value));
        }
    }

    public static string GetClassName(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(hWnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    public static string GetWindowText(IntPtr hWnd)
    {
        var buffer = new StringBuilder(512);
        var length = GetWindowText(hWnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    /// <summary>把窗口拉到前台（含最小化还原）。返回是否成功置前。</summary>
    public static bool ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            return false;
        }

        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(hWnd, 5); // SW_SHOW
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;

        // Windows 限制后台进程抢前台，临时挂到当前前台线程可绕过
        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, foregroundThread, true);
        }

        try
        {
            BringWindowToTop(hWnd);
            var ok = SetForegroundWindow(hWnd);
            return ok || GetForegroundWindow() == hWnd;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    /// <summary>发送单个虚拟键（按下+抬起）。</summary>
    public static void SendKey(ushort virtualKey)
    {
        var inputs = new[]
        {
            MakeKeyDown(virtualKey, 0, 0),
            MakeKeyUp(virtualKey, 0, 0),
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>发送组合键（如 Ctrl+F）。</summary>
    public static void SendCombo(ushort modifier, ushort virtualKey)
    {
        var inputs = new[]
        {
            MakeKeyDown(modifier, 0, 0),
            MakeKeyDown(virtualKey, 0, 0),
            MakeKeyUp(virtualKey, 0, 0),
            MakeKeyUp(modifier, 0, 0),
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>用 KEYEVENTF_UNICODE 输入文本，不受输入法与键盘布局影响。</summary>
    public static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        const int chunkSize = 64;
        for (var offset = 0; offset < text.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, text.Length - offset);
            var inputs = new INPUT[length * 2];

            for (var i = 0; i < length; i++)
            {
                var ch = text[offset + i];
                inputs[i * 2] = MakeKeyDown(0, ch, KEYEVENTF_UNICODE);
                inputs[(i * 2) + 1] = MakeKeyUp(0, ch, KEYEVENTF_UNICODE);
            }

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            Thread.Sleep(10);
        }
    }

    private static INPUT MakeKeyDown(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = scan,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static INPUT MakeKeyUp(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = scan,
                dwFlags = flags | KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
