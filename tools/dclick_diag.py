"""诊断：重复一遍双击流程，每步后枚举 NotifyHub 相关窗口 + 任务栏状态，最后全屏截屏。"""
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


def enum_notifyhub():
    out = []

    def enum_cb(hwnd, _):
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        n = user32.GetWindowTextLengthW(hwnd)
        t = ""
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            t = buf.value
        if "NotifyHub" in cls.value or "NotifyHub" in t:
            out.append((hwnd, cls.value, t, rect_of(hwnd),
                        bool(user32.IsWindowVisible(hwnd))))
        return True

    EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND,
                                         wintypes.LPARAM)
    user32.EnumWindows(EnumWindowsProc(enum_cb), 0)
    return out


def overflow_state():
    ow = user32.FindWindowW("NotifyIconOverflowWindow", None)
    if not ow:
        return "closed"
    return "open rect=%s" % (rect_of(ow),)


def click(x, y, times=1):
    for _ in range(times):
        user32.SetCursorPos(x, y)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(0.1)


print("== before ==")
for e in enum_notifyhub():
    print("  ", e)
print("  overflow:", overflow_state())

print("== open panel ==")
click(1736, 1060)
time.sleep(1.2)
print("  overflow:", overflow_state())

print("== double-click bell ==")
click(1776, 1020, 2)
time.sleep(3.0)
for e in enum_notifyhub():
    print("  ", e)
print("  overflow:", overflow_state())

out = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\reg-dclick-diag.png"
subprocess.run([
    "powershell", "-ExecutionPolicy", "Bypass", "-File",
    r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\screenshot-region.ps1",
    "-X", "0", "-Y", "0", "-W", "1920", "-H", "1080",
    "-OutFile", out,
], check=True)
print("saved", out)
