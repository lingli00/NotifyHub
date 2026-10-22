"""枚举 NotifyHub 相关窗口当前位置（灵动岛/吸附条）。"""
import ctypes
from ctypes import wintypes

user32 = ctypes.windll.user32


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def main():
    out = []

    def enum_cb(hwnd, _):
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        if "NotifyHub" not in cls.value:
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        t = ""
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            t = buf.value
        r = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        out.append((hwnd, t, (r.left, r.top, r.right, r.bottom),
                    bool(user32.IsWindowVisible(hwnd))))
        return True

    EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND,
                                         wintypes.LPARAM)
    user32.EnumWindows(EnumWindowsProc(enum_cb), 0)
    for hwnd, t, r, vis in out:
        print("%d  %-14r  rect=%s  visible=%s" % (hwnd, t, r, vis))


if __name__ == "__main__":
    main()
