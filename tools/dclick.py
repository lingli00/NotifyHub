"""Double-click at absolute screen coordinates (physical px)."""
import ctypes
import sys
import time

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

x = int(sys.argv[1])
y = int(sys.argv[2])
times = int(sys.argv[3]) if len(sys.argv) > 3 else 1

user32.SetCursorPos(x, y)
time.sleep(0.25)

for _ in range(times):
    for _ in range(2):
        user32.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
        time.sleep(0.02)
        user32.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
        time.sleep(0.05)
    time.sleep(0.35)

print("double-clicked at %d,%d x%d" % (x, y, times))
