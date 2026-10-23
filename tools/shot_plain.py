"""整屏抓图（只抓屏，不触发任何通知/请求）。

用法: python tools/shot_plain.py [out.png]

与 fastshot.py 的区别：fastshot 会先 POST 两条 ntfy 消息造通知卡，
这里只做「DPI 感知 + CAPTUREBLT」的纯抓屏，用于核对常驻实例的真实观感
（灵动岛位置、细条状态、通知卡排版等）。
"""
import sys

from fastshot import capture_full, write_png

out = sys.argv[1] if len(sys.argv) > 1 else "tools/shot.png"
w, h, px = capture_full()
write_png(out, w, h, px)
print("saved %s (%dx%d)" % (out, w, h))
