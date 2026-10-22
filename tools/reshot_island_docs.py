"""重拍文档截图：island.png（音乐组+长文本跑马灯）与 workbuddy-live.png（中性任务状态）。

通过 --island-stream（IPC 快路径，不撞单实例 Mutex）向常驻实例灌状态文本。
"""
import ctypes
import subprocess
import time
from ctypes import wintypes

user32 = ctypes.windll.user32
EXE = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\dist\NotifyHub.exe"
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


def shot_island(path):
    wins = find_window("NotifyHub 灵动岛")
    if not wins:
        print("!! island window not found")
        return
    x0, y0, x1, y1 = rect_of(wins[0])
    w, h = x1 - x0, y1 - y0
    print("island rect = (%d,%d)-(%d,%d) %dx%d" % (x0, y0, x1, y1, w, h))
    subprocess.run([
        "powershell", "-ExecutionPolicy", "Bypass", "-File",
        TOOLS + r"\screenshot-region.ps1",
        "-X", str(x0), "-Y", str(y0), "-W", str(w), "-H", str(h),
        "-OutFile", path,
    ], capture_output=True, timeout=30)
    print("saved", path)


def main():
    p = subprocess.Popen([EXE, "--island-stream"], stdin=subprocess.PIPE)
    time.sleep(1.0)

    # 1) 长文本：超出状态区宽度 → 跑马灯滚动（同时音乐组在放 FOLIA）
    long_text = ("AI 正在整理 v0.2.3 发布物料：长文本跑马灯横向滚动、停靠只允许屏幕顶部且两侧各 1/5 禁停、"
                 "音乐白名单过滤浏览器误触发、消息游标持久化不再补弹旧消息、公众号与服务号消息自动屏蔽")
    p.stdin.write(long_text.encode("utf-8") + b"\n")
    p.stdin.flush()
    time.sleep(1.2)
    shot_island(DOCS + r"\island.png")

    # 2) 中性短文本：任务进行时态
    neutral = "AI 正在汇总本次改动并更新发布文档，完成后自动播放完成提示音"
    p.stdin.write(neutral.encode("utf-8") + b"\n")
    p.stdin.flush()
    time.sleep(1.2)
    shot_island(DOCS + r"\workbuddy-live.png")

    p.stdin.close()
    try:
        p.wait(timeout=5)
    except Exception:
        p.kill()
    print("stream ended")


if __name__ == "__main__":
    main()
