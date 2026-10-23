"""Build release zips (portable + source) with UTF-8 filenames preserved.

便携包卫生策略：
- 剔除运行期状态（logs/ dump/ cursor.json dedup.json events.json 调试截图）
- config.json 用 dist/default-config.json 出厂模板替换，不把开发机配置打进去

Usage: python tools/pack.py
"""
import os
import shutil
import sys
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "release")
VER = "v0.2.8"

# 便携包内不发布的运行期状态/调试产物
PORTABLE_SKIP_DIRS = {"logs", "dump"}
PORTABLE_SKIP_FILES = {
    "cursor.json", "dedup.json", "events.json",
    "settings-capture.png", "settings-logs.png",
}


def add_tree(zf, src_dir, arc_prefix, skip_dirs=(), skip_files=(), overrides=None):
    """overrides: {相对路径: 磁盘上的替代文件}——打包时替换内容（如出厂 config）。"""
    overrides = overrides or {}
    for dirpath, dirnames, filenames in os.walk(src_dir):
        dirnames[:] = [d for d in dirnames if d not in skip_dirs]
        for name in filenames:
            if name in skip_files:
                continue
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, src_dir)
            arc = os.path.join(arc_prefix, rel).replace("\\", "/")
            if rel.replace("\\", "/") in overrides:
                zf.write(overrides[rel.replace("\\", "/")], arc)
            else:
                zf.write(full, arc)


def build_zip(path, src_root_dir, arc_root, skip_dirs=(), skip_files=(), overrides=None):
    if os.path.exists(path):
        os.remove(path)
    t0 = time.time()
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        add_tree(zf, src_root_dir, arc_root, skip_dirs, skip_files, overrides)
    size = os.path.getsize(path)
    print("%-40s %6.1f MB  (%ds)" % (os.path.basename(path), size / 1048576.0, time.time() - t0))
    return size


def main():
    os.makedirs(OUT, exist_ok=True)

    # ---------- 1) 便携完整包：解压即用 ----------
    portable_dir = os.path.join(ROOT, "dist", "portable", "NotifyHub-" + VER)
    if not os.path.isdir(portable_dir):
        print("missing portable dir: " + portable_dir)
        return 1
    default_cfg = os.path.join(ROOT, "dist", "default-config.json")
    if not os.path.isfile(default_cfg):
        print("missing default config: " + default_cfg)
        return 1
    build_zip(
        os.path.join(OUT, "NotifyHub-%s-portable-win-x64.zip" % VER),
        portable_dir,
        "NotifyHub-" + VER,
        skip_dirs=PORTABLE_SKIP_DIRS,
        skip_files=PORTABLE_SKIP_FILES,
        overrides={"config.json": default_cfg},
    )

    # ---------- 2) 工程源码包 ----------
    stage_root = os.path.join(OUT, "_stage")
    stage = os.path.join(stage_root, "NotifyHub-%s-src" % VER)
    shutil.rmtree(stage_root, ignore_errors=True)
    os.makedirs(stage, exist_ok=True)

    # src：完整源码，排除编译产物
    shutil.copytree(
        os.path.join(ROOT, "src"),
        os.path.join(stage, "src"),
        ignore=shutil.ignore_patterns("bin", "obj", ".vs"),
    )

    # docs：README 里引用的截图
    docs_src = os.path.join(ROOT, "docs")
    if os.path.isdir(docs_src):
        shutil.copytree(docs_src, os.path.join(stage, "docs"))

    # tools：只要脚本，不要历史截图
    os.makedirs(os.path.join(stage, "tools"), exist_ok=True)
    for name in sorted(os.listdir(os.path.join(ROOT, "tools"))):
        full = os.path.join(ROOT, "tools", name)
        if not os.path.isfile(full):
            continue
        if os.path.splitext(name)[1].lower() in (".png", ".jpg", ".gif"):
            continue
        shutil.copy2(full, os.path.join(stage, "tools", name))

    for f in ("README.md", "LICENSE", "CHANGELOG.md", ".gitignore"):
        src = os.path.join(ROOT, f)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(stage, f))

    build_zip(
        os.path.join(OUT, "NotifyHub-%s-source.zip" % VER),
        stage,
        "NotifyHub-" + VER + "-src",
    )
    shutil.rmtree(stage_root, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
