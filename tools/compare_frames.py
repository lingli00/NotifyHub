"""对比两张状态行截屏：统计亮像素列分布，判断文字是否在滚动。

用法: python tools/compare_frames.py a.png [b.png ...]
输出：每帧的亮像素数、亮区左右边界、亮像素重心；多帧时输出最佳互相关位移。
"""
import struct
import sys
import zlib


def read_png(path):
    """最小 PNG 读取器（支持 colortype 2=RGB / 6=RGBA，bitdepth 8）。"""
    with open(path, "rb") as f:
        data = f.read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "不是 PNG"
    pos = 8
    idat = b""
    w = h = 0
    colortype = 0
    while pos < len(data):
        length, ctype = struct.unpack(">I4s", data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + length]
        if ctype == b"IHDR":
            w, h, bitdepth, colortype = struct.unpack(">IIBB", chunk[:10])
            assert bitdepth == 8 and colortype in (2, 6), (bitdepth, colortype)
        elif ctype == b"IDAT":
            idat += chunk
        pos += 12 + length
    channels = 3 if colortype == 2 else 4
    raw = zlib.decompress(idat)
    stride = w * channels
    out = bytearray(w * h * channels)
    prev = bytearray(stride)
    p = 0
    for row in range(h):
        ft = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if ft == 1:  # Sub
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ft == 2:  # Up
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:  # Average
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ft == 4:  # Paeth
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                pp = a + b - c
                pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out[row * stride:(row + 1) * stride] = line
        prev = line
    return w, h, channels, out


def lit_columns(path):
    w, h, channels, px = read_png(path)
    cols = [0] * w
    total = 0
    for row in range(h):
        for col in range(w):
            i = (row * w + col) * channels
            # 亮文字：RGB 三通道都高（深色底 R/G/B≈44 通不过）
            if px[i] > 150 and px[i + 1] > 150 and px[i + 2] > 150:
                cols[col] += 1
                total += 1
    return w, h, cols, total


def main():
    files = sys.argv[1:]
    if not files:
        print(__doc__)
        return 1
    results = []
    for f in files:
        w, h, cols, total = lit_columns(f)
        lit_cols = [i for i, c in enumerate(cols) if c > 0]
        span = (lit_cols[0], lit_cols[-1]) if lit_cols else None
        center = (sum(i * c for i, c in enumerate(cols)) / total) if total else -1
        print("%s: %dx%d 亮像素=%d 亮区=[%s..%s] 亮像素重心=%.1f (画面中心=%d)" % (
            f.replace("\\", "/").split("/")[-1], w, h, total,
            span[0] if span else "-", span[1] if span else "-", center, w // 2))
        results.append((f, cols, total))
    if len(results) >= 2:
        (f1, c1, t1), (f2, c2, t2) = results[0], results[1]
        if t1 == 0 or t2 == 0:
            print("结论: 至少一帧没有文字")
            return 0
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
        print("最佳互相关位移 = %+d px（负=文字向左移动/滚动中）" % best_shift)
    return 0


if __name__ == "__main__":
    sys.exit(main())
