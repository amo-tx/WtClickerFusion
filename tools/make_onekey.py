# -*- coding: utf-8 -*-
"""生成「傻瓜式一键运行包」：下载 -> 解压 -> 双击 RUN.bat，无需装 .NET / 官方客户端。

从 dist/ 挑选最小可运行集：
  WtClickerFusion.exe   自包含单文件（内嵌打了精确状态桥补丁的桌宠 payload）
  *.dll                 .NET 8 单文件发布时外置的 WPF 原生库，缺一不可
  config.ini            默认配置（自动开始、宝箱秒领、真实键鼠同步）
  characters/           5 个自定义角色
再补上 RUN.bat / 一键运行.bat / 设置开机自启.bat / 取消开机自启.bat / 使用说明.txt。

排除：WtClickerFusion.pdb、WtClicker.log、ctr_fail_*.bin（调试与诊断产物）。

用法：python tools/make_onekey.py
输出：tools/_onekey/WtClickerFusion-OneClick-v1.0.0.zip
"""

from pathlib import Path
import hashlib
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "dist"
OUT = ROOT / "tools" / "_onekey"
BUNDLE = OUT / "WtClickerFusion-OneClick-v1.0.0"
ZIP = OUT / "WtClickerFusion-OneClick-v1.0.0.zip"

# ---------------------------------------------------------------- 启动脚本
# cmd 默认代码页 936：bat 必须以 GBK + CRLF 写入，否则中文全乱码。
MAIN_BAT = r'''@echo off
chcp 936 >nul
title WtClickerFusion 一键运行
cd /d "%~dp0"

echo ==========================================================
echo    WtClickerFusion  一键运行
echo ==========================================================
echo.

rem 桌宠是高完整性进程，普通权限下 PostMessage 会被 UIPI 静默拦截。
rem 带 --elevated 的是提权后的第二次执行，直接放行，避免权限检测误判造成无限弹窗。
if /I "%~1"=="--elevated" goto :run
fltmc >nul 2>&1
if %errorlevel% EQU 0 goto :run
echo [1/2] 需要管理员权限，请在弹出的 UAC 窗口点「是」...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '--elevated' -Verb RunAs"
exit /b

:run
if not exist "WtClickerFusion.exe" (
  echo [错误] 同目录下没有 WtClickerFusion.exe，请完整解压压缩包后再运行。
  pause
  exit /b 1
)

rem 单实例保护：重复启动会抢占 48643 端口，导致精确状态桥回退 OCR 模式。
tasklist /FI "IMAGENAME eq WtClickerFusion.exe" 2>nul | find /I "WtClickerFusion.exe" >nul
if %errorlevel% EQU 0 (
  echo [提示] 程序已经在运行，无需重复启动。
  echo        可在系统托盘找到图标，监控页 http://127.0.0.1:48643/
  echo.
  choice /C YN /N /T 8 /D N /M "仍要再开一个实例吗？ Y / N "
  if errorlevel 2 exit /b
)

if not exist "%ProgramFiles(x86)%\Microsoft\EdgeWebView\Application" (
  echo [提醒] 未检测到 WebView2 运行库；若桌宠没出现，请安装：
  echo        https://developer.microsoft.com/microsoft-edge/webview2/
  echo.
)

echo [2/2] 正在启动：自动开始连点 + 最小化到托盘。
echo        首次启动需解压内嵌组件，约 10-30 秒，请耐心等待。
start "" "WtClickerFusion.exe" --start --min
echo.
echo   已启动。窗口最小化在托盘，点托盘图标可打开。
echo   可视化监控页： http://127.0.0.1:48643/
echo   原始状态数据： http://127.0.0.1:48643/stats
echo   开机自动启动： 双击「设置开机自启.bat」，可选
echo   详细说明：     使用说明.txt
echo.
timeout /t 8 >nul
'''

