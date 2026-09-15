#!/usr/bin/env python
"""CORE-BUSY Release 构建 + 单文件发布 + 打包（无需可用的 bash）。

为什么需要这个脚本：沙箱 bash 缺失 Windows 系统环境变量（SystemRoot/ProgramData/
ProgramFiles/APPDATA 等），dotnet 会报 `Value cannot be null (path1)`；本机 bash 的
coreutils 也时常不可用。本脚本直接以正确的环境字典拉起 dotnet.exe，行为与
`source scripts/dev-env.sh && dotnet ...` 等价，且不依赖 shell。

发布约定（v1.14.0 起为双通道，见 .workbuddy/memory/2026-09-13.md；
v1.19.0 起完整版关闭单文件压缩，见 .workbuddy/memory/2026-09-14.md「内存占用结案」）：
    dist/_publish/CoreBusy.App.exe            完整版发布输出（自包含单文件，含 pdb）
    dist/_publish-lite/CoreBusy.App.exe       精简版发布输出（依赖系统运行时）
    dist/CoreBusy.App.exe                     完整版单文件 exe 副本，双击即用
    dist/CoreBusy-v{版本}-win-x64.zip          完整版压缩包（零依赖）
    dist/CoreBusy-v{版本}-win-x64-lite.zip     精简版压缩包（体积/内存都小）

为什么两个都出：完整版零依赖但 exe 较大（不压缩约 159 MB），精简版 exe 仅个位数 MB
但要系统装一次 .NET 8 桌面运行时；两者的**运行内存已经一致**（都不压缩）。
曾经的"压缩换体积"实测多吃约 96 MB 工作集 / 68 MB 提交 —— 对常驻托盘的工具不划算，
故 v1.19.0 起完整版不再压缩（详见 FULL_PUBLISH_ARGS 上方的实测表）。

用法:
    python scripts/build-release.py                       # 两个通道都构建 + 打包
    python scripts/build-release.py --variant lite        # 只出精简版
    python scripts/build-release.py --no-publish          # 只构建，验证编译
    python scripts/build-release.py --no-zip              # 不打包
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SLN = os.path.join(REPO, "CORE-BUSY.sln")
APP_PROJ = os.path.join(REPO, "src", "CoreBusy.App", "CoreBusy.App.csproj")
APP_CSPROJ = APP_PROJ
DIST = os.path.join(REPO, "dist")
PUBLISH = os.path.join(DIST, "_publish")
LITE_PUBLISH = os.path.join(DIST, "_publish-lite")

# ===== 两个发布通道（v1.14.0；v1.19.0 起完整版关闭压缩）=====
#
# 完整版**必须**关闭单文件压缩：压缩会把全部托管程序集在启动时解压进内存。
# 实测（.workbuddy/measure_footprint.py，同机、同 v1.18.0 源码、隔离数据目录、采样 50s）：
#     单文件 + 压缩 ： 文件  70 MB   稳定工作集 252 MB   稳定提交 132 MB（峰值 144 MB）
#     松散未压缩    ： 文件夹 174 MB  稳定工作集 156 MB   稳定提交  70 MB（峰值  85 MB）
#     单文件 不压缩 ： 文件 159 MB   稳定工作集 153 MB   稳定提交  64 MB（峰值  87 MB）
# 即压缩用 **约 96 MB 工作集 / 68 MB 提交** 换体积从 159 MB 缩到 70 MB。
# 对一个常驻托盘、只盯 CPU 的小工具，运行内存远比下载体积要紧 —— 故完整版放弃压缩；
# 仍保留单文件（不压缩的单文件与松散目录内存相同，却免去"一堆 DLL 里找 exe"）。
#
# 完整版：零依赖，双击即用；拿体积换运行内存。
FULL_PUBLISH_ARGS = [
    "-r", "win-x64",
    "--self-contained",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=false",
]

# 精简版：依赖系统 .NET 8 桌面运行时（一次性安装，所有 .NET 应用共用）。
#   · --no-self-contained  不打包运行时 → 体积降到个位数 MB
#   · 压缩显式关闭         压缩是内存大头，不能开（同完整版理由）
#   · 仍用单文件           免去「一堆 DLL 里找 exe」；未压缩的单文件是内存映射加载，
#                          不触发解压进内存，故不重蹈压缩版的覆辙（已实测复核）
#   · DebugType=none       不带 pdb，打包更干净
LITE_PUBLISH_ARGS = [
    "-r", "win-x64",
    "--no-self-contained",
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=false",
    "-p:DebugType=none",
]
BINARIES = os.path.join(os.path.expanduser("~"), ".workbuddy", "binaries")
DOTNET_EXE = os.path.join(BINARIES, "dotnet-sdk", "dotnet.exe")
NUGET_PACKAGES = os.path.join(BINARIES, "nuget-packages")

# 构建日志写项目根的 build-log.txt（沿用既有约定），UTF-8，不依赖 shell 重定向。
LOG_PATH = os.path.join(REPO, "build-log.txt")
LOG_HANDLE = None


def log(msg: str) -> None:
    print(msg, flush=True)
    if LOG_HANDLE is not None:
        try:
            LOG_HANDLE.write(msg + "\n")
            LOG_HANDLE.flush()
        except Exception:  # noqa: BLE001
            pass


def _windows_defaults() -> dict:
    """沙箱 bash 里缺失、但 dotnet/MSBuild 必需的系统变量（值按当前用户推导）。

    早期版本把它们**硬编码**成 `C:\\Users\\Administrator\\...`（当时的机器用户）。换用户 /
    换机器后 TEMP 指向一个不存在的目录，构建会当场失败，且失败信息与真实原因毫不相干。
    现在一律从 `~` 推导，并配合 setdefault 使用：**只补缺失项，真实值优先**。
    """
    home = os.path.expanduser("~")
    drive, tail = os.path.splitdrive(home)
    local = os.path.join(home, "AppData", "Local")
    return {
        "USERPROFILE": home,
        "HOMEDRIVE": drive,
        "HOMEPATH": tail,
        "APPDATA": os.path.join(home, "AppData", "Roaming"),
        "LOCALAPPDATA": local,
        "TEMP": os.path.join(local, "Temp"),
        "TMP": os.path.join(local, "Temp"),
        "ALLUSERSPROFILE": r"C:\ProgramData",
        "ProgramData": r"C:\ProgramData",
        "ProgramFiles": r"C:\Program Files",
        "CommonProgramFiles": r"C:\Program Files\Common Files",
        "ProgramFiles(x86)": r"C:\Program Files (x86)",
        "CommonProgramFiles(x86)": r"C:\Program Files (x86)\Common Files",
        "SystemDrive": "C:",
        "SystemRoot": r"C:\Windows",
        "windir": r"C:\Windows",
        "ComSpec": r"C:\Windows\system32\cmd.exe",
        "PATHEXT": ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.WS;.MSC",
    }


def build_env() -> dict:
    """镜像 scripts/dev-env.sh：补齐沙箱缺失的 Windows 环境变量（不覆盖已有真实值）。"""
    env = dict(os.environ)
    for key, value in _windows_defaults().items():
        env.setdefault(key, value)

    env.update(
        {
            # 这三项是**刻意的构建约定**，必须覆盖环境里的任何值。
            "NUGET_PACKAGES": NUGET_PACKAGES,
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
            "DOTNET_ROOT": os.path.dirname(DOTNET_EXE),
            "PATH": os.path.dirname(DOTNET_EXE)
            + r";C:\Windows\System32;C:\Windows;C:\Windows\System32\Wbem;"
            + env.get("PATH", ""),
        }
    )
    return env


def read_version() -> str:
    with open(APP_CSPROJ, encoding="utf-8-sig") as fh:
        m = re.search(r"<Version>([^<]+)</Version>", fh.read())
    if not m:
        raise SystemExit("无法从 %s 读取 <Version>" % APP_CSPROJ)
    return m.group(1).strip()


def run(step: str, args: list[str], env: dict) -> None:
    log("")
    log("=" * 66)
    log("[%s] %s" % (step, " ".join(os.path.basename(a) if a.endswith(".exe") else a for a in args)))
    log("=" * 66)
    started = time.time()
    # 流式读取，既实时输出也落盘，且不依赖 shell 的编码与重定向行为。
    proc = subprocess.Popen(
        [DOTNET_EXE, *args],
        env=env,
        cwd=REPO,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    assert proc.stdout is not None
    for line in proc.stdout:
        log(line.rstrip("\r\n"))
    proc.wait()
    elapsed = time.time() - started
    if proc.returncode != 0:
        log("[失败] %s 退出码 = %d（用时 %.1f 秒）" % (step, proc.returncode, elapsed))
        sys.exit(proc.returncode)
    log("[完成] %s（用时 %.1f 秒）" % (step, elapsed))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--no-publish", action="store_true", help="只构建，不发布")
    parser.add_argument("--no-zip", action="store_true", help="不生成 zip")
    parser.add_argument("--no-build", action="store_true", help="跳过 build（publish 已隐含构建）")
    parser.add_argument("--package-only", action="store_true",
                        help="跳过 build/publish，仅用 dist/_publish 现有产物复制与打包")
    parser.add_argument("--incremental-publish", action="store_true",
                        help="不清空 dist/_publish，直接 publish 覆盖（单文件发布只有 exe+pdb，"
                             "残留风险极低；用于环境禁止批量删除时）")
    parser.add_argument("--variant", choices=("full", "lite", "both"), default="both",
                        help="发布通道：full=自包含零依赖（默认也产出）；lite=依赖系统运行时（体积"
                             "与内存都小）；both=两个都出（默认）")
    args = parser.parse_args()

    if args.package_only:
        args.no_build = True
        args.no_publish = True

    global LOG_HANDLE
    try:
        LOG_HANDLE = open(LOG_PATH, "w", encoding="utf-8")
    except Exception:  # noqa: BLE001
        LOG_HANDLE = None

    if not os.path.isfile(DOTNET_EXE):
        log("[失败] 未找到 SDK: %s" % DOTNET_EXE)
        log("       请先执行: python scripts/setup-dotnet-sdk.py")
        return 1

    env = build_env()
    version = read_version()
    log("dotnet SDK : %s" % DOTNET_EXE)
    log("项目版本   : %s" % version)
    log("NuGet 目录 : %s" % NUGET_PACKAGES)

    if not args.no_build:
        run("build", ["build", SLN, "-c", "Release", "--nologo"], env)

    variants = ["full", "lite"] if args.variant == "both" else [args.variant]

    if not args.no_publish:
        for variant in variants:
            out_dir = PUBLISH if variant == "full" else LITE_PUBLISH
            if os.path.isdir(out_dir) and not args.incremental_publish:
                # 清空旧输出，避免 publish 残留上一版的孤儿文件。
                # 注：某些环境会对「一次删 50+ 文件」触发安全确认并中断脚本，
                #     此时改用 --incremental-publish 让 dotnet publish 就地覆盖。
                shutil.rmtree(out_dir)
            elif os.path.isdir(out_dir):
                log("[提示] 增量发布：保留 %s 现有文件，由 publish 就地覆盖" % out_dir)
            run(
                "publish(%s)" % variant,
                [
                    "publish",
                    APP_PROJ,
                    "-c", "Release",
                    *(FULL_PUBLISH_ARGS if variant == "full" else LITE_PUBLISH_ARGS),
                    "-o", out_dir,
                    "--nologo",
                ],
                env,
            )

    # 复制 + 打包：只要发布输出存在就执行，与 --no-publish/--package-only 解耦，
    # 这样「dist 被运行中实例锁住」时还能单独重打包，不必重建。
    os.makedirs(DIST, exist_ok=True)

    artifacts: list[tuple[str, str, str]] = []
    for variant in variants:
        out_dir = PUBLISH if variant == "full" else LITE_PUBLISH
        exe = os.path.join(out_dir, "CoreBusy.App.exe")
        if not os.path.isfile(exe):
            log("[失败] %s 通道缺少 CoreBusy.App.exe: %s" % (variant, out_dir))
            return 1
        zip_name = ("CoreBusy-v%s-win-x64.zip" % version) if variant == "full" \
            else ("CoreBusy-v%s-win-x64-lite.zip" % version)
        artifacts.append((variant, exe, zip_name))

    # 完整版额外镜像到 dist 根目录，作为「双击即用、零依赖」的那一份。
    # 运行中的实例会锁住 exe（WinError 32），此时不应整条流水线崩掉：
    # 压缩包一律从发布输出取，镜像复制失败只作降级提示。
    copied = True
    full_exe = next((e for v, e, _z in artifacts if v == "full"), None)
    if full_exe is not None:
        target_exe = os.path.join(DIST, "CoreBusy.App.exe")
        try:
            shutil.copy2(full_exe, target_exe)
            log("[复制] %s  (%.1f MB)" % (target_exe, os.path.getsize(target_exe) / 1048576))
        except PermissionError:
            copied = False
            log("")
            log("[占用] 无法覆盖 %s" % target_exe)
            log("       该文件正被运行中的实例锁定，本次未更新 dist 镜像。")
            log("       关闭程序后重跑本脚本，或手动执行：")
            log('       copy "%s" "%s"' % (full_exe, target_exe))

    if not args.no_zip:
        for _variant, exe, zip_name in artifacts:
            zip_path = os.path.join(DIST, zip_name)
            # 不再先 os.remove(zip_path)：ZipFile 以 "w" 打开即截断重建。
            # 某些环境对文件删除设有配额，配额耗尽会让流水线在最后一步半途中断。
            with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
                zf.write(exe, "CoreBusy.App.exe")
            log("[打包] %s  (%.1f MB)" % (zip_path, os.path.getsize(zip_path) / 1048576))

    log("")
    log("-" * 66)
    log("产物一览（版本 %s）" % version)
    log("-" * 66)
    for variant, exe, zip_name in artifacts:
        zip_path = os.path.join(DIST, zip_name)
        zip_size = os.path.getsize(zip_path) / 1048576 if os.path.isfile(zip_path) else -1
        note = "零依赖" if variant == "full" else "需系统 .NET 8 桌面运行时"
        log("  %-5s exe %6.1f MB   zip %6.1f MB   (%s)"
            % (variant, os.path.getsize(exe) / 1048576, zip_size, note))

    if not copied:
        log("")
        log("=" * 66)
        log("构建完成但 dist 镜像未更新（exe 被占用）：版本 %s" % version)
        log("=" * 66)
        return 2

    log("")
    log("=" * 66)
    log("构建流水线完成：版本 %s" % version)
    log("=" * 66)
    return 0


if __name__ == "__main__":
    sys.exit(main())
