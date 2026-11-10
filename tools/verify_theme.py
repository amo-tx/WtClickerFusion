"""UI 主题一键切换验收（实机）。

用法: python tools/verify_theme.py <PID> [config.ini路径]

做四件事并打印结果：
 1) 截取启动时的主题 -> docs/ui-<当前主题>.png（可用来验证 config.ini 持久化生效）；
 2) 向主题切换按钮发鼠标按下/抬起消息，读 /stats 确认主题翻转，截图对照图；
 3) 再点一次回到起始主题，验证往返切换不累积偏差；
 4) 检查 config.ini 写入的 theme 与最终状态一致。

截图同时是人工视觉验收材料（文字可读性、是否残留浅色块、标题栏是否变黑）。
"""
import ctypes
import json
import os
import sys
import time
import urllib.request
from ctypes import wintypes

from PIL import ImageGrab

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCS = os.path.join(ROOT, "docs")

u32 = ctypes.windll.user32
try:                                   # 让 GetWindowRect 与 ImageGrab 使用同一套物理坐标
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        u32.SetProcessDPIAware()
    except Exception:
        pass

CB = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
u32.EnumWindows.argtypes = [CB, wintypes.LPARAM]
u32.EnumChildWindows.argtypes = [wintypes.HWND, CB, wintypes.LPARAM]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.SetForegroundWindow.argtypes = [wintypes.HWND]
u32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
u32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]


def find_main_window(pid):
    found = []

    def cb(h, _):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value != pid:
            return True
        b = ctypes.create_unicode_buffer(512)
        u32.GetWindowTextW(h, b, 512)
        if b.value and u32.IsWindowVisible(h) and ("WuwaTappo" in b.value or "融合版" in b.value):
            found.append((h, b.value))
        return True

    u32.EnumWindows(CB(cb), 0)
    return found


def find_button(main, keyword):
    hits = []

    def cb(h, _):
        b = ctypes.create_unicode_buffer(256)
        u32.GetWindowTextW(h, b, 256)
        if keyword in b.value:
            hits.append((h, b.value))
        return True

    u32.EnumChildWindows(main, CB(cb), 0)
    return hits


def stats():
    op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    return json.loads(op.open("http://127.0.0.1:48643/stats", timeout=5).read())


def click(hwnd):
    r = wintypes.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    w, ht = r.right - r.left, r.bottom - r.top
    lp = ((ht // 2) << 16) | (w // 2)     # 按钮客户区中心
    u32.SendMessageW(hwnd, 0x0201, 1, lp)  # WM_LBUTTONDOWN
    u32.SendMessageW(hwnd, 0x0202, 0, lp)  # WM_LBUTTONUP -> Click
    time.sleep(0.5)


def shot(main, name):
    u32.ShowWindow(main, 9)               # SW_RESTORE
    u32.SetForegroundWindow(main)
    time.sleep(0.8)
    r = wintypes.RECT()
    u32.GetWindowRect(main, ctypes.byref(r))
    box = (r.left, r.top, r.right, r.bottom)
    img = ImageGrab.grab(bbox=box, all_screens=True)
    path = os.path.join(DOCS, name)
    img.save(path)
    return path, img.size, box


def main():
    pid = int(sys.argv[1]) if len(sys.argv) > 1 else None
    cfg = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "dist", "config.ini")
    if pid is None:
        print("FAIL: 需要主程序 PID")
        return 1
    wins = find_main_window(pid)
    if not wins:
        print("FAIL: 找不到主窗口")
        return 1
    mainh, title = wins[0]
    print("main window:", hex(mainh), title)

    btns = find_button(mainh, "切换")
    if not btns:
        print("FAIL: 找不到主题切换按钮；子窗口文本：")
        def dump(h, _):
            b = ctypes.create_unicode_buffer(256)
            u32.GetWindowTextW(h, b, 256)
            print("   ", hex(h), repr(b.value))
            return True
        u32.EnumChildWindows(mainh, CB(dump), 0)
        return 1
    btn = btns[0][0]
    print("theme button:", hex(btn), btns[0][1])

    t0 = stats().get("theme")
    print("theme at startup =", t0, "(由 config.ini 决定)")
    p0, size0, box0 = shot(mainh, f"ui-{t0}.png")
    print("shot A:", p0, size0, box0)

    click(btn)
    t1 = stats().get("theme")
    print("theme after click #1 =", t1)
    p1, size1, box1 = shot(mainh, f"ui-{t1}.png")
    print("shot B:", p1, size1, box1)

    click(btn)
    t2 = stats().get("theme")
    print("theme after click #2 (left as final) =", t2)

    text = open(cfg, encoding="utf-8").read()
    cfg_theme = next((l.split("=", 1)[1].strip() for l in text.splitlines()
                      if l.strip().startswith("theme=")), None)
    print("config.ini theme =", cfg_theme)

    ok = (t0 in ("light", "dark") and t1 != t0 and t2 == t0 and cfg_theme == t2)
    print("RESULT:", "PASS" if ok else "FAIL",
          f"| {t0}->{t1}->{t2} 往返切换 + 配置持久化")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
