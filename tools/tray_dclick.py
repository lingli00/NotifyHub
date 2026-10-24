"""Double-click NotifyHub tray icon (overflow panel) and poll for the settings window."""
import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32

SW_SHOWMINIMIZED = 2


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find(cls, win=None):
    return user32.FindWindowW(cls, win)


def child(hwnd, cls):
    return user32.FindWindowExW(hwnd, None, cls, None)


def rect_of(hwnd):
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r


def enum_windows():
    out = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, lparam):
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == TARGET_PID:
            txt = ctypes.create_unicode_buffer(256)
            user32.GetWindowTextW(hwnd, txt, 256)
            vis = user32.IsWindowVisible(hwnd)
            r = rect_of(hwnd)
            out.append((hwnd, vis, txt.value, r.left, r.top, r.right - r.left, r.bottom - r.top))
        return True

    user32.EnumWindows(cb, 0)
    return out


TARGET_PID = int(sys.argv[1])

# locate tray icon: overflow panel ToolbarWindow32
panel = find("NotifyIconOverflowWindow")
tb = child(panel, "ToolbarWindow32") if panel else 0
if not tb:
    tb = find("ToolbarWindow32")
print("panel=0x%X toolbar=0x%X" % (panel, tb))
r = rect_of(tb)
print("toolbar rect", r.left, r.top, r.right, r.bottom)
# 3x3 grid, NotifyHub at r2c2 per prior probing
w = (r.right - r.left) / 3.0
h = (r.bottom - r.top) / 3.0
x = int(r.left + w * 1.5)
y = int(r.top + h * 1.5)
print("click at", x, y)

user32.SetCursorPos(x, y)
time.sleep(0.4)
for _ in range(2):
    user32.mouse_event(0x0002, 0, 0, 0, 0)  # down
    time.sleep(0.03)
    user32.mouse_event(0x0004, 0, 0, 0, 0)  # up
    time.sleep(0.06)

deadline = time.time() + 6
seen = False
while time.time() < deadline:
    for (hwnd, vis, txt, l, t, ww, hh) in enum_windows():
        if "NotifyHub" in txt and ("设置" in txt or "灵动岛" in txt or "吸附条" in txt):
            print("WIN hwnd=0x%X vis=%s text=%s rect=%d,%d %dx%d" % (hwnd, vis, txt, l, t, ww, hh))
            if "设置" in txt:
                seen = True
    if seen:
        break
    time.sleep(0.3)
print("SETTINGS OPENED" if seen else "SETTINGS NOT OPENED")
