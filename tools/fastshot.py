"""快速验证通知卡：POST 两条 ntfy 消息后立即用 ctypes BitBlt 抓屏（零进程启动开销）。

用法: python tools/fastshot.py [out.png] [crop_x0 crop_y0 crop_x1 crop_y1]
"""
import ctypes
import sys
import time
import urllib.request

# 关键：先声明 DPI aware，否则 GetSystemMetrics 返回 DPI 虚拟化尺寸
# （150% 缩放下物理 1920x1080 会被当成 1280x720，右侧通知卡全部漏抓）
try:
    ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # PerMonitorAwareV2
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

gdi32 = ctypes.windll.gdi32
user32 = ctypes.windll.user32

SRCCOPY = 0x00CC0020
CAPTUREBLT = 0x40000000


def capture_full():
    screen_dc = user32.GetDC(None)
    mem_dc = gdi32.CreateCompatibleDC(screen_dc)
    w = user32.GetSystemMetrics(0)
    h = user32.GetSystemMetrics(1)
    bmp = gdi32.CreateCompatibleBitmap(screen_dc, w, h)
    gdi32.SelectObject(mem_dc, bmp)
    gdi32.BitBlt(mem_dc, 0, 0, w, h, screen_dc, 0, 0, SRCCOPY | CAPTUREBLT)

    # 拿到 BITMAPINFOHEADER 后 GetDIBits
    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [
            ("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32), ("biHeight", ctypes.c_int32),
            ("biPlanes", ctypes.c_uint16), ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
            ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
            ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32), ("biClrImportant", ctypes.c_uint32),
        ]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h  # 自上而下
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem_dc, bmp, 0, h, buf, ctypes.byref(bi), 0)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem_dc)
    user32.ReleaseDC(None, screen_dc)
    return w, h, buf.raw


def write_png(path, w, h, pixels):
    import struct
    import zlib

    raw = bytearray()
    stride = w * 4
    for y in range(h):
        row = bytearray(pixels[y * stride:(y + 1) * stride])
        # BGRA → RGBA
        for x in range(0, stride, 4):
            row[x], row[x + 2] = row[x + 2], row[x]
        raw.append(0)
        raw += row

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 6))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def post(url, title, body):
    req = urllib.request.Request(url, data=body.encode("utf-8"), method="POST")
    req.add_header("X-Title", title)
    urllib.request.urlopen(req, timeout=5).read()


if __name__ == "__main__":
    import time

    out = sys.argv[1] if len(sys.argv) > 1 else "tools/reg-fast.png"
    stamp = time.strftime("%H:%M:%S")
    post("http://127.0.0.1:8099/work", "work-alert", f"[{stamp}] 构建服务器 v0.2.4 发布完成，请查收")
    post("http://127.0.0.1:8099/home", "home-alert", f"[{stamp}] 晚饭好了，记得回来吃饭")
    # 等 SSE 到达 + 卡片创建渲染（通知显示 4s，0.9s 后抓图仍在窗口内）
    time.sleep(0.9)
    w, h, px = capture_full()
    write_png(out, w, h, px)
    print(f"saved {out} ({w}x{h})")
