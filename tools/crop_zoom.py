"""裁剪 PNG 区域并最近邻放大，存为新 PNG（无 PIL 依赖）。

用法: python tools/crop_zoom.py in.png x0 y0 x1 y1 scale out.png
"""
import struct
import sys
import zlib


def read_png(path):
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
        if ft == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ft == 4:
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


def write_png(path, w, h, channels, px):
    stride = w * channels
    raw = bytearray()
    for row in range(h):
        raw.append(0)  # filter None
        raw += px[row * stride:(row + 1) * stride]

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8,
                                      2 if channels == 3 else 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def main():
    src, x0, y0, x1, y1, scale, dst = (
        sys.argv[1], int(sys.argv[2]), int(sys.argv[3]),
        int(sys.argv[4]), int(sys.argv[5]), int(sys.argv[6]), sys.argv[7])
    w, h, ch, px = read_png(src)
    x0 = max(0, x0)
    y0 = max(0, y0)
    x1 = min(w, x1)
    y1 = min(h, y1)
    cw, chh = x1 - x0, y1 - y0
    ow, oh = cw * scale, chh * scale
    out = bytearray(ow * oh * ch)
    for y in range(oh):
        sy = y0 + y // scale
        for x in range(ow):
            sx = x0 + x // scale
            si = (sy * w + sx) * ch
            di = (y * ow + x) * ch
            out[di:di + ch] = px[si:si + ch]
    write_png(dst, ow, oh, ch, out)
    print("saved %s (%dx%d from %d,%d-%d,%d x%d)" % (dst, ow, oh, x0, y0, x1, y1, scale))


if __name__ == "__main__":
    main()
