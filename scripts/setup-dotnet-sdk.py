#!/usr/bin/env python
"""按项目约定配置 .NET SDK 构建环境。

背景：CORE-BUSY 的 SDK 是"zip 解压、非全局安装"在
`~/.workbuddy/binaries/dotnet-sdk`，该目录历史上会被环境清理掉，
导致 `dotnet` 不在 PATH、无法编译。本脚本可重复执行以快速恢复。

约定（见 .workbuddy/memory/2026-09-10.md）：
    SDK 版本 8.0.425（= 8.0 通道最新，运行时 8.0.31）
    安装目录 C:\\Users\\Administrator\\.workbuddy\\binaries\\dotnet-sdk

用法:
    python scripts/setup-dotnet-sdk.py            # 缺失才装
    python scripts/setup-dotnet-sdk.py --force    # 强制重装
    python scripts/setup-dotnet-sdk.py --version 8.0.424
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile

BASENAME = os.path.join(os.path.expanduser("~"), ".workbuddy", "binaries")
INSTALL_DIR = os.path.join(BASENAME, "dotnet-sdk")
STAGING_DIR = os.path.join(BASENAME, "dotnet-sdk.staging")
DOWNLOAD_DIR = os.path.join(BASENAME, "_cache")
URL_TEMPLATE = "https://builds.dotnet.microsoft.com/dotnet/Sdk/{0}/dotnet-sdk-{0}-win-x64.zip"
DEFAULT_VERSION = "8.0.425"


def log(msg: str) -> None:
    print(msg, flush=True)


def probe_version(dotnet_exe: str) -> str | None:
    if not os.path.isfile(dotnet_exe):
        return None
    try:
        out = subprocess.run(
            [dotnet_exe, "--version"],
            capture_output=True,
            text=True,
            timeout=120,
        )
        if out.returncode == 0:
            return out.stdout.strip()
        return None
    except Exception:  # noqa: BLE001
        return None


def download(url: str, dest: str, attempts: int = 5) -> None:
    """带重试与进度输出的下载（约 272 MB）。"""
    for attempt in range(1, attempts + 1):
        try:
            log("[下载] 第 %d/%d 次尝试: %s" % (attempt, attempts, url))
            started = time.time()
            req = urllib.request.Request(url, headers={"User-Agent": "curl/8"})
            with urllib.request.urlopen(req, timeout=60) as resp, open(dest, "wb") as fh:
                total = int(resp.headers.get("Content-Length") or 0)
                done = 0
                last_report = 0
                while True:
                    chunk = resp.read(1024 * 1024)
                    if not chunk:
                        break
                    fh.write(chunk)
                    done += len(chunk)
                    pct = (done * 100 // total) if total else 0
                    if pct >= last_report + 10:
                        last_report = pct
                        log("        %d%%  (%.1f MB / %.1f MB)"
                            % (pct, done / 1048576, total / 1048576))
            log("[下载] 完成，用时 %.1f 秒，文件 %.1f MB"
                % (time.time() - started, os.path.getsize(dest) / 1048576))
            return
        except Exception as exc:  # noqa: BLE001
            log("[下载] 失败: %r" % exc)
            if attempt == attempts:
                raise
            wait = 5 * attempt
            log("[下载] %d 秒后重试…" % wait)
            time.sleep(wait)


def extract(zip_path: str, target: str) -> None:
    if os.path.isdir(target):
        shutil.rmtree(target)
    os.makedirs(target, exist_ok=True)
    log("[解压] %s -> %s" % (os.path.basename(zip_path), target))
    with zipfile.ZipFile(zip_path) as zf:
        names = zf.namelist()
        total = len(names)
        for i, name in enumerate(names, 1):
            zf.extract(name, target)
            if i % 2000 == 0 or i == total:
                log("        %d/%d" % (i, total))
    log("[解压] 完成，%d 个条目" % total)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default=DEFAULT_VERSION)
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()

    dotnet_exe = os.path.join(INSTALL_DIR, "dotnet.exe")

    if not args.force:
        current = probe_version(dotnet_exe)
        if current:
            log("[跳过] SDK 已就绪: %s (%s)" % (current, dotnet_exe))
            return 0
        log("[检查] 现有 SDK 不可用或缺失，开始安装")

    os.makedirs(DOWNLOAD_DIR, exist_ok=True)
    zip_path = os.path.join(DOWNLOAD_DIR, "dotnet-sdk-%s-win-x64.zip" % args.version)

    if os.path.isfile(zip_path) and os.path.getsize(zip_path) > 200 * 1048576:
        log("[缓存] 复用已下载的 %s (%.1f MB)"
            % (os.path.basename(zip_path), os.path.getsize(zip_path) / 1048576))
    else:
        download(URL_TEMPLATE.format(args.version), zip_path)

    extract(zip_path, STAGING_DIR)

    if os.path.isdir(INSTALL_DIR):
        shutil.rmtree(INSTALL_DIR)
    os.rename(STAGING_DIR, INSTALL_DIR)

    version = probe_version(dotnet_exe)
    if not version:
        log("[失败] 解压后 dotnet.exe 仍无法执行: %s" % dotnet_exe)
        return 1

    log("[完成] dotnet %s -> %s" % (version, dotnet_exe))
    return 0


if __name__ == "__main__":
    sys.exit(main())
