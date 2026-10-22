"""抓 NotifyHub 通知卡窗口内容（PrintWindow + PW_RENDERFULLCONTENT，不依赖屏幕合成）。

先 POST 两条 ntfy 消息（工作/家庭两个订阅），等卡片弹出后按窗口句柄逐张抓取。
用法: python tools/card_shot.py [out_prefix]
"""
import ctypes
import struct
import sys
import time
import urllib.request
import zlib
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

PW_RENDERFULLCONTENT = 0x00000002


def post(url, title, body):
    req = urllib.request.Request(url, data=body.encode("utf-8"), method="POST")
    req.add_header("X-Title", title)
    urllib.request.urlopen(req, timeout=5).read()


def find_notify_windows():
    """枚举所有标题为「NotifyHub 通知」的顶层窗口（按 y 排序）。"""
    found = []

    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n <= 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if buf.value == "NotifyHub 通知":
            r = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            found.append((hwnd, r))
        return True

    enum_proc = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)(cb)
    user32.EnumWindows(enum_proc, 0)
    return found


def capture_hwnd(hwnd, rect):
    r = rect
    w, h = r.right - r.left, r.bottom - r.top
    screen_dc = user32.GetDC(None)
    mem_dc = gdi32.CreateCompatibleDC(screen_dc)
    bmp = gdi32.CreateCompatibleBitmap(screen_dc, w, h)
    gdi32.SelectObject(mem_dc, bmp)

    ok = user32.PrintWindow(hwnd, mem_dc, PW_RENDERFULLCONTENT)
    if not ok:
        # 回退：BitBlt 屏幕区域（带 CAPTUREBLT）
        gdi32.BitBlt(mem_dc, 0, 0, w, h, screen_dc, r.left, r.top, 0x00CC0020 | 0x40000000)

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
    bi.biHeight = -h
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
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        row = bytearray(pixels[y * stride:(y + 1) * stride])
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


if __name__ == "__main__":
    prefix = sys.argv[1] if len(sys.argv) > 1 else "tools/reg-card"
    stamp = time.strftime("%H%M%S")
    post("http://127.0.0.1:8099/work", "work-alert", f"[{stamp}] 构建服务器 v0.2.4 发布完成，请查收")
    post("http://127.0.0.1:8099/home", "home-alert", f"[{stamp}] 晚饭好了，记得回来吃饭")

    # 等卡片弹出（最多 3s）
    cards = []
    for _ in range(15):
        time.sleep(0.2)
        cards = find_notify_windows()
        if len(cards) >= 2:
            break

    if not cards:
        print("no notification windows found")
        sys.exit(1)

    for i, (hwnd, rect) in enumerate(sorted(cards, key=lambda c: c[1].top)):
        w, h, px = capture_hwnd(hwnd, rect)
        out = f"{prefix}-{i}.png"
        write_png(out, w, h, px)
        print(f"saved {out} ({w}x{h}) hwnd={hwnd}")
