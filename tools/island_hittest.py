"""验证灵动岛点按穿透（v0.2.6 需求 2）。

做法：给岛窗口直接发 WM_NCHITTEST（带屏幕坐标），看返回值：
  -1 = HTTRANSPARENT（穿透，点击落到下层窗口）
   0 = HTNOWHERE / 1 = HTCLIENT（岛自己接收）

预期：播放按钮位置返回非 -1；文字/空白位置返回 -1。
用法: python tools/island_hittest.py
"""
import ctypes
import subprocess
import time
from ctypes import wintypes

user32 = ctypes.windll.user32

WM_NCHITTEST = 0x0084
HTTRANSPARENT = -1

user32.SendMessageW.restype = ctypes.c_long


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find_island():
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == "NotifyHub 灵动岛":
                found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else None


def hit(hwnd, x, y):
    lp = (y << 16) | (x & 0xFFFF)
    return user32.SendMessageW(hwnd, WM_NCHITTEST, 0, lp)


def main():
    exe = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\dist\tmp-v26\NotifyHub.exe"
    subprocess.run([exe, "--island", "点按穿透验证"], timeout=20)
    time.sleep(0.8)

    hwnd = find_island()
    if not hwnd:
        print("ISLAND NOT FOUND")
        return

    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    print(f"island hwnd={hwnd} rect=({r.left},{r.top})-({r.right},{r.bottom})")

    # Pill 内缩 14（DIP→物理按 1.5 缩放）= 21px
    pad = 21
    x0, y0 = r.left + pad, r.top + pad
    x1 = r.right - pad - r.x if False else r.right - pad
    y1 = r.bottom - pad

    print("\n扫描 Pill 内的命中结果（T=穿透 CLIENT=岛接收）：")
    rows = []
    steps = 9
    for i in range(steps):
        y = y0 + (y1 - y0) * i // max(1, steps - 1)
        line = []
        for j in range(12):
            x = x0 + (x1 - x0) * j // 11
            code = hit(hwnd, x, y)
            line.append("T " if code == HTTRANSPARENT else "C ")
        rows.append("".join(line))

    for row in rows:
        print("  " + row)

    # 精确检查：播放按钮（UIA 取矩形）
    uia = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\UiaToggle\bin\Debug\net8.0-windows10.0.19041.0\win-x64\UiaToggle.exe"
    try:
        out = subprocess.run([uia, str(hwnd), "播放 / 暂停", "query"],
                             capture_output=True, timeout=20)
        text = out.stdout.decode("gbk", "replace")
        print("\n播放按钮 UIA:", text.strip().replace("\n", " | "))

        # 从输出里解析 rect=(x0,y0)-(x1,y1)
        import re
        m = re.search(r"rect=\(([\d.]+),([\d.]+)\)-\(([\d.]+),([\d.]+)\)", text)
        if m:
            bx = int((float(m.group(1)) + float(m.group(3))) / 2)
            by = int((float(m.group(2)) + float(m.group(4))) / 2)
            code = hit(hwnd, bx, by)
            print(f"按钮中心 ({bx},{by}) → WM_NCHITTEST={code} "
                  f"({'接收 ✓' if code != HTTRANSPARENT else '穿透 ✗ 按钮失灵'})")
    except FileNotFoundError:
        print("UiaToggle 未编译，跳过按钮精确校验")


if __name__ == "__main__":
    main()
