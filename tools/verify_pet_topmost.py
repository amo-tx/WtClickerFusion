"""验证「自定义角色窗口始终在最顶层」。

复现的真实故障（2026-11-11）：角色窗口 exstyle 带 WS_EX_TOPMOST，但真实 z 序排在
鸣潮 / 浏览器 / QQ / 主窗口之下 —— 有置顶样式位、却不在置顶带里。

本脚本不依赖任何第三方库（tkinter 只用来造一个普通的竞争窗口），检查项：
  1) 角色窗口带 WS_EX_TOPMOST，且 z 序上方不存在任何**普通**窗口；
  2) 造一个覆盖角色区域的普通窗口并激活它 → 角色窗口仍在其之上；
  3) 用 SetWindowPos(HWND_NOTOPMOST) 把角色窗口强行踢出置顶带（模拟被别的程序
     抢层级 / 掉出置顶层），1.6s 内必须自动爬回置顶带且样式位恢复；
  4) 巡检不会疯狂重排 z 序：静置 4s 期间提升次数不增长；
  5) 提升过程不抢焦点（前台窗口前后一致）。

用法: python tools/verify_pet_topmost.py <pid> [stats_port]
"""

import ctypes
import json
import sys
import time
import urllib.request
from ctypes import wintypes

u32 = ctypes.windll.user32
u32.GetWindowLongPtrW.restype = ctypes.c_longlong
u32.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
u32.GetWindow.restype = wintypes.HWND
u32.GetTopWindow.restype = wintypes.HWND
u32.GetTopWindow.argtypes = [wintypes.HWND]
u32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int,
                             ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.UINT]
u32.GetForegroundWindow.restype = wintypes.HWND

GWL_EXSTYLE = -20
WS_EX_TOPMOST = 0x00000008
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_LAYERED = 0x00080000
WS_EX_NOACTIVATE = 0x08000000
GW_HWNDNEXT = 2
GW_HWNDPREV = 3
HWND_TOPMOST = wintypes.HWND(-1)
HWND_NOTOPMOST = wintypes.HWND(-2)
SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x0001, 0x0002, 0x0010

P = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def title(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def klass(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def exstyle(h):
    return u32.GetWindowLongPtrW(h, GWL_EXSTYLE) & 0xFFFFFFFF


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def find_pet(pid):
    """角色窗口：本进程、可见、分层 + 工具窗 + 不激活、无标题的 WinForms 窗口。"""
    found = []

    def cb(h, _):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value != pid or not u32.IsWindowVisible(h):
            return True
        e = exstyle(h)
        need = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        if (e & need) == need and klass(h).startswith("WindowsForms10") and not title(h):
            found.append(h)
        return True

    u32.EnumWindows(P(cb), 0)
    return found[0] if found else None


def zorder():
    out, h = [], u32.GetTopWindow(None)
    while h:
        if u32.IsWindowVisible(h):
            out.append(h)
        h = u32.GetWindow(h, GW_HWNDNEXT)
    return out


def normal_above(hwnd):
    """z 序里位于 hwnd 之上、可见、未最小化、有面积且**非置顶**的窗口。"""
    bad = []
    p = u32.GetWindow(hwnd, GW_HWNDPREV)
    while p and len(bad) < 200:
        if u32.IsWindowVisible(p) and not u32.IsIconic(p):
            l, t, r, b = rect(p)
            if r > l and b > t and (exstyle(p) & WS_EX_TOPMOST) == 0:
                bad.append((hex(p), klass(p)[:24], title(p)[:20]))
        p = u32.GetWindow(p, GW_HWNDPREV)
    return bad


def is_topmost(hwnd):
    return bool(exstyle(hwnd) & WS_EX_TOPMOST)


def stats(port):
    op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    return json.loads(op.open("http://127.0.0.1:%d/stats" % port, timeout=5).read())


def wait_until(fn, timeout=2.0, step=0.1):
    end = time.time() + timeout
    while time.time() < end:
        if fn():
            return True
        time.sleep(step)
    return fn()


def main():
    if len(sys.argv) < 2:
        print("usage: verify_pet_topmost.py <pid> [port]")
        return 2
    pid = int(sys.argv[1])
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 48643

    pet = find_pet(pid)
    if not pet:
        print("FAIL: 找不到角色窗口（请确认托盘角色已开启）")
        return 1
    print("pet hwnd =", hex(pet), "rect =", rect(pet), "exstyle = 0x%08X" % exstyle(pet))

    results = []

    def record(name, ok, detail=""):
        results.append((name, ok, detail))
        print(("PASS  " if ok else "FAIL  ") + name + ("  | " + detail if detail else ""))

    # --- 1) 基线：带置顶样式位，且上方没有普通窗口
    ok1 = wait_until(lambda: is_topmost(pet) and not normal_above(pet), 2.0)
    record("基线：角色窗口在置顶带最上层", ok1,
           "topmost=%s normal_above=%d" % (is_topmost(pet), len(normal_above(pet))))

    base_raises = stats(port).get("petTopMostRaises", 0)
    fg_before = u32.GetForegroundWindow()

    # --- 2) 普通竞争窗口盖上来（模拟聊天软件/浏览器/游戏窗口）
    l, t, r, b = rect(pet)
    cw, ch = max(240, r - l + 120), max(240, b - t + 120)
    WS_OVERLAPPEDWINDOW, WS_VISIBLE = 0x00CF0000, 0x10000000
    u32.CreateWindowExW.restype = wintypes.HWND
    u32.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR,
                                    wintypes.DWORD, ctypes.c_int, ctypes.c_int,
                                    ctypes.c_int, ctypes.c_int,
                                    wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE,
                                    wintypes.LPVOID]
    comp_hwnd = u32.CreateWindowExW(0, "STATIC", "topmost-probe",
                                    WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                                    max(0, l - 60), max(0, t - 40), cw, ch,
                                    None, None, None, None)
    if not comp_hwnd:
        print("FAIL: 竞争窗口创建失败 err=%d" % ctypes.get_last_error())
        return 1
    u32.ShowWindow(comp_hwnd, 5)          # SW_SHOW
    u32.SetForegroundWindow(comp_hwnd)
    time.sleep(0.5)
    order = zorder()
    above = (comp_hwnd in order and pet in order
             and order.index(pet) < order.index(comp_hwnd))
    record("普通窗口盖上来后角色仍在最顶层", above,
           "pet_idx=%s comp_idx=%s" % (order.index(pet) if pet in order else None,
                                       order.index(comp_hwnd) if comp_hwnd in order else None))

    # --- 3) 强行踢出置顶带，等巡检自动纠正
    u32.SetWindowPos(pet, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
    time.sleep(0.15)
    kicked = (not is_topmost(pet)) or bool(normal_above(pet))
    print("       已踢出置顶带: topmost=%s normal_above=%d" % (is_topmost(pet), len(normal_above(pet))))

    recovered = wait_until(lambda: is_topmost(pet) and not normal_above(pet), 2.5)
    record("掉层后 2.5s 内自动恢复置顶", kicked and recovered,
           "topmost=%s normal_above=%d" % (is_topmost(pet), len(normal_above(pet))))

    u32.DestroyWindow(comp_hwnd)
    time.sleep(0.3)

    # --- 4) 静置不抖动：提升次数不再增长
    n1 = stats(port).get("petTopMostRaises", 0)
    time.sleep(4.0)
    n2 = stats(port).get("petTopMostRaises", 0)
    record("静置 4s 巡检不反复重排 z 序", n2 == n1, "raises %d -> %d（基线 %d）" % (n1, n2, base_raises))

    # --- 5) 不抢焦点
    fg_after = u32.GetForegroundWindow()
    record("置顶提升不抢焦点", fg_before == fg_after,
           "foreground %s -> %s" % (hex(fg_before), hex(fg_after)))

    bad = [r for r in results if not r[1]]
    print("RESULT:", "PASS" if not bad else "FAIL", "| %d/%d 项通过" % (len(results) - len(bad), len(results)))
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
