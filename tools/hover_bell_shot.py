"""重开溢出面板 → 悬停蓝铃图标 (1776,1020) → 1.6s 后截屏，看 tooltip 气泡是否出现。"""
import ctypes
import subprocess
import time

user32 = ctypes.windll.user32

# 1. 点折叠箭头展开溢出面板
user32.SetCursorPos(1736, 1060)
user32.mouse_event(0x0002, 0, 0, 0, 0)
user32.mouse_event(0x0004, 0, 0, 0, 0)
time.sleep(1.2)

# 2. 悬停蓝铃图标
user32.SetCursorPos(1776, 1020)
time.sleep(1.6)

# 3. 截屏面板及周边
out = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\reg-hover-bell.png"
subprocess.run([
    "powershell", "-ExecutionPolicy", "Bypass", "-File",
    r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\screenshot-region.ps1",
    "-X", "1620", "-Y", "860", "-W", "340", "-H", "230",
    "-OutFile", out,
], check=True)
print("saved", out)
