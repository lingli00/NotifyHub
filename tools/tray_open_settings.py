"""定位 NotifyHub 托盘图标并双击打开设置窗。

跨进程限制（实测）：
- TB_GETITEMRECT 等带指针的消息发到 explorer 全部返回 (0,0) → 不用
- GetWindowRect 是 USER32 函数不是消息 → 跨进程有效
策略：拿到 ToolbarWindow32 矩形后按网格心/步进悬停，读 tooltips_class32 文本。
"""
import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32


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
    return (r.left, r.top, r.right, r.bottom)


def main_tray_toolbar():
    tray = find("Shell_TrayWnd")
    if not tray:
        return None
    notify = child(tray, "TrayNotifyWnd")
    if not notify:
        return None
    pager = child(notify, "SysPager")
    if not pager:
        return None
    return child(pager, "ToolbarWindow32")


def overflow_toolbar():
    ow = find("NotifyIconOverflowWindow")
    if not ow:
        return None

    def walk(parent, depth=0):
        if depth > 6:
            return None
        c = None
        while True:
            c = user32.FindWindowExW(parent, c, "ToolbarWindow32", None)
            if c and user32.IsWindowVisible(c):
                return c
        c = None
        while True:
            c = user32.FindWindowExW(parent, c, None, None)
            if not c:
                break
            r = walk(c, depth + 1)
            if r:
                return r
        return None

    return walk(ow)


def tooltip_text():
    tip = find("tooltips_class32")
    if not tip:
        return None
    n = user32.GetWindowTextLengthW(tip)
    if n <= 0:
        return None
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(tip, buf, n + 1)
    return buf.value


def hover(x, y, wait=0.45):
    user32.SetCursorPos(x, y)
    time.sleep(wait)
    return tooltip_text()


def click(x, y, times=2):
    for _ in range(times):
        user32.SetCursorPos(x, y)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(0.08)


def scan_main_tray():
    """主托盘可见图标区：从左到右 22px 步进悬停。"""
    tb = main_tray_toolbar()
    if not tb:
        print("main-tray: no toolbar")
        return None
    r = rect_of(tb)
    print("main-tray rect:", r)
    y = (r[1] + r[3]) // 2
    x = r[0] + 10
    while x < r[2] - 4:
        tip = hover(x, y, 0.35)
        if tip:
            print("  hover (%d,%d) -> %r" % (x, y, tip))
            if "NotifyHub" in tip:
                return (x, y)
        x += 22
    return None


def scan_overflow():
    """溢出面板 3x3 网格心悬停。"""
    tb = overflow_toolbar()
    if not tb:
        print("overflow: no toolbar")
        return None
    r = rect_of(tb)
    print("overflow toolbar rect:", r)
    w = r[2] - r[0]
    h = r[3] - r[1]
    for row in range(3):
        for col in range(3):
            cx = r[0] + int((col + 0.5) * w / 3)
            cy = r[1] + int((row + 0.5) * h / 3)
            tip = hover(cx, cy, 0.5)
            print("  hover r%d c%d (%d,%d) -> %r" % (row, col, cx, cy, tip))
            if tip and "NotifyHub" in tip:
                return (cx, cy)
    return None


def main():
    hit = scan_main_tray()
    if not hit:
        # 展开溢出区：点折叠箭头（早前实测 Button 窗口 (1724,1040)-(1748,1080)）
        print("clicking overflow arrow (1736,1060)")
        click(1736, 1060, 1)
        time.sleep(1.2)
        hit = scan_overflow()
    if hit:
        print("FOUND NotifyHub tray icon at", hit)
        click(hit[0], hit[1], 2)
        time.sleep(2.0)
        # 枚举顶级窗口找设置窗
        found = []

        def enum_cb(hwnd, _):
            n = user32.GetWindowTextLengthW(hwnd)
            if n > 0:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                t = buf.value
                if "NotifyHub" in t or "设置" in t:
                    found.append((hwnd, t, rect_of(hwnd)))
            return True

        EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND,
                                             wintypes.LPARAM)
        user32.EnumWindows(EnumWindowsProc(enum_cb), 0)
        for hwnd, t, r in found:
            print("window %d %r rect=%s" % (hwnd, t, r))
    else:
        print("NOT FOUND")
        sys.exit(2)


if __name__ == "__main__":
    main()
