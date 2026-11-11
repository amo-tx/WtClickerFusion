"""自定义角色 ↔ 原生桌宠：遮挡深度 + 游戏模式跟随 的实机验收。

用法： python tools/verify_pet_depth.py <主程序PID> [端口]

覆盖 5 项：
 1) 深度状态与真实 z 序一致（自定义角色相对原生全屏画布是 above 还是 below）
 2) 把自定义角色移到屏幕上方 → 自动沉到原生画布下面（below）
 3) 移回下方 → 自动回到上面（above）
 4) 静置 6s 不来回翻转（防抖）
 5) 原生游戏模式（F10）→ 自定义角色跟随：保持可见但整窗穿透；退出后还原
"""
import ctypes
import json
import sys
import time
import urllib.request
from ctypes import wintypes

# 必须先做 DPI 感知：否则 GetWindowRect 返回 150% 虚拟化坐标，
# 和 /stats 里的物理像素脚底 Y 不在同一坐标系（实测差 1.5 倍）。
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

u32 = ctypes.windll.user32
u32.GetWindowLongPtrW.restype = ctypes.c_longlong
u32.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetDlgCtrlID.argtypes = [wintypes.HWND]
u32.GetDlgCtrlID.restype = ctypes.c_int
u32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
u32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
u32.GetWindow.restype = wintypes.HWND
u32.GetTopWindow.restype = wintypes.HWND
u32.GetTopWindow.argtypes = [wintypes.HWND]

WM_COMMAND = 0x0111
CB_GETCOUNT, CB_GETLBTEXT, CB_SETCURSEL = 0x0146, 0x0148, 0x014E
CBN_SELCHANGE = 0x0001
GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x20

PID = int(sys.argv[1])
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 48643
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))
results = []


def stats():
    return json.loads(OPENER.open("http://127.0.0.1:%d/stats" % PORT, timeout=5).read())


def record(name, ok, detail=""):
    results.append((name, ok, detail))
    print(("PASS " if ok else "FAIL ") + name + ("  | " + detail if detail else ""))


def cls(h):
    b = ctypes.create_unicode_buffer(128)
    u32.GetClassNameW(h, b, 128)
    return b.value


def title(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetWindowTextW(h, b, 256)
    return b.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def z_chain(limit=60):
    out, h, i = [], u32.GetTopWindow(None), 0
    while h and i < limit:
        if u32.IsWindowVisible(h):
            e = u32.GetWindowLongPtrW(h, GWL_EXSTYLE) & 0xFFFFFFFF
            if u32.GetWindowTextLengthW(h) or (e & 0x8):
                out.append(h)
                i += 1
        h = u32.GetWindow(h, 2)
    return out


def find_pids():
    """原生桌宠与自定义角色窗口（都是分层置顶、无标题）。"""
    pet_pid = None
    for h in z_chain(120):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value != PID and cls(h) == "Tauri Window" and title(h) == "Wuwa Tappo":
            pet_pid = p.value
    return pet_pid


def native_render(pet_pid):
    for h in z_chain(120):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pet_pid and cls(h) == "Tauri Window" and title(h) == "Wuwa Tappo":
            return h
    return None


def custom_pet():
    best = None
    for h in z_chain(120):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == PID and cls(h).startswith("WindowsForms10.Window") and title(h) == "":
            r = rect(h)
            if r[2] - r[0] > 20 and r[3] - r[1] > 20:
                best = h
    return best


def depth_actual(render, pet):
    chain = z_chain(120)
    if render not in chain or pet not in chain:
        return None
    return "above" if chain.index(pet) < chain.index(render) else "below"


def main_combo(target_text):
    """在主窗口里按内容找 ComboBox（位置下拉框）。"""
    tops = []
    cb = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def each(h, _):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == PID and u32.IsWindowVisible(h) and "WuwaTappo" in title(h):
            tops.append(h)
        return True

    u32.EnumWindows(cb(each), 0)
    if not tops:
        return None, -1
    kids = []
    u32.EnumChildWindows(tops[0], ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)(
        lambda h, _: (kids.append(h), True)[1]), 0)
    for h in kids:
        if "ComboBox" not in cls(h):
            continue
        n = u32.SendMessageW(h, CB_GETCOUNT, 0, 0)
        for i in range(n):
            buf = ctypes.create_unicode_buffer(128)
            u32.SendMessageW(h, CB_GETLBTEXT, i, ctypes.cast(buf, ctypes.c_void_p).value)
            if buf.value == target_text:
                return h, i
    return None, -1


