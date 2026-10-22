"""截屏灵动岛当前状态（音乐组是否显示）。"""
import subprocess

out = r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\reg-island-now2.png"
subprocess.run([
    "powershell", "-ExecutionPolicy", "Bypass", "-File",
    r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\tools\screenshot-region.ps1",
    "-X", "1100", "-Y", "-10", "-W", "860", "-H", "90",
    "-OutFile", out,
], check=True)
print("saved", out)