ENABLE_BAT = r'''@echo off
chcp 936 >nul
title 设置开机自启
cd /d "%~dp0"

if /I not "%~1"=="--elevated" (
  fltmc >nul 2>&1
  if %errorlevel% NEQ 0 (
    echo 需要管理员权限，请在 UAC 窗口点「是」...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '--elevated' -Verb RunAs"
    exit /b
  )
)

set "EXE=%~dp0WtClickerFusion.exe"
if not exist "%EXE%" (
  echo [错误] 没找到 WtClickerFusion.exe
  pause
  exit /b 1
)

rem /rl highest 让登录时以最高权限运行，避免每次开机弹 UAC；/it 保证是交互式会话。
schtasks /create /f /tn "WtClickerFusion" /sc onlogon /rl highest /it /tr "\"%EXE%\" --start --min"
if %errorlevel% EQU 0 (
  echo.
  echo [完成] 已设置开机自启：登录后自动以管理员权限启动并立即开始连点。
  echo        想取消，请双击「取消开机自启.bat」。
) else (
  echo [失败] 计划任务创建失败，请右键本文件选择「以管理员身份运行」后重试。
)
echo.
pause
'''

DISABLE_BAT = r'''@echo off
chcp 936 >nul
title 取消开机自启
if /I not "%~1"=="--elevated" (
  fltmc >nul 2>&1
  if %errorlevel% NEQ 0 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '--elevated' -Verb RunAs"
    exit /b
  )
)
schtasks /delete /f /tn "WtClickerFusion"
if %errorlevel% EQU 0 ( echo [完成] 已取消开机自启。 ) else ( echo [提示] 未找到该计划任务，或权限不足。 )
echo.
pause
'''


