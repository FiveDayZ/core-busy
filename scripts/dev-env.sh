#!/usr/bin/env bash
# CORE-BUSY 构建环境：沙箱 bash 缺失 Windows 系统环境变量，需先补齐再调用 dotnet。
# 用法: source scripts/dev-env.sh && dotnet build CORE-BUSY.sln -c Release

export APPDATA="C:\\Users\\Administrator\\AppData\\Roaming"
export ALLUSERSPROFILE="C:\\ProgramData"
export ProgramData="C:\\ProgramData"
export ProgramFiles="C:\\Program Files"
export CommonProgramFiles="C:\\Program Files\\Common Files"
export SystemDrive="C:"
export SystemRoot="C:\\Windows"
export windir="C:\\Windows"
export ComSpec="C:\\Windows\\system32\\cmd.exe"
export NUGET_PACKAGES="C:\\Users\\Administrator\\.workbuddy\\binaries\\nuget-packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_ROOT="C:\\Users\\Administrator\\.workbuddy\\binaries\\dotnet-sdk"
export PATH="/c/Users/Administrator/.workbuddy/binaries/dotnet-sdk:$PATH"
# ProgramFiles(x86) 不是合法 bash 标识符，按需用: env 'ProgramFiles(x86)=C:\Program Files (x86)' dotnet ...
