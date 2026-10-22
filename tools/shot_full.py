"""全屏截屏（通知卡验证用，复用 screenshot2.ps1 的 CAPTUREBLT 抓取）。"""
import subprocess

out = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\reg-ntfy-cards.png"
subprocess.run([
    "powershell", "-ExecutionPolicy", "Bypass", "-File",
    r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\screenshot2.ps1",
    "-OutFile", out,
    "-CropW", "1920", "-CropH", "1080",
], check=True)
print("saved", out)
