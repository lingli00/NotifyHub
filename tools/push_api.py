"""在没有 git 网络（CONNECT 隧道被代理拒绝）时，用 GitHub Git Data API 推送本地提交。

用法：
    GH_TOKEN=xxx python tools/push_api.py <base_sha> <local_ref> [branch]
"""

import base64
import json
import os
import subprocess
import sys
import urllib.request

REPO = "lingli00/NotifyHub"
TOKEN = os.environ.get("GH_TOKEN") or ""
if not TOKEN:
    sys.exit("缺少 GH_TOKEN")

BASE = sys.argv[1]
LOCAL = sys.argv[2] if len(sys.argv) > 2 else "HEAD"
BRANCH = sys.argv[3] if len(sys.argv) > 3 else "main"

# 远端 base（本地可能没有这个对象：上一次是走 API 推的，本地 git 里没有它）
API_BASE = os.environ.get("API_BASE") or BASE

API = "https://api.github.com"


def api(path, method="GET", data=None):
    body = json.dumps(data).encode("utf-8") if data is not None else None
    req = urllib.request.Request(API + path, data=body, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Accept", "application/vnd.github+json")
    if body is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            return json.load(resp)
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", "replace")[:900]
        raise SystemExit(f"API {method} {path} -> {exc.code}\n{detail}")


def git(*args):
    return subprocess.run(
        ["git"] + list(args), capture_output=True, text=True, check=True
    ).stdout


def main():
    head = git("rev-parse", LOCAL).strip()
    base_tree = api(f"/repos/{REPO}/git/commits/{API_BASE}")["tree"]["sha"]
    print(f"base {API_BASE[:8]} tree {base_tree[:8]} -> head {head[:8]}（diff 自 {BASE[:8]}）")

    raw = git("diff", "--name-status", BASE, head)
    changes = [line.split("\t") for line in raw.splitlines() if line.strip()]
    print(f"变更 {len(changes)} 个文件")

    entries = []
    for item in changes:
        status, path = item[0], item[-1]
        if status.startswith("D"):
            entries.append(
                {"path": path, "mode": "100644", "type": "blob", "sha": None}
            )
            print(f"  D {path}")
            continue

        mode = git("ls-tree", head, "--", path).split()[0] or "100644"
        data = open(path, "rb").read()
        blob = api(
            f"/repos/{REPO}/git/blobs",
            "POST",
            {
                "content": base64.b64encode(data).decode("ascii"),
                "encoding": "base64",
            },
        )
        entries.append(
            {"path": path, "mode": mode, "type": "blob", "sha": blob["sha"]}
        )
        print(f"  {status} {path}  {len(data)}B -> {blob['sha'][:8]}")

    tree = api(
        f"/repos/{REPO}/git/trees", "POST", {"base_tree": base_tree, "tree": entries}
    )
    print(f"tree {tree['sha'][:8]}")

    message = "\n\n".join(
        block.strip()
        for block in git("log", "--format=%B", f"{BASE}..{head}").split("\n\n\n")
        if block.strip()
    )
    message = message.replace("\r\n", "\n").strip() + "\n"

    author = {
        "name": "lingli00",
        "email": "63622499+lingli00@users.noreply.github.com",
        "date": subprocess.run(
            ["date", "-u", "+%Y-%m-%dT%H:%M:%SZ"],
            capture_output=True,
            text=True,
            check=True,
        ).stdout.strip(),
    }

    commit = api(
        f"/repos/{REPO}/git/commits",
        "POST",
        {
            "message": message,
            "tree": tree["sha"],
            "parents": [API_BASE],
            "author": author,
            "committer": author,
        },
    )
    print(f"commit {commit['sha'][:8]}")

    api(
        f"/repos/{REPO}/git/refs/heads/{BRANCH}",
        "PATCH",
        {"sha": commit["sha"], "force": False},
    )
    print(f"已更新 {BRANCH} -> {commit['sha']}")


if __name__ == "__main__":
    main()
