"""悬停法定位 NotifyHub 托盘图标（支持溢出区），找到后双击打开设置窗。

托盘结构（均用 FindWindowEx 逐层定位，EnumWindows 只列顶级窗口会漏掉子工具栏）：
  主托盘:  Shell_TrayWnd → TrayNotifyWnd → SysPager → ToolbarWindow32
  溢出区:  点击折叠箭头后弹出 NotifyIconOverflowWindow → ... → ToolbarWindow32
识别方式：悬停按钮 → 读 tooltips_class32 弹窗文本（含 "NotifyHub" 即命中）。
"""
import ctypes
import time
from ctypes import wintypes

user32 = ctypes.windll.user32

TB_BUTTONCOUNT = 0x0418
TB_GETITEMRECT = 0x041D


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def child(hwnd, cls):
    return user32.FindWindowExW(hwnd, None, cls, None)


def main_tray_toolbar():
    """主托盘可见图标区的 ToolbarWindow32。"""
    tray = user32.FindWindowW("Shell_TrayWnd", None)
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
    """溢出弹窗里的 ToolbarWindow32（点开折叠箭头后才存在）。"""
    overflow = user32.FindWindowW("NotifyIconOverflowWindow", None)
    if not overflow:
        return None
    # 溢出弹窗结构: NotifyIconOverflowWindow → ... → ToolbarWindow32
    # 逐层找：先 ListView/ListBox 容器，再 Toolbar
    def find_tb(parent, depth=0):
        if depth > 6:
            return None
        c = None
        while True:
            c = user32.FindWindowExW(parent, c, "ToolbarWindow32", None)
            if not c:
                break
            if user32.IsWindowVisible(c):
                return c
        c = None
        while True:
            c = user32.FindWindowExW(parent, c, None, None)
            if not c:
                break
            r = find_tb(c, depth + 1)
            if r:
                return r
        return None

    return find_tb(overflow)


def button_rect(toolbar, index):
    rect = RECT()
    if user32.SendMessageW(toolbar, TB_GETITEMRECT, index, ctypes.byref(rect)):
        cx = (rect.left + rect.right) // 2
        cy = (rect.top + rect.bottom) // 2
        if cx != 0 or cy != 0:
            return cx, cy
    return None


def tooltip_text():
    tip = user32.FindWindowW("tooltips_class32", None)
    if not tip:
        return None
    n = user32.GetWindowTextLengthW(tip)
    if n <= 0:
        return None
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(tip, buf, n + 1)
    return buf.value


def scan(toolbar, label):
    """悬停工具栏每个按钮，返回 NotifyHub 图标的 (x, y)。"""
    if not toolbar:
        print("%s: 无工具栏" % label)
        return None
    count = user32.SendMessageW(toolbar, TB_BUTTONCOUNT, 0, 0)
    print("%s: %d 个按钮" % (label, count))
    for i in range(count):
        pos = button_rect(toolbar, i)
        if not pos:
            continue
        cx, cy = pos
        user32.SetCursorPos(cx, cy)
        time.sleep(0.5)
        tip = tooltip_text()
        print("  悬停(%d,%d) → %r" % (cx, cy, tip))
        if tip and "NotifyHub" in tip:
            return cx, cy
    return None


def click(x, y, times=2):
    for _ in range(times):
        user32.SetCursorPos(x, y)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(0.08)


def main():
    hit = scan(main_tray_toolbar(), "主托盘")
    if not hit:
        # 展开溢出区：点主托盘最后一个按钮（通常是折叠箭头）
        tb = main_tray_toolbar()
        if tb:
            count = user32.SendMessageW(tb, TB_BUTTONCOUNT, 0, 0)
            pos = button_rect(tb, count - 1)
            if pos:
                print("点击折叠箭头 (%d,%d)" % pos)
                click(pos[0], pos[1], 1)
                time.sleep(1.0)
        hit = scan(overflow_toolbar(), "溢出区")
    if hit:
        print("双击 NotifyHub 托盘图标 (%d,%d)" % hit)
        click(hit[0], hit[1], 2)
    else:
        print("未找到 NotifyHub 托盘图标")


if __name__ == "__main__":
    main()
