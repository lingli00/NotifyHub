"""把离屏渲染的岛 PNG（透明底）合成到示意背景上，供 README 文档图使用。

用法: python tools/compose_island_doc.py <in.png> <out.png>
背景用一块低饱和平滑渐变 + 一处柔和光斑，主要是让毛玻璃的半透明看得出来。
"""
import math
import sys

sys.path.insert(0, "tools")
from crop_zoom import read_png, write_png  # noqa: E402


def backdrop(w, h):
    """生成背景像素（RGB，自上而下）。"""
    pixels = bytearray(w * h * 4)
    for y in range(h):
        t = y / max(1, h - 1)
        r = int(226 - 46 * t)
        g = int(232 - 34 * t)
        b = int(240 - 20 * t)
        for x in range(w):
            # 右上角一团柔光
            dx, dy = (x - w * 0.82) / (w * 0.45), (y - h * 0.15) / (h * 0.55)
            glow = max(0.0, 1.0 - math.sqrt(dx * dx + dy * dy))
            rr = min(255, int(r + 22 * glow))
            gg = min(255, int(g + 18 * glow))
            bb = min(255, int(b + 10 * glow))
            i = (y * w + x) * 4
            pixels[i] = rr
            pixels[i + 1] = gg
            pixels[i + 2] = bb
            pixels[i + 3] = 255
    return pixels


def main():
    if len(sys.argv) < 3:
        print("usage: compose_island_doc.py <in.png> <out.png>")
        return

    src, dst = sys.argv[1], sys.argv[2]
    w, h, ch, px = read_png(src)
    canvas = backdrop(w, h)

    for y in range(h):
        for x in range(w):
            i = (y * w + x) * ch
            a = px[i + 3] / 255.0
            if a <= 0:
                continue
            for c in range(3):
                canvas[i + c] = int(px[i + c] * a + canvas[i + c] * (1 - a))
    write_png(dst, w, h, 4, canvas)
    print(f"composed {dst} ({w}x{h})")


if __name__ == "__main__":
    main()
