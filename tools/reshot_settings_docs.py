"""重拍设置窗文档截图：settings.png（顶部全景）+ settings-music.png（白名单与浏览器分组）。

流程：托盘双击打开设置窗 → 截全窗 → 鼠标滚轮下滚 → 截全窗。
"""
import ctypes
import subprocess
import time
from ctypes import wintypes

user32 = ctypes.windll.user32
TOOLS = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools"
DOCS = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\docs"


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def rect_of(hwnd):
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def find_window(title):
    found = []

    def enum_cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == title:
                found.append(hwnd)
        return True

    EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND,
                                         wintypes.LPARAM)
    user32.EnumWindows(EnumWindowsProc(enum_cb), 0)
    return found


def click(x, y, times=1):
    for _ in range(times):
        user32.SetCursorPos(x, y)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(0.12)


def open_settings():
    for attempt in range(3):
        click(1736, 1060, 1)
        time.sleep(1.0)
        click(1776, 1020, 2)
        for _ in range(24):
            time.sleep(0.25)
            wins = find_window("NotifyHub 设置")
            if wins:
                return wins[0]
        print("  第 %d 次双击未唤起，重试" % (attempt + 1))
    return None


def shot(hwnd, path):
    x0, y0, x1, y1 = rect_of(hwnd)
    w, h = x1 - x0, y1 - y0
    subprocess.run([
        "powershell", "-ExecutionPolicy", "Bypass", "-File",
        TOOLS + r"\screenshot-region.ps1",
        "-X", str(x0), "-Y", str(y0), "-W", str(w), "-H", str(h),
        "-OutFile", path,
    ], capture_output=True, timeout=30)
    print("saved", path, "(%dx%d)" % (w, h))


def main():
    hwnd = (find_window("NotifyHub 设置") or [None])[0] or open_settings()
    if hwnd is None:
        print("ERROR: 设置窗打不开")
        return
    x0, y0, x1, y1 = rect_of(hwnd)
    print("settings rect =", (x0, y0, x1, y1))

    # 1) 顶部全景
    shot(hwnd, DOCS + r"\settings.png")

    # 2) 下滚到「音乐 App 白名单与浏览器」分组
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2 + 100
    user32.SetCursorPos(cx, cy)
    for _ in range(6):
        user32.mouse_event(0x0800, 0, 0, -120, 0)  # 滚轮下滚
        time.sleep(0.15)
    time.sleep(0.8)
    shot(hwnd, DOCS + r"\settings-music.png")

    # 关闭设置窗（不保存，避免改动用户配置）——发 Esc 无效（WPF 不认），
    # 用 UIA 点「取消」
    r = subprocess.run([
        TOOLS + r"\UiaToggle\bin\Debug\net8.0-windows\win-x64\UiaToggle.exe",
        str(hwnd), "取消", "invoke"],
        capture_output=True, timeout=30, encoding="gbk", errors="replace")
    print("cancel:", (r.stdout or "").strip())


if __name__ == "__main__":
    main()
