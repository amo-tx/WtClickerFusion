"""验收：把自定义角色下拉框依次切到全部可用角色，确认每个都能加载并真正重绘。

用法：python tools/verify_all_pets.py <测试实例 PID> [日志路径]

WinForms 的 ComboBox 是原生控件，用 CB_SETCURSEL 改选中项，再向父窗投递
WM_COMMAND/CBN_SELCHANGE 让托管侧触发 SelectedIndexChanged → ApplyPet()。
每切一个角色：重新枚举 WS_EX_LAYERED 窗口（尺寸会随素材变化）、抓一帧、
算图像指纹；同时检查日志里有没有「角色…没有可用帧 / 窗口初始化失败」。
"""
import ctypes
import glob
import os
import sys
import time
from ctypes import wintypes

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

from PIL import Image, ImageGrab          # noqa: E402
import numpy as np                        # noqa: E402

u32 = ctypes.windll.user32
P = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
u32.EnumWindows.argtypes = [P, wintypes.LPARAM]
u32.EnumChildWindows.argtypes = [wintypes.HWND, P, wintypes.LPARAM]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongPtrW.restype = ctypes.c_longlong
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
u32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetDlgCtrlID.argtypes = [wintypes.HWND]
u32.GetDlgCtrlID.restype = ctypes.c_int

CB_GETCOUNT = 0x0146
CB_SETCURSEL = 0x014E
CB_GETLBTEXT = 0x0148
WM_COMMAND = 0x0111
CBN_SELCHANGE = 1
WS_EX_LAYERED = 0x00080000
WS_EX_NOACTIVATE = 0x08000000


def txt(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def klass(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def pidof(h):
    p = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def children(parent):
    out = []

    def cb(h, lp):
        out.append(h)
        return True
    u32.EnumChildWindows(parent, P(cb), 0)
    return out


def tops(pid):
    out = []

    def cb(h, lp):
        if pidof(h) == pid and u32.IsWindowVisible(h):
            out.append(h)
        return True
    u32.EnumWindows(P(cb), 0)
    return out


def layered(pid):
    for h in tops(pid):
        ex = u32.GetWindowLongPtrW(h, -20) & 0xFFFFFFFF
        if ex & WS_EX_LAYERED and ex & WS_EX_NOACTIVATE:
            return h
    return None


def cb_item(h, i):
    b = ctypes.create_unicode_buffer(512)
    u32.SendMessageW(h, CB_GETLBTEXT, i, ctypes.cast(b, ctypes.c_void_p).value)
    return b.value


def main():
    pid = int(sys.argv[1])
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    logp = sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, "dist_new", "WtClicker.log")
    mainh = None
    for h in tops(pid):
        if "WuwaTappo" in txt(h) and "融合版" in txt(h):
            mainh = h
    if not mainh:
        print("FAIL 未找到主窗体")
        return 2
    u32.ShowWindow(mainh, 9)
    time.sleep(0.4)

    combos = [h for h in children(mainh) if "ComboBox" in klass(h)]
    print("combos:", [(hex(h), u32.SendMessageW(h, CB_GETCOUNT, 0, 0), rect(h)) for h in combos])
    # 角色数量随 characters/ 增删变化，靠「下拉项里出现任一角色名」定位，
    # 不再硬编码项数（旧版写死 9 = 8 角色 + 1 个「关闭」项）。
    pet_names = [e.name for e in os.scandir(os.path.join(root, "characters")) if e.is_dir()]
    pet = None
    for h in combos:
        cnt = u32.SendMessageW(h, CB_GETCOUNT, 0, 0)
        if cnt <= 1:
            continue
        if any(nm in [cb_item(h, i) for i in range(cnt)] for nm in pet_names):
            pet = h
            break
    if pet is None and len(combos) >= 2:
        pet = combos[1]
    if pet is None:
        print("FAIL 未找到角色下拉框")
        return 3
    n = u32.SendMessageW(pet, CB_GETCOUNT, 0, 0)
    ctrl_id = u32.GetDlgCtrlID(pet)
    names = [cb_item(pet, i) for i in range(n)]
    print("pet combo=0x%X id=%d items=%d %r" % (pet, ctrl_id, n, names))

    outdir = os.path.join(root, "tools", "_pet_switch")
    os.makedirs(outdir, exist_ok=True)
    for f in glob.glob(os.path.join(outdir, "*.png")):
        os.remove(f)

    with open(logp, "rb") as f:
        logpos = f.seek(0, 2)

    sigs = []
    ok = True
    for i in range(1, n):
        u32.SendMessageW(pet, CB_SETCURSEL, i, 0)
        u32.SendMessageW(mainh, WM_COMMAND, (CBN_SELCHANGE << 16) | (ctrl_id & 0xFFFF), pet)
        time.sleep(0.55)
        ph = layered(pid)
        if not ph:
            print("  %d %-10s FAIL 角色窗口消失" % (i, names[i]))
            ok = False
            continue
        l, t, r, b = rect(ph)
        im = ImageGrab.grab(bbox=(l, t, r, b), include_layered_windows=True)
        path = os.path.join(outdir, "%02d_%s.png" % (i, names[i].replace("/", "_")))
        im.save(path)
        a = np.asarray(im.convert("RGB")).astype(np.float32)
        sig = float(np.abs(a - 128).mean()) + a.mean() * 0.001
        dup = any(abs(sig - s) < 0.05 for s in sigs)
        sigs.append(sig)
        print("  %d %-10s rect=%dx%d sig=%.3f %s"
              % (i, names[i], r - l, b - t, sig, "DUP?" if dup else "OK"))

    with open(logp, "rb") as f:
        f.seek(logpos)
        added = f.read().decode("utf-8", errors="replace")
    bad = [ln for ln in added.splitlines()
           if ("角色" in ln and ("没有可用帧" in ln or "初始化失败" in ln))
           or "未找到 characters" in ln]
    print("new log lines:", len(added.splitlines()), "errors:", len(bad))
    for ln in bad:
        print("  ", ln)
        ok = False
    print("VERDICT:", ("PASS %d 个角色均可切换加载并重绘" % (n - 1)) if ok and len(sigs) == n - 1 and not bad
          else "FAIL 存在加载异常")
    return 0 if ok and not bad else 1


if __name__ == "__main__":
    sys.exit(main())
