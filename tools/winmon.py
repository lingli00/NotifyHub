"""Monitor all visible top-level windows for N seconds; log NotifyHub-owned and corner-zone windows."""
import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32

DURATION = float(sys.argv[1]) if len(sys.argv) > 1 else 60
NH_PID = int(sys.argv[2]) if len(sys.argv) > 2 else 0

out = []


@ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
def cb(hwnd, lparam):
    if not user32.IsWindowVisible(hwnd):
        return True
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    txt = ctypes.create_unicode_buffer(256)
    user32.GetWindowTextW(hwnd, txt, 256)
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    if w < 4 or h < 4:
        return True
    is_nh = pid.value == NH_PID
    corner = (r.left > 1400 and r.top < 260) or (r.left > 1400 and r.top > 700)
    if is_nh or corner:
        out.append("%.2f pid=%d vis rect=%d,%d %dx%d title=%s%s"
                   % (time.time() % 1000, pid.value, r.left, r.top, w, h, txt.value,
                      "  <<< NH" if is_nh else ""))
    return True


start = time.time()
seen = set()
while time.time() - start < DURATION:
    out.clear()
    user32.EnumWindows(cb, 0)
    stamp = "%.1f" % (time.time() - start)
    for line in out:
        key = line.split("title=")[1] + line.split("rect=")[1].split(" ")[0]
        sig = (line.split("rect=")[1].split(" ")[0], line.split("title=")[1])
        if sig not in seen:
            seen.add(sig)
            print("t=%s NEW %s" % (stamp, line), flush=True)
    time.sleep(0.25)
print("MONITOR DONE, distinct window signatures:", len(seen))
