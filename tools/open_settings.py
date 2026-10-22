"""双击 NotifyHub 托盘图标打开设置窗，找到窗口后整窗截屏。"""
import ctypes
import subprocess
import time
from ctypes import wintypes

user32 = ctypes.windll.user32


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def rect_of(hwnd):
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def find_settings():
    found = []

    def enum_cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == "NotifyHub 设置":
                found.append((hwnd, rect_of(hwnd)))
        return True

    EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND,
                                         wintypes.LPARAM)
    user32.EnumWindows(EnumWindowsProc(enum_cb), 0)
    return found


def click(x, y, times=2):
    for _ in range(times):
        user32.SetCursorPos(x, y)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(0.1)


# 1. 展开溢出面板
click(1736, 1060, 1)
time.sleep(1.0)
# 2. 双击 NotifyHub 托盘图标
click(1776, 1020, 2)
time.sleep(2.5)

wins = find_settings()
print("settings windows:", wins)
if wins:
    hwnd, r = wins[0]
    w = r[2] - r[0]
    h = r[3] - r[1]
    print("rect=%s size=%dx%d" % (r, w, h))
    out = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\reg-settings-open.png"
    subprocess.run([
        "powershell", "-ExecutionPolicy", "Bypass", "-File",
        r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\screenshot-region.ps1",
        "-X", str(r[0]), "-Y", str(r[1]), "-W", str(w), "-H", str(h),
        "-OutFile", out,
    ], check=True)
    print("saved", out)
else:
    print("SETTINGS WINDOW NOT FOUND")
