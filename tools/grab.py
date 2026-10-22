"""Screen region capture via GDI BitBlt (CAPTUREBLT so layered WPF windows show up).

No third-party deps: raw DIB -> zlib-compressed PNG written by hand.
Usage: python tools/grab.py out.png [x y w h]
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import zlib

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

SRCCOPY_CAPTUREBLT = 0x40CC0020
DIB_RGB_COLORS = 0
BI_RGB = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD),
        ("biWidth", wt.LONG),
        ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD),
        ("biBitCount", wt.WORD),
        ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD),
        ("biXPelsPerMeter", wt.LONG),
        ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def capture(x, y, w, h):
    screen = user32.GetDC(None)
    mem = gdi32.CreateCompatibleDC(screen)
    bmp = gdi32.CreateCompatibleBitmap(screen, w, h)
    old = gdi32.SelectObject(mem, bmp)

    ok = gdi32.BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY_CAPTUREBLT)
    if not ok:
        raise RuntimeError("BitBlt failed")

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h  # top-down
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = BI_RGB

    buf = ctypes.create_string_buffer(w * h * 4)
    got = gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS)

    gdi32.SelectObject(mem, old)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(None, screen)

    if got != h:
        raise RuntimeError("GetDIBits returned %d of %d lines" % (got, h))
    return buf.raw


def to_png(raw, w, h, bgra=True):
    """Convert 32-bit BGRA/BGRX rows into an RGB PNG (alpha dropped intentionally)."""
    rows = []
    stride = w * 4
    for y in range(h):
        line = raw[y * stride:(y + 1) * stride]
        rgb = bytearray(1 + w * 3)  # filter byte 0 + RGB
        o = 1
        for x in range(w):
            i = x * 4
            if bgra:
                rgb[o] = line[i + 2]
                rgb[o + 1] = line[i + 1]
                rgb[o + 2] = line[i]
            else:
                rgb[o] = line[i]
                rgb[o + 1] = line[i + 1]
                rgb[o + 2] = line[i + 2]
            o += 3
        rows.append(bytes(rgb))
    data = b"".join(rows)

    def chunk(tag, payload):
        c = struct.pack(">I", len(payload)) + tag + payload
        return c + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)  # 8-bit truecolor RGB
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(data, 9))
            + chunk(b"IEND", b""))


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "shot.png"
    if len(sys.argv) >= 6:
        x, y, w, h = (int(v) for v in sys.argv[2:6])
    else:
        x, y = 0, 0
        w = user32.GetSystemMetrics(0)
        h = user32.GetSystemMetrics(1)

    raw = capture(x, y, w, h)
    png = to_png(raw, w, h)
    with open(out, "wb") as f:
        f.write(png)
    print("saved %s (%dx%d from %d,%d) %d bytes" % (out, w, h, x, y, len(png)))


if __name__ == "__main__":
    main()
