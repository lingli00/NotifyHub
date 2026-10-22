"""创建 GitHub Release 并上传资产（走 API，绕开 git 的 CONNECT 限制）。

用法：GH_TOKEN=xxx python tools/release_api.py <tag> <name> <notes_file> <file1> [file2]
"""

import json
import os
import subprocess
import sys
import urllib.request

REPO = "lingli00/NotifyHub"
TOKEN = os.environ.get("GH_TOKEN") or ""
if not TOKEN:
    sys.exit("缺少 GH_TOKEN")

TAG = sys.argv[1]
NAME = sys.argv[2]
NOTES_FILE = sys.argv[3]
FILES = sys.argv[4:]

API = "https://api.github.com"


def api(path, method="GET", data=None):
    body = json.dumps(data).encode("utf-8") if data is not None else None
    req = urllib.request.Request(API + path, data=body, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Accept", "application/vnd.github+json")
    if body is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=300) as resp:
        return json.load(resp)


def upload(url, path):
    """用 curl 上传（-F 需要流式），带进度输出。"""
    name = os.path.basename(path)
    cmd = [
        "curl",
        "-sS",
        "--fail-with-body",
        "-X",
        "POST",
        "-H",
        f"Authorization: Bearer {TOKEN}",
        "-H",
        "Accept: application/vnd.github+json",
        "-H",
        "Content-Type: application/zip",
        "--data-binary",
        f"@{path}",
        f"{url}?name={urllib.parse.quote(name)}",
    ]
    out = subprocess.run(cmd, capture_output=True, text=True)
    if out.returncode != 0:
        return None, (out.stdout or out.stderr)[:600]
    return json.loads(out.stdout), None


def main():
    notes = open(NOTES_FILE, encoding="utf-8").read()

    try:
        rel = api(f"/repos/{REPO}/releases/tags/{TAG}")
        print(f"Release {TAG} 已存在 id={rel['id']}")
    except urllib.error.HTTPError as exc:
        if exc.code != 404:
            raise
        rel = api(
            f"/repos/{REPO}/releases",
            "POST",
            {
                "tag_name": TAG,
                "target_commitish": "main",
                "name": NAME,
                "body": notes,
                "draft": False,
                "prerelease": False,
            },
        )
        print(f"已创建 Release {TAG} id={rel['id']}")

    existing = {a["name"]: a["id"] for a in rel.get("assets", [])}
    for f in FILES:
        name = os.path.basename(f)
        if name in existing:
            api(f"/repos/{REPO}/releases/assets/{existing[name]}", "DELETE")
            print(f"删除旧资产 {name}")

        size = os.path.getsize(f)
        print(f"上传 {name} ({size / 1048576:.1f} MB) ...", flush=True)
        asset, err = upload(
            f"https://uploads.github.com/repos/{REPO}/releases/{rel['id']}/assets", f
        )
        if err:
            sys.exit(f"上传失败：{err}")
        print(f"  完成 state={asset['state']} size={asset['size']}")

    print(f"Release 页面：{rel['html_url']}")


if __name__ == "__main__":
    main()
