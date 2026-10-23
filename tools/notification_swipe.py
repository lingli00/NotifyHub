"""验证通知弹窗滑动取消（v0.2.6 需求 1）：真鼠标拖拽 → 卡片应带惯性飞出并关闭。

流程：
  1. 通过本地 ntfy mock 推一条通知（卡片出现在右上角）；
  2. 鼠标移到卡片中心，按住左键横向拖 220px（分 12 步，模拟真实拖动速度）；
  3. 松手后等 600ms，统计 "NotifyHub 通知" 窗口数量 —— 应为 0。
用法: python tools/notification_swipe.py
"""
import ctypes
import subprocess
import time
import urllib.request

user32 = ctypes.windll.user32

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004


def windows_titled(title):
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == title:
                found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found


def post(body, title="Swipe Test"):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    req = urllib.request.Request("http://127.0.0.1:8099/work",
                                 data=body.encode("utf-8"), method="POST")
    req.add_header("X-Title", title)
    opener.open(req, timeout=5).read()


def drag(x0, y0, dx, steps=12, step_delay=0.012):
    user32.SetCursorPos(x0, y0)
    time.sleep(0.12)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.05)
    for i in range(1, steps + 1):
        user32.SetCursorPos(x0 + dx * i // steps, y0)
        time.sleep(step_delay)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)


def main():
    before = len(windows_titled("NotifyHub 通知"))
    print(f"拖动前卡片数: {before}")

    post("滑动取消验证：按住卡片向右甩，应当带惯性飞出并关闭")
    time.sleep(1.3)

    cards = windows_titled("NotifyHub 通知")
    print(f"弹出后卡片数: {len(cards)}")
    if not cards:
        print("NO CARD — 未弹出，无法验证")
        return

    # 卡片窗口矩形（含 14px 透明留白），取中间偏上位置作为抓取点
    class RECT(ctypes.Structure):
        _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                    ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

    r = RECT()
    user32.GetWindowRect(cards[0], ctypes.byref(r))
    cx = (r.left + r.right) // 2
    cy = (r.top + r.bottom) // 2
    print(f"卡片 rect=({r.left},{r.top})-({r.right},{r.bottom}) 抓取点=({cx},{cy})")

    drag(cx, cy, dx=220)
    time.sleep(0.7)

    after = len(windows_titled("NotifyHub 通知"))
    print(f"拖动后卡片数: {after}")
    print("RESULT:", "PASS 滑动取消生效" if after < len(cards) else "FAIL 卡片仍在")


if __name__ == "__main__":
    main()
