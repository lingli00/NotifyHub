"""需求 8 真机验收（修正版）：音乐开关连续切换 3 次（勾选→显示、取消→隐藏）。

改进：
- 每次截屏前动态枚举灵动岛窗口矩形（岛会随音乐组显隐变宽/窄、吸顶/落回）
- 托盘双击打开设置窗带 3 次重试
- 序列：勾选→显示 / 取消→隐藏 / 勾选→显示（当前 config=false，跑完恢复 true）
"""
import ctypes
import json
import re
import subprocess
import time
from ctypes import wintypes

user32 = ctypes.windll.user32
TOOLS = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools"
UIA = TOOLS + r"\UiaToggle\bin\Debug\net8.0-windows\win-x64\UiaToggle.exe"
PORTABLE = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\dist\portable\NotifyHub-v0.2.3"
CONFIG = PORTABLE + r"\config.json"
LOG = PORTABLE + r"\logs\NotifyHub-20261022.log"


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def rect_of(hwnd):
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def find_windows(title):
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


def open_settings_via_tray():
    for attempt in range(3):
        click(1736, 1060, 1)      # 折叠箭头展开溢出面板
        time.sleep(1.0)
        click(1776, 1020, 2)      # 双击蓝铃 NotifyHub 图标
        for _ in range(24):       # 最多等 6s
            time.sleep(0.25)
            wins = find_windows("NotifyHub 设置")
            if wins:
                return wins[0]
        print("    第 %d 次双击未唤起设置窗，重试" % (attempt + 1))
    return None


def uia(hwnd, name, action):
    r = subprocess.run([UIA, str(hwnd), name, action],
                       capture_output=True, timeout=30,
                       encoding="gbk", errors="replace")
    out = ((r.stdout or "") + (r.stderr or "")).strip()
    print("    UIA[%s|%s] rc=%d %s" % (name, action, r.returncode,
                                       out.replace("\n", " | ")))
    return r.returncode, out


def music_enabled():
    with open(CONFIG, encoding="utf-8") as f:
        return json.load(f).get("MusicEnabled")


def shot_island(path):
    """动态定位灵动岛窗口并整窗截屏。"""
    wins = find_windows("NotifyHub 灵动岛")
    if not wins:
        print("    !! 灵动岛窗口未找到")
        return None
    x0, y0, x1, y1 = rect_of(wins[0])
    w, h = x1 - x0, y1 - y0
    print("    island rect = (%d,%d)-(%d,%d) %dx%d" % (x0, y0, x1, y1, w, h))
    subprocess.run([
        "powershell", "-ExecutionPolicy", "Bypass", "-File",
        TOOLS + r"\screenshot-region.ps1",
        "-X", str(x0), "-Y", str(y0), "-W", str(w), "-H", str(h),
        "-OutFile", path,
    ], capture_output=True, timeout=30)
    print("    shot:", path)
    return (x0, y0, w, h)


def log_tail(n=4):
    try:
        with open(LOG, encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
        return "".join(lines[-n:]).strip()
    except Exception as e:
        return "log read failed: %s" % e


def main():
    print("== 基线 config MusicEnabled =", music_enabled(), "==")

    sequence = [
        ("勾选", True, "reg-music2-c1-check.png"),
        ("取消", False, "reg-music2-c2-uncheck.png"),
        ("勾选", True, "reg-music2-c3-check.png"),
    ]
    results = []
    for i, (label, expect_cfg, shot_name) in enumerate(sequence, 1):
        print("\n== 第 %d 轮：%s ==" % (i, label))
        hwnd = (find_windows("NotifyHub 设置") or [None])[0] or open_settings_via_tray()
        if hwnd is None:
            print("  ERROR: 设置窗打不开")
            results.append((i, label, "FAIL", "设置窗打不开"))
            continue

        rc, out = uia(hwnd, "音乐控制模块", "toggle")
        m = re.search(r"state after: (\w+)", out)
        after = m.group(1) if m else "?"
        expect_uia = "On" if expect_cfg else "Off"
        uia_ok = (after == expect_uia)

        uia(hwnd, "保存并应用", "invoke")
        time.sleep(1.2)

        cfg = music_enabled()
        shot_island(TOOLS + "\\" + shot_name)
        print("  日志: " + log_tail(2).replace("\n", " / "))
        cfg_ok = (cfg == expect_cfg)
        results.append((i, label,
                        "PASS" if (uia_ok and cfg_ok) else "FAIL",
                        "UIA=%s(期望%s) config=%s(期望%s)" %
                        (after, expect_uia, cfg, expect_cfg)))

    print("\n== 汇总 ==")
    for i, label, status, detail in results:
        print("  轮%d %-4s %s (%s)" % (i, label, status, detail))
    print("\n最终 config MusicEnabled =", music_enabled())


if __name__ == "__main__":
    main()
