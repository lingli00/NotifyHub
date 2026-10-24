"""Restore and foreground a window by title, then keep it there."""
import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32

SW_RESTORE = 9
SW_SHOW = 5
HWND_TOPMOST = -1
HWND_NOTOPMOST = -2
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_SHOWWINDOW = 0x0040

title_part = sys.argv[1]
mode = sys.argv[2] if len(sys.argv) > 2 else "restore"

found = []


@ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
def cb(hwnd, lparam):
    txt = ctypes.create_unicode_buffer(256)
    user32.GetWindowTextW(hwnd, txt, 256)
    if title_part in txt.value and user32.IsWindowVisible(hwnd):
        found.append(hwnd)
    return True


user32.EnumWindows(cb, 0)
print("found", [hex(h) for h in found])
for hwnd in found:
    if mode == "restore":
        user32.ShowWindow(hwnd, SW_RESTORE)
    else:
        user32.ShowWindow(hwnd, SW_SHOW)
    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW)
    time.sleep(0.2)
    user32.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW)
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    print("hwnd=0x%X rect=%d,%d %dx%d" % (hwnd, r.left, r.top, r.right - r.left, r.bottom - r.top))