def gbk(path: Path, text: str):
    path.write_bytes(text.replace("\n", "\r\n").encode("gbk"))


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if not (SRC / "WtClickerFusion.exe").exists():
        raise SystemExit("找不到 dist/WtClickerFusion.exe，请先 dotnet publish")

    # 1) 清空并重建目录
    if BUNDLE.exists():
        shutil.rmtree(BUNDLE)
    BUNDLE.mkdir(parents=True)

    copied = []
    exe = SRC / "WtClickerFusion.exe"
    shutil.copy2(exe, BUNDLE / exe.name)
    copied.append(BUNDLE / exe.name)
    for dll in sorted(SRC.glob("*.dll")):
        shutil.copy2(dll, BUNDLE / dll.name)
        copied.append(BUNDLE / dll.name)
    shutil.copy2(SRC / "config.ini", BUNDLE / "config.ini")
    copied.append(BUNDLE / "config.ini")
    shutil.copytree(SRC / "characters", BUNDLE / "characters")
    for f in sorted((BUNDLE / "characters").rglob("*")):
        if f.is_file():
            copied.append(f)

    print("=== 复制运行文件 ===")
    n_char = len([p for p in (BUNDLE / "characters").rglob("*") if p.is_file()])
    print("  根目录 %d 项，characters/ %d 个文件" %
          (len([p for p in copied if p.parent == BUNDLE]), n_char))
    for p in copied:
        if p.parent == BUNDLE:
            print("  %9.2f MB  %s" % (p.stat().st_size / 1048576, p.name))

    exe_hash = sha256(BUNDLE / "WtClickerFusion.exe")

    # 2) 启动脚本（中文名 + ASCII 别名，兼容老解压软件中文名乱码）
    gbk(BUNDLE / "一键运行.bat", MAIN_BAT)
    gbk(BUNDLE / "RUN.bat", MAIN_BAT)
    gbk(BUNDLE / "设置开机自启.bat", ENABLE_BAT)
    gbk(BUNDLE / "取消开机自启.bat", DISABLE_BAT)

    readme = f'''WtClickerFusion 一键运行包  v1.0.0
==========================================================

【怎么用】
  1. 完整解压本压缩包到任意文件夹（不要只解压单个文件）。
  2. 双击「一键运行.bat」；若中文文件名显示乱码，双击「RUN.bat」。
  3. 弹出 UAC 时点「是」。程序必须管理员权限，否则 Windows 会静默拦截点击注入。
  4. 完成。程序会自动开始连点并最小化到系统托盘，点托盘图标可打开窗口。

  首次启动会解压内嵌的桌宠组件，约需 10-30 秒，请耐心等待。
  可视化监控页：  http://127.0.0.1:48643/
  原始状态数据：  http://127.0.0.1:48643/stats

【不需要你安装什么】
  - 不需要安装 .NET 8：exe 是自包含单文件。
  - 不需要安装官方桌宠客户端：已内嵌打了精确状态桥补丁的版本，
    运行时释放到 %LOCALAPPDATA%，不会改动你已安装的官方程序。
  - Windows 10/11 64 位即可；绝大多数机器已自带 WebView2 运行库与 VC++ 运行库。
    若桌宠窗口始终不出现，请安装：
      WebView2 Runtime  https://developer.microsoft.com/microsoft-edge/webview2/
      VC++ 2015-2022 x64  https://aka.ms/vc_redist.x64.exe

【可选：开机自启】
  双击「设置开机自启.bat」注册登录时自动启动（管理员、立即开始连点、无 UAC 弹窗）。
  双击「取消开机自启.bat」即可撤销。

【自定义角色】
  把 PetForge 格式的角色文件夹放进 characters/ 后重启程序即可，无需重新编译。
  内置 5 个：Denia、娜波摩、菲比、赞妮、露帕。
  config.ini 里的 petName / petEnabled / petScale / petRealInput 可调整。

【出问题先看这里】
  - 点击次数不涨：确认 UAC 提权成功、桌宠窗口确实存在，且没有重复开第二个实例。
  - 端口 48643 被占用：说明已有实例在跑，本包会提示并默认不重复启动。
  - 托盘里退出程序即可完全停止连点。

【文件校验】
  WtClickerFusion.exe  SHA256: {exe_hash}
  同目录 SHA256SUMS.txt 列出了全部文件的校验值。

【重要声明】
  本程序内嵌了第三方官方桌面宠物程序及其精确状态桥补丁，仅供个人学习与研究使用，
  请勿用于商业用途或公开二次分发。仓库源码不包含 payload/payload.exe，
  clone 后自行构建需自备该资源。使用本项目请遵守相关游戏与软件的用户协议。
'''
    (BUNDLE / "使用说明.txt").write_text(readme, encoding="utf-8-sig")

    sums = []
    for p in sorted(BUNDLE.rglob("*"), key=lambda x: str(x.relative_to(BUNDLE))):
        if p.is_file() and p.name != "SHA256SUMS.txt":
            sums.append("%s  %s" % (sha256(p), p.relative_to(BUNDLE).as_posix()))
    (BUNDLE / "SHA256SUMS.txt").write_text("\n".join(sums) + "\n", encoding="utf-8")

    # 3) 打包。exe/PNG/DLL 已是压缩数据，用 ZIP_STORED 省时且不再膨胀。
    OUT.mkdir(parents=True, exist_ok=True)
    if ZIP.exists():
        ZIP.unlink()
    total = 0
    with zipfile.ZipFile(ZIP, "w", allowZip64=True) as z:
        prefix = BUNDLE.name
        zi = zipfile.ZipInfo(prefix + "/")
        zi.external_attr = 0x10 | 0o755 << 16
        z.writestr(zi, b"")
        for p in sorted(BUNDLE.rglob("*"), key=lambda x: str(x.relative_to(BUNDLE))):
            rel = prefix + "/" + p.relative_to(BUNDLE).as_posix()
            if p.is_dir():
                zi = zipfile.ZipInfo(rel + "/")
                zi.external_attr = 0x10 | 0o755 << 16
                z.writestr(zi, b"")
                continue
            store = p.suffix.lower() in (".exe", ".dll", ".png")
            z.write(p, rel, compress_type=zipfile.ZIP_STORED if store else zipfile.ZIP_DEFLATED)
            total += p.stat().st_size

    # 4) 完整性校验
    with zipfile.ZipFile(ZIP) as z:
        bad = z.testzip()
        names = z.namelist()
    print("=== 打包结果 ===")
    print("  %s" % ZIP)
    print("  包内 %d 个条目 / 原始 %.2f MB / zip %.2f MB" %
          (len(names), total / 1048576, ZIP.stat().st_size / 1048576))
    print("  CRC 校验:", "PASS" if bad is None else "FAIL " + str(bad))
    top = sorted(n[len(BUNDLE.name) + 1:] for n in names
                 if n.count("/") == 1 and not n.endswith("/"))
    print("  顶层文件:", top)
    print("  角色目录:", sorted({n.split("/")[2] for n in names if n.startswith(BUNDLE.name + "/characters/") and n.count("/") >= 3}))
    print("  exe SHA256:", exe_hash)


if __name__ == "__main__":
    main()
