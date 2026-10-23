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
import os
import subprocess
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)

# 物理像素读数：否则 150% 缩放下 GetWindowRect 返回的是 DPI 虚拟化坐标，居中判断会错
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass

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


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32), ("rcMonitor", RECT),
                ("rcWork", RECT), ("dwFlags", ctypes.c_uint32)]


def work_area():
    """主屏工作区（物理像素，已扣掉任务栏）。"""
    mi = MONITORINFO()
    mi.cbSize = ctypes.sizeof(MONITORINFO)
    hmon = user32.MonitorFromPoint(w.POINT(0, 0), 1)  # MONITOR_DEFAULTTOPRIMARY
    user32.GetMonitorInfoW(hmon, ctypes.byref(mi))
    return mi.rcWork


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


def center(text):
    """拉起一次 --island，量真实窗口矩形相对工作区的左右留白，判断是否真居中。"""
    exe = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       "src", "NotifyHub", "bin", "Release",
                       "net8.0-windows10.0.19041.0", "win-x64", "NotifyHub.exe")
    if not os.path.exists(exe):
        print("EXE NOT FOUND:", exe)
        return 1

    proc = subprocess.Popen([exe, "--island", text], cwd=os.path.dirname(exe),
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    # 框架依赖版首次启动要 1.5~3s（hostfxr + WPF 初始化），轮询等窗口出现
    hwnd = None
    for _ in range(30):
        time.sleep(0.25)
        hwnd = find_island()
        if hwnd is not None:
            break

    if hwnd is None:
        proc.terminate()
        print("ISLAND NOT FOUND")
        return 1

    r = rect_of(hwnd)
    for _ in range(8):  # 等位移动画落定：连续两次采样一致
        time.sleep(0.2)
        again = rect_of(find_island() or hwnd)
        if (again.left, again.top, again.right, again.bottom) == (r.left, r.top, r.right, r.bottom):
            break
        r = again

    proc.terminate()
    try:
        proc.wait(timeout=5)
    except Exception:
        proc.kill()
    time.sleep(0.4)

    work = work_area()
    left_gap = r.left - work.left
    right_gap = work.right - r.right
    width = r.right - r.left
    drift = abs(left_gap - right_gap)

    print(f"text='{text}' width={width}")
    print(f"rect=({r.left},{r.top})-({r.right},{r.bottom})")
    print(f"work=({work.left},{work.top})-({work.right},{work.bottom})")
    print(f"上距={r.top - work.top} 左留白={left_gap} 右留白={right_gap} 居中偏差={drift}")
    print("CENTER " + ("OK" if drift <= 2 else "FAIL"))
    return 0 if drift <= 2 else 2


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    cmd = sys.argv[1].lower()

    if cmd == "info":
        return info()

    if cmd == "center":
        # center [text]：自己拉起 --island，量真实矩形并判断是否真居中（需求 5）
        text = sys.argv[2] if len(sys.argv) > 2 else "正在梳理需求文档结构并拆分任务"
        return center(text)

    if cmd in ("drag", "rdrag"):
        dx = int(sys.argv[2]) if len(sys.argv) > 2 else 0
        dy = int(sys.argv[3]) if len(sys.argv) > 3 else 0
        return drag(dx, dy, right=(cmd == "rdrag"))

    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main())
