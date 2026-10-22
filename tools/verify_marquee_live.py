"""一体化真机验证：动态定位灵动岛 → 抓拍 → 行剖面 → 状态行双帧滚动检测。

用法: python tools/verify_marquee_live.py
"""
import ctypes
import re
import subprocess
import sys
import time

sys.path.insert(0, "tools")
from compare_frames import read_png

user32 = ctypes.windll.user32


def find_island():
    """用 island_probe info 拿岛窗口矩形（复用现有探测逻辑）。"""
    out = subprocess.run(
        [sys.executable, "tools/island_probe.py", "info"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    m = re.search(r"rect=\((-?\d+),(-?\d+)\)-\((-?\d+),(-?\d+)\)", out.stdout)
    if not m:
        print("探针输出: " + out.stdout)
        raise SystemExit("没找到灵动岛窗口")
    x0, y0, x1, y1 = (int(g) for g in m.groups())
    return x0, y0, x1 - x0, y1 - y0


def grab(path, x, y, w, h):
    out = subprocess.run(
        [sys.executable, "tools/grab.py", path, str(x), str(y), str(w), str(h)],
        capture_output=True, text=True)
    if "saved" not in out.stdout:
        raise SystemExit("抓拍失败: " + out.stdout + out.stderr)


def row_profile(path):
    """每行统计暗/亮像素数。"""
    w, h, ch, px = read_png(path)
    rows = []
    for row in range(h):
        dark = bright = 0
        for col in range(w):
            i = (row * w + col) * ch
            r, g, b = px[i], px[i + 1], px[i + 2]
            if r < 70 and g < 70 and b < 80:
                dark += 1
            elif r > 150 and g > 150 and b > 150:
                bright += 1
        rows.append((dark, bright))
    return w, h, rows


def longest_dark_band(rows, min_dark=300):
    """最长连续暗带（pill 主体）。"""
    best = cur = None
    for r, (d, _) in enumerate(rows):
        if d > min_dark:
            cur = (r, r) if cur is None else (cur[0], r)
            if best is None or cur[1] - cur[0] > best[1] - best[0]:
                best = cur
        else:
            cur = None
    return best


def text_bands(rows, top, bot, min_bright=40):
    """pill 内的文字子带（亮像素超阈值的连续行）。"""
    bands = []
    cur = None
    for r in range(top, bot + 1):
        if rows[r][1] >= min_bright:
            cur = (r, r) if cur is None else (cur[0], r)
        else:
            if cur:
                bands.append(cur)
            cur = None
    if cur:
        bands.append(cur)
    return [b for b in bands if b[1] - b[0] >= 2]


def band_cols(path, y0, y1):
    """指定行带的亮像素列分布。"""
    w, h, ch, px = read_png(path)
    cols = [0] * w
    total = 0
    for row in range(max(0, y0), min(y1, h)):
        for col in range(w):
            i = (row * w + col) * ch
            r, g, b = px[i], px[i + 1], px[i + 2]
            if r > 150 and g > 150 and b > 150:
                cols[col] += 1
                total += 1
    return total, cols


def cross_correlate(c1, c2):
    n = min(len(c1), len(c2))
    best_shift, best_score = 0, -1
    for shift in range(-n, n):
        score = 0
        for i in range(n):
            j = i + shift
            if 0 <= j < n:
                score += min(c1[i], c2[j])
        if score > best_score:
            best_score, best_shift = score, shift
    return best_shift


def main():
    x0, y0, w, h = find_island()
    print("灵动岛窗口: (%d,%d) %dx%d  中心x=%d" % (x0, y0, w, h, x0 + w // 2))

    user32.SetCursorPos(60, 900)  # 光标移开，排除悬停暂停
    time.sleep(0.3)

    shot = "tools/verify-island.png"
    grab(shot, x0, y0, w, h)
    _, _, rows = row_profile(shot)
    pill = longest_dark_band(rows)
    if not pill:
        raise SystemExit("没找到 pill 暗带")
    print("pill 垂直范围（窗口内）: y=%d..%d" % pill)

    bands = text_bands(rows, pill[0], pill[1])
    print("pill 内文字带（窗口内）: %s" % (
        ", ".join("y%d-%d(峰%d)" % (b[0], b[1], max(rows[r][1] for r in range(b[0], b[1] + 1))) for b in bands)
        or "无"))
    if not bands:
        raise SystemExit("pill 内没有文字带")

    # 逐带检测滚动
    for idx, (ty0, ty1) in enumerate(bands):
        a, b = "tools/verify-band%d-a.png" % idx, "tools/verify-band%d-b.png" % idx
        grab(a, x0, y0 + ty0 - 1, w, ty1 - ty0 + 3)
        time.sleep(1.0)
        grab(b, x0, y0 + ty0 - 1, w, ty1 - ty0 + 3)
        t1, c1 = band_cols(a, 1, ty1 - ty0 + 2)
        t2, c2 = band_cols(b, 1, ty1 - ty0 + 2)
        if t1 == 0 or t2 == 0:
            print("  文字带 y%d-%d: 无文字" % (ty0, ty1))
            continue
        shift = cross_correlate(c1, c2)
        verdict = "滚动中 ✓" if shift < -5 else ("静止（悬停/文本未超宽）" if abs(shift) <= 5 else "异常右移 %+d" % shift)
        print("  文字带 y%d-%d: 亮像素 %d→%d 互相关位移=%+d → %s" % (ty0, ty1, t1, t2, shift, verdict))


if __name__ == "__main__":
    main()
