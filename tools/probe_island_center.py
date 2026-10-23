"""灵动岛"是否真的居中"的一次性真机探针（v0.2.10 需求 1 验收）。

同时读出：窗口矩形（EnumWindows，物理像素）、截图里药丸本体的墨迹包围盒。
输出三个中心的横向位置：窗口中心 / 药丸中心 / 屏幕中心，以及各自的偏差。

用法：
    python tools/probe_island_center.py 文本 [等待毫秒]

依赖正在运行的常驻实例（内部会调 --island 把岛弹出来）。
"""
import ctypes
import ctypes.wintypes as w
import subprocess
import sys
import time

sys.path.insert(0, "tools")
from fastshot import capture_full, write_png  # noqa: E402

user32 = ctypes.WinDLL("user32", use_last_error=True)
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass


def find_windows(title_prefix="NotifyHub 灵动岛"):
    found = []

    EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, w.HWND, w.LPARAM)

    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value.startswith(title_prefix):
                r = w.RECT()
                user32.GetWindowRect(hwnd, ctypes.byref(r))
                found.append((hwnd, r))
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return found


def pill_bbox(pixels, width, height, rect):
    """在窗口矩形内找深色药丸的墨迹包围盒（药丸是深色玻璃，桌面是浅色）。"""
    minx, miny, maxx, maxy = 10 ** 9, 10 ** 9, -1, -1
    for y in range(max(0, rect.top), min(height, rect.bottom)):
        row = y * width
        for x in range(max(0, rect.left), min(width, rect.right)):
            i = (row + x) * 4
            r, g, b = pixels[i], pixels[i + 1], pixels[i + 2]
            lum = 0.299 * r + 0.587 * g + 0.114 * b
            if lum < 110:
                if x < minx:
                    minx = x
                if x > maxx:
                    maxx = x
                if y < miny:
                    miny = y
                if y > maxy:
                    maxy = y
    if maxx < 0:
        return None
    return (minx, miny, maxx, maxy)


def main():
    text = sys.argv[1] if len(sys.argv) > 1 else "居中探针：单内容应当整体居中"
    wait_ms = int(sys.argv[2]) if len(sys.argv) > 2 else 320

    subprocess.run(["NotifyHub.exe", "--island", text], capture_output=True, timeout=30)
    time.sleep(wait_ms / 1000.0)

    wins = find_windows()
    if not wins:
        print("FAIL 找不到灵动岛窗口（实例没在跑？）")
        return 1

    hwnd, rect = wins[0]
    width, height, pixels = capture_full()
    box = pill_bbox(pixels, width, height, rect)

    win_center = (rect.left + rect.right) / 2
    screen_center = width / 2
    print("窗口   rect=(%d,%d)-(%d,%d) size=%dx%d center=%.1f"
          % (rect.left, rect.top, rect.right, rect.bottom,
             rect.right - rect.left, rect.bottom - rect.top, win_center))
    print("屏幕   width=%d center=%.1f" % (width, screen_center))

    if box is None:
        print("FAIL 窗口里找不到药丸墨迹（可能还在动画中，加大等待毫秒）")
        return 1

    pill_center = (box[0] + box[2]) / 2
    print("药丸   bbox=(%d,%d)-(%d,%d) size=%dx%d center=%.1f"
          % (box[0], box[1], box[2], box[3],
             box[2] - box[0] + 1, box[3] - box[1] + 1, pill_center))

    win_off = win_center - screen_center
    pill_off = pill_center - screen_center
    print("偏差   窗口中心 %+.1f px / 药丸中心 %+.1f px" % (win_off, pill_off))

    write_png("tools/probe-island-center.png", width, height, pixels)
    print("截图   tools/probe-island-center.png")

    ok = abs(win_off) <= 2 and abs(pill_off) <= 3
    print("RESULT", "PASS" if ok else "FAIL")
    return 0 if ok else 2


if __name__ == "__main__":
    sys.exit(main())
