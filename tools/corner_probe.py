"""四角通知位置探针：逐角跑 --demo，枚举通知窗口真实矩形，打印相对工作区的偏差。

用法：python tools/corner_probe.py
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import shutil
import subprocess
import time

u = ctypes.windll.user32
u.SetProcessDpiAwarenessContext(-4)  # 物理像素

EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "src", "NotifyHub", "bin", "Release",
                   "net8.0-windows10.0.19041.0", "win-x64", "NotifyHub.exe")
CFG_DIR = os.path.join(os.environ["APPDATA"], "NotifyHub")
CFG = os.path.join(CFG_DIR, "config.json")

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", wt.RECT),
                ("rcWork", wt.RECT), ("dwFlags", wt.DWORD)]


def work_area():
    """主屏工作区（物理像素）。"""
    mi = MONITORINFO()
    mi.cbSize = ctypes.sizeof(MONITORINFO)
    u.MonitorFromPoint(wt.POINT(0, 0), 1)  # MONITOR_DEFAULTTOPRIMARY
    hmon = u.MonitorFromPoint(wt.POINT(0, 0), 1)
    u.GetMonitorInfoW(hmon, ctypes.byref(mi))
    return mi.rcMonitor, mi.rcWork


def notify_windows():
    found = []

    def cb(hwnd, _):
        buf = ctypes.create_unicode_buffer(256)
        u.GetWindowTextW(hwnd, buf, 256)
        if buf.value == "NotifyHub 通知" and u.IsWindowVisible(hwnd):
            r = wt.RECT()
            u.GetWindowRect(hwnd, ctypes.byref(r))
            found.append((r.left, r.top, r.right, r.bottom))
        return True

    u.EnumWindows(WNDENUMPROC(cb), 0)
    return sorted(found, key=lambda x: x[1])


def set_corner(corner):
    with open(CFG, encoding="utf-8-sig") as f:
        cfg = json.load(f)
    cfg["NotificationCorner"] = corner
    cfg["MaxStack"] = 5
    with open(CFG, "w", encoding="utf-8-sig") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=4)


def run(corner, seconds=3.2):
    set_corner(corner)
    p = subprocess.Popen([EXE, "--demo"],
                         cwd=os.path.dirname(EXE),
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # 等最后一帧动画落定：连续两次采样一致才算稳定
    time.sleep(2.0)
    rects = notify_windows()
    for _ in range(8):
        time.sleep(0.2)
        again = notify_windows()
        if again == rects:
            break
        rects = again
    p.terminate()
    try:
        p.wait(timeout=5)
    except subprocess.TimeoutExpired:
        p.kill()
    time.sleep(0.6)
    return rects


def main():
    mon, work = work_area()
    print("monitor=(%d,%d)-(%d,%d)  work=(%d,%d)-(%d,%d)" % (
        mon.left, mon.top, mon.right, mon.bottom,
        work.left, work.top, work.right, work.bottom))
    for corner in ("TopLeft", "TopRight", "BottomLeft", "BottomRight"):
        rects = run(corner)
        print("\n== %s  卡片数=%d" % (corner, len(rects)))
        for i, (l, t, r, b) in enumerate(rects):
            # 卡片本体 = 窗口去掉 14px 阴影留白
            cl, ct, cr, cb = l + 14, t + 14, r - 14, b - 14
            print("  #%d win=(%d,%d)-(%d,%d) card=(%d,%d)-(%d,%d) "
                  "左距=%d 右距=%d 上距=%d 下距=%d" % (
                      i, l, t, r, b, cl, ct, cr, cb,
                      cl - work.left, work.right - cr,
                      ct - work.top, work.bottom - cb))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
