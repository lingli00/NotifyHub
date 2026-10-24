"""列出与给定屏幕区域相交的所有可见顶层窗口（判断某块色斑属于谁）。

用法: python tools/probe_region.py <x> <y> <w> <h>
"""
import ctypes
import sys

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()


class RECT(ctypes.Structure):
    _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long),
                ("r", ctypes.c_long), ("b", ctypes.c_long)]


def main():
    x, y, w, h = (int(v) for v in sys.argv[1:5])
    tx, ty, tr, tb = x, y, x + w, y + h
    rows = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        r = RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(r)):
            return True
        if r.r <= tx or r.l >= tr or r.b <= ty or r.t >= tb:
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 1) if n else ctypes.create_unicode_buffer(1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        rows.append((hwnd, pid.value, cls.value, buf.value,
                     r.l, r.t, r.r - r.l, r.b - r.t))
        return True

    user32.EnumWindows(cb, 0)
    for hwnd, pid, cls, title, l, t, ww, hh in rows:
        print(f"hwnd=0x{hwnd:X} pid={pid} cls={cls} rect={l},{t} {ww}x{hh} title={title}")


if __name__ == "__main__":
    main()
