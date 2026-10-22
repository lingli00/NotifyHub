"""验证 --island-stream 的状态更新延迟。

启动一个常驻 stream 客户端，连续推送 N 行状态，测量每次写入的实际开销。
进程只启动一次，所以后续每行的耗时就是"命令到达常驻实例"的净成本。
"""

import subprocess
import sys
import time

EXE = (r"C:\Users\Administrator\WorkBuddy\通知\NotifyHub\src\NotifyHub\bin\Release"
       r"\net8.0-windows10.0.19041.0\win-x64\NotifyHub.exe")

STEPS = [
    "正在读取需求文档…",
    "已解析 8 个章节，开始拆分任务",
    "任务 1/6：重构灵动岛动效",
    "任务 2/6：修复拖拽链路",
    "任务 3/6：四边吸附与自动滑出",
    "任务 4/6：状态通道改长连接",
    "任务 5/6：控制台并入设置页",
    "任务 6/6：收紧阴影留白",
    "两份包已就绪。开始上传 GitHub，先校验令牌。",
    "令牌有效，正在推送源码…",
]


def main():
    proc = subprocess.Popen(
        [EXE, "--island-stream"],
        stdin=subprocess.PIPE,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        text=True,
        encoding="utf-8",
    )

    t0 = time.perf_counter()
    costs = []

    for text in STEPS:
        start = time.perf_counter()
        proc.stdin.write("RUN|" + text + "\n")
        proc.stdin.flush()
        costs.append((time.perf_counter() - start) * 1000)
        time.sleep(0.45)

    proc.stdin.write("DONE|完成\n")
    proc.stdin.flush()
    time.sleep(0.8)

    proc.stdin.close()
    proc.wait(timeout=10)

    total = (time.perf_counter() - t0) * 1000
    body = costs[1:] if len(costs) > 1 else costs

    print(f"lines={len(STEPS)} first(incl. process+pipe)={costs[0]:.1f}ms")
    print(f"per-line avg(2..n)={sum(body) / len(body):.2f}ms max={max(body):.2f}ms")
    print(f"total={total:.0f}ms")

    worst = max(body)
    print("LATENCY " + ("OK (<100ms)" if worst < 100 else "FAIL"))
    return 0 if worst < 100 else 1


if __name__ == "__main__":
    sys.exit(main())
