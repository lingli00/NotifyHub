"""NotifyHub 灵动岛真机探测 / 拖拽工具。

用法：
  python tools/island_probe.py info            # 打印灵动岛窗口位置与样式
  python tools/island_probe.py drag dx dy      # 从条体中心真实拖拽（dx/dy 像素位移）
  python tools/island_probe.py rdrag dx dy     # 右键拖拽

只做真实鼠标事件（SetCursorPos + mouse_event），不走配置文件写坐标——
上一轮"改配置就算验证"是错的做法，必须让系统真的把鼠标消息打进窗口。
"""

import ctypes
import ctypes.wintypes as w
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)

TITLE = "NotifyHub 灵动岛"

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010

GWL_EXSTYLE = -20
WS_EX_NOACTIVATE = 0x08000000
WS_EX_TRANSPARENT = 0x00000020
WS_EX_TOOLWINDOW = 0x00000080


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def enum_windows():
    result = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, w.HWND, w.LPARAM)
    def cb(hwnd, _lp):
        length = user32.GetWindowTextLengthW(hwnd)
        if length > 0:
            buf = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buf, length + 1)
            result.append((hwnd, buf.value))
        return True

    user32.EnumWindows(cb, 0)
    return result


def find_island():
    for hwnd, title in enum_windows():
        if title == TITLE:
            return hwnd
    return None


def rect_of(hwnd):
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r


def info():
    hwnd = find_island()
    if hwnd is None:
        print("ISLAND NOT FOUND")
        return 1

    r = rect_of(hwnd)
    style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE) & 0xFFFFFFFF
    visible = bool(user32.IsWindowVisible(hwnd))

    print(f"hwnd=0x{hwnd:X}")
    print(f"rect=({r.left},{r.top})-({r.right},{r.bottom}) size={r.right - r.left}x{r.bottom - r.top}")
    print(f"visible={visible} noactivate={bool(style & WS_EX_NOACTIVATE)} "
          f"toolwindow={bool(style & WS_EX_TOOLWINDOW)}")
    return 0


def drag(dx, dy, right=False, hold_ms=90):
    hwnd = find_island()
    if hwnd is None:
        print("ISLAND NOT FOUND")
        return 1

    r = rect_of(hwnd)
    # 从右侧状态文字区域起手：中间正好压在播放按钮上，那一片本来就该让给按钮
    cx = r.right - 70
    cy = (r.top + r.bottom) // 2
    if cx <= r.left + 20:
        cx = (r.left + r.right) // 2

    before = (r.left, r.top)

    down = MOUSEEVENTF_RIGHTDOWN if right else MOUSEEVENTF_LEFTDOWN
    up = MOUSEEVENTF_RIGHTUP if right else MOUSEEVENTF_LEFTUP

    user32.SetCursorPos(cx, cy)
    time.sleep(0.12)
    user32.mouse_event(down, 0, 0, 0, 0)
    time.sleep(hold_ms / 1000.0)

    steps = 24
    for i in range(1, steps + 1):
        user32.SetCursorPos(cx + dx * i // steps, cy + dy * i // steps)
        time.sleep(0.013)

    time.sleep(0.08)
    user32.mouse_event(up, 0, 0, 0, 0)
    time.sleep(0.35)

    after = rect_of(find_island() or hwnd)
    moved = (after.left - before[0], after.top - before[1])

    print(f"before=({before[0]},{before[1]}) after=({after.left},{after.top}) moved={moved} "
          f"requested=({dx},{dy})")
    ok = abs(moved[0] - dx) <= 8 and abs(moved[1] - dy) <= 8

    # 拖动结束会立刻"回吸"到最近边，所以按位移判断是否真的被拖动了
    print("DRAG " + ("OK" if (abs(moved[0]) > 4 or abs(moved[1]) > 4) else "FAIL"))
    return 0 if (abs(moved[0]) > 4 or abs(moved[1]) > 4) else 2


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    cmd = sys.argv[1].lower()

    if cmd == "info":
        return info()

    if cmd in ("drag", "rdrag"):
        dx = int(sys.argv[2]) if len(sys.argv) > 2 else 0
        dy = int(sys.argv[3]) if len(sys.argv) > 3 else 0
        return drag(dx, dy, right=(cmd == "rdrag"))

    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main())
