"""需求 8 真机验收：音乐开关连续切换 3 次（勾选→显示、取消→隐藏）。

每轮流程（与真实用户操作一致）：
  打开设置窗(托盘双击) → UIA 切换「音乐控制模块」复选框 → UIA 点击「保存并应用」
  → 窗自动关闭 → 截屏灵动岛 + 读 config.json + 日志佐证

当前基线：MusicEnabled=true，FOLIA 正在播放，音乐组可见。
切换序列：取消→隐藏 / 勾选→显示 / 取消→隐藏，最后恢复 true（保持现场原状）。
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


def find_settings():
    found = []

    def enum_cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == "NotifyHub 设置":
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
        time.sleep(0.1)


def open_settings_via_tray():
    """展开溢出面板 → 双击 NotifyHub 托盘图标，返回设置窗句柄。"""
    click(1736, 1060, 1)      # 折叠箭头
    time.sleep(1.0)
    click(1776, 1020, 2)      # 蓝铃图标
    for _ in range(20):
        time.sleep(0.25)
        wins = find_settings()
        if wins:
            return wins[0]
    return None


def uia(hwnd, name, action):
    # 控制台输出是 GBK 编码，不能按 UTF-8 解
    r = subprocess.run([UIA, str(hwnd), name, action],
                       capture_output=True, timeout=30,
                       encoding="gbk", errors="replace")
    out = ((r.stdout or "") + (r.stderr or "")).strip()
    print("    UIA[%s %s] rc=%d\n      %s" % (name, action, r.returncode,
                                             out.replace("\n", "\n      ")))
    return r.returncode, out


def music_enabled():
    with open(CONFIG, encoding="utf-8") as f:
        return json.load(f).get("MusicEnabled")


def shot(path):
    subprocess.run([
        "powershell", "-ExecutionPolicy", "Bypass", "-File",
        TOOLS + r"\screenshot-region.ps1",
        "-X", "1100", "-Y", "-10", "-W", "860", "-H", "90",
        "-OutFile", path,
    ], capture_output=True, timeout=30)
    print("    shot:", path)


def log_tail(n=6):
    try:
        with open(LOG, encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
        return "".join(lines[-n:]).strip()
    except Exception as e:
        return "log read failed: %s" % e


def main():
    print("== 基线 ==")
    print("  config MusicEnabled =", music_enabled())

    # 序列：取消→隐藏 / 勾选→显示 / 取消→隐藏 / (恢复)勾选→显示
    sequence = [
        (1, "取消", "toggle", False, "reg-music-c1-uncheck.png"),
        (2, "勾选", "toggle", True, "reg-music-c2-check.png"),
        (3, "取消", "toggle", False, "reg-music-c3-uncheck.png"),
        (4, "恢复勾选", "toggle", True, "reg-music-c4-restore.png"),
    ]
    results = []
    for cycle, label, action, expect_cfg, shot_name in sequence:
        print("\n== 第 %d 轮：%s ==" % (cycle, label))
        hwnd = find_settings()[0] if find_settings() else open_settings_via_tray()
        if hwnd is None:
            print("  ERROR: 设置窗打不开")
            results.append((cycle, label, "FAIL", "设置窗打不开"))
            continue
        print("  settings hwnd =", hwnd, "rect =", rect_of(hwnd))

        # 切换前先查状态
        uia(hwnd, "音乐控制模块", "query")
        rc, out = uia(hwnd, "音乐控制模块", "toggle")
        m = re.search(r"state after: (\w+)", out)
        after = m.group(1) if m else "?"
        print("  切换后 UIA 状态 =", after)

        # 保存并应用（窗口随之关闭）
        uia(hwnd, "保存并应用", "invoke")
        time.sleep(1.2)

        cfg = music_enabled()
        print("  保存后 config MusicEnabled =", cfg)
        shot(TOOLS + "\\" + shot_name)
        print("  日志尾部:\n    " + log_tail(4).replace("\n", "\n    "))
        ok = (cfg == expect_cfg)
        results.append((cycle, label, "PASS" if ok else "FAIL",
                        "UIA=%s config=%s" % (after, cfg)))

    print("\n== 汇总 ==")
    for cycle, label, status, detail in results:
        print("  轮%d %-6s %s (%s)" % (cycle, label, status, detail))
    print("\n最终 config MusicEnabled =", music_enabled())


if __name__ == "__main__":
    main()