def set_pos(combo, idx, main_hwnd):
    u32.SendMessageW(combo, CB_SETCURSEL, idx, 0)
    u32.SendMessageW(main_hwnd, WM_COMMAND,
                     (CBN_SELCHANGE << 16) | (u32.GetDlgCtrlID(combo) & 0xFFFF), combo)


def main():
    pet_pid = find_pids()
    render = native_render(pet_pid) if pet_pid else None
    pet = custom_pet()
    if not render or not pet:
        print("找不到原生画布或自定义角色窗口（pet_pid=%s render=%s pet=%s）" % (pet_pid, render, pet))
        return 1

    s = stats()
    st_depth = s.get("petDepth")
    native_feet = s.get("petNativeFeet", -1)
    my_feet = rect(pet)[3]
    actual = depth_actual(render, pet)
    record("深度状态与真实 z 序一致", st_depth == actual,
           "stats=%s actual=%s 自定义脚底=%s 原生脚底=%s" % (st_depth, actual, my_feet, native_feet))

    # 找主窗口（用于发 WM_COMMAND）
    main_hwnd = None
    for h in z_chain(120):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == PID and title(h).startswith("WuwaTappo 融合版"):
            main_hwnd = h
            break
    combo, idx_tl = main_combo("左上")
    combo2, idx_br = main_combo("右下")
    if not combo or not combo2 or not main_hwnd:
        record("定位「位置」下拉框", False, "combo=%s combo2=%s main=%s" % (combo, combo2, main_hwnd))
        return 1

    # 2) 移到左上 → 应该沉到原生画布下面
    set_pos(combo, idx_tl, main_hwnd)
    time.sleep(3.0)
    s2 = stats()
    act2 = depth_actual(render, pet)
    record("移到屏幕上方后沉到原生角色之后", s2.get("petDepth") == "below" and act2 == "below",
           "stats=%s actual=%s 自定义脚底=%s" % (s2.get("petDepth"), act2, rect(pet)[3]))

    # 4) 静置 6s 不抖
    sw_before = s2.get("petDepthSwaps")
    time.sleep(6.0)
    s3 = stats()
    record("静置 6s 不反复翻转", s3.get("petDepthSwaps") == sw_before,
           "swaps %s -> %s" % (sw_before, s3.get("petDepthSwaps")))

    # 3) 移回右下 → 回到上面
    set_pos(combo2, idx_br, main_hwnd)
    time.sleep(3.0)
    s4 = stats()
    act4 = depth_actual(render, pet)
    record("移回右下后重新压在原生角色之上", s4.get("petDepth") == "above" and act4 == "above",
           "stats=%s actual=%s 自定义脚底=%s" % (s4.get("petDepth"), act4, rect(pet)[3]))

    # 5) 游戏模式跟随（F10 切原生游戏模式）
    gm0 = s4.get("petNativeGameMode")
    u32.keybd_event(0x79, 0, 0, 0)
    time.sleep(0.06)
    u32.keybd_event(0x79, 0, 2, 0)
    time.sleep(2.5)
    s5 = stats()
    ex = u32.GetWindowLongPtrW(pet, GWL_EXSTYLE) & 0xFFFFFFFF
    passed = (s5.get("petNativeGameMode") is True and bool(ex & WS_EX_TRANSPARENT)
              and u32.IsWindowVisible(pet))
    record("进入游戏模式：角色仍显示但整窗穿透", passed,
           "stats=%s exstyle=0x%08X visible=%s" % (s5.get("petNativeGameMode"), ex, u32.IsWindowVisible(pet)))

    u32.keybd_event(0x79, 0, 0, 0)
    time.sleep(0.06)
    u32.keybd_event(0x79, 0, 2, 0)
    time.sleep(2.5)
    s6 = stats()
    record("退出游戏模式：状态还原", s6.get("petNativeGameMode") is False,
           "stats=%s switches=%s（回原状态 %s）" % (s6.get("petNativeGameMode"),
                                                    s6.get("petGameModeSwitches"), gm0))

    fails = [r for r in results if not r[1]]
    print("\nRESULT: %s (%d/%d)" % ("PASS" if not fails else "FAIL",
                                    len(results) - len(fails), len(results)))
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())
