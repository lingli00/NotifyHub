"""诊断：悬停溢出面板 (1776,1020) 2.5s，枚举所有顶层窗口看 tooltip 是否出现、类名是什么。"""
import ctypes
import time
from ctypes import wintypes

user32 = ctypes.windll.user32


def main():
    x, y = 1776, 1020
    user32.SetCursorPos(x, y)
    time.sleep(2.5)
    print("cursor at", x, y)

    results = []

    def enum_cb(hwnd, _):
        cls_buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls_buf, 256)
        n = user32.GetWindowTextLengthW(hwnd)
        title = ""
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            title = buf.value
        visible = user32.IsWindowVisible(hwnd)
        if visible and (title or "tooltip" in cls_buf.value.lower()):
            results.append((hwnd, cls_buf.value, title))
        return True

    EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND,
                                         wintypes.LPARAM)
    user32.EnumWindows(EnumWindowsProc(enum_cb), 0)
    for hwnd, cls, title in results:
        print("%d  cls=%r  title=%r" % (hwnd, cls, title))
    print("total visible titled:", len(results))


if __name__ == "__main__":
    main()
