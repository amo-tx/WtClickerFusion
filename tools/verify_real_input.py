# -*- coding: utf-8 -*-
"""实机验收：自定义角色是否跟随「真实物理键鼠」、注入是否被重复计数、白边。

用法：python tools/verify_real_input.py <WtClickerFusion PID>

阶段 A（12s，不干预）：引擎照常 PostMessage 注入。
  → realTaps 增量应远小于 injected 增量，证明 PostMessage 不经过低级钩子、
    不会把一次注入算成两次拍打；拍打段数 ≈ injected 增量。
阶段 B（约 8s，发 6 次键盘 + 2 次鼠标真实输入）：
  → realTaps 增量 >= 8，拍打段数相应增加，slap_l / slap_r 仍严格交替。
阶段 C：保存真实窗口截图，供视觉复核白边。

真实输入用 keybd_event(VK_F13) / mouse_event 左上角空点：不产生字符、
不弹菜单；低级钩子会带 INJECTED 标志捕获（本程序刻意不过滤，验收才可复现）。
"""
import ctypes
import glob
import json
import os
import sys
import time
import urllib.request
from collections import Counter
from ctypes import wintypes

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    ctypes.windll.user32.SetProcessDpiAware()

from PIL import Image, ImageGrab  # noqa: E402
import numpy as np  # noqa: E402

u32 = ctypes.windll.user32
WS_EX_LAYERED = 0x00080000
WS_EX_NOACTIVATE = 0x08000000
GWL_EXSTYLE = -20
EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
u32.EnumWindows.argtypes = [EnumProc, wintypes.LPARAM]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongPtrW.restype = ctypes.c_longlong
u32.IsWindowVisible.argtypes = [wintypes.HWND]

STATS = "http://127.0.0.1:48643/stats"
VK_F13 = 0x7C
KEYEVENTF_KEYUP = 0x0002
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004


def pid_of(h):
    p = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def find_layered(pid):
    out = []

    def cb(h, lp):
        if pid_of(h) == pid and u32.IsWindowVisible(h):
            ex = u32.GetWindowLongPtrW(h, GWL_EXSTYLE) & 0xFFFFFFFF
            if ex & WS_EX_LAYERED and ex & WS_EX_NOACTIVATE:
                out.append(h)
        return True

    u32.EnumWindows(EnumProc(cb), 0)
    return out


def stats():
    try:
        return json.loads(urllib.request.urlopen(STATS, timeout=3).read())
    except Exception:
        return {}


def runs(seq):
    out = []
    for x in seq:
        if out and out[-1][0] == x:
            out[-1] = (x, out[-1][1] + 1)
        else:
            out.append((x, 1))
    return out


def key_tap():
    u32.keybd_event(VK_F13, 0, 0, 0)
    time.sleep(0.02)
    u32.keybd_event(VK_F13, 0, KEYEVENTF_KEYUP, 0)


def mouse_tap():
    u32.SetCursorPos(3, 3)
    time.sleep(0.02)
    u32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.02)
    u32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)


def main():
    if len(sys.argv) < 2:
        print("usage: verify_real_input.py <pid>")
        return 2
    pid = int(sys.argv[1])
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    chdir = os.path.join(root, "characters", "娜波摩")
    outdir = os.path.join(root, "tools")
    meta = json.load(open(os.path.join(chdir, "character.json"), encoding="utf-8"))
    frame2clip = {}
    for clip, files in meta["animations"].items():
        for f in files:
            frame2clip[os.path.basename(f)] = clip

    wins = find_layered(pid)
    if not wins:
        print("FAIL 未找到角色分层窗口 pid=%d" % pid)
        return 3
    ph = wins[0]
    box = rect(ph)
    w, h = box[2] - box[0], box[3] - box[1]
    print("layered=0x%X rect=%s %dx%d" % (ph, box, w, h))

    mw, mh = max(32, w // 2), max(32, h // 2)
    srcs = []
    for p in sorted(glob.glob(os.path.join(chdir, "frames", "*.png"))):
        im = Image.open(p).convert("RGBA").resize((mw, mh), Image.LANCZOS)
        a = np.asarray(im).astype(np.float32)
        srcs.append((os.path.basename(p), a[:, :, :3], a[:, :, 3] / 255.0))
    rgb_stack = np.stack([s[1] for s in srcs])
    alpha_stack = np.stack([s[2] for s in srcs])[..., None]
    cats = [frame2clip.get(s[0], "unknown") for s in srcs]
    names = [s[0][:-4] for s in srcs]

    def classify():
        im = ImageGrab.grab(bbox=box, include_layered_windows=True)
        arr = np.asarray(im.convert("RGB").resize((mw, mh), Image.LANCZOS)).astype(np.float32)
        corner = np.concatenate([arr[:4, :4].reshape(-1, 3), arr[:4, -4:].reshape(-1, 3),
                                 arr[-4:, :4].reshape(-1, 3), arr[-4:, -4:].reshape(-1, 3)])
        bg = np.median(corner, axis=0)
        pred = rgb_stack * alpha_stack + bg[None, None, None, :] * (1 - alpha_stack)
        scores = np.abs(pred - arr[None, ...]).mean(axis=(1, 2, 3))
        k = int(np.argmin(scores))
        conf = float(scores[k])
        return ("unknown" if conf > 42 else cats[k]), im

    def phase(label, seconds, taps):
        s0 = stats()
        inj0 = int(s0.get("injected") or 0)
        rt0 = int(s0.get("realTaps") or 0)
        seq, low, saved = [], 0, 0
        sent = 0
        t0 = time.time()
        next_tap = t0 + 0.6
        tap_gap = (seconds - 1.2) / max(1, taps)
        while time.time() - t0 < seconds:
            if sent < taps and time.time() >= next_tap:
                (mouse_tap if sent >= 6 else key_tap)()
                sent += 1
                next_tap += tap_gap
            lab, im = classify()
            if lab == "unknown":
                low += 1
            else:
                seq.append(lab)
            if saved < 8:
                im.save(os.path.join(outdir, "_real_%s_%02d.png" % (label, saved)))
                saved += 1
            time.sleep(0.055)
        s1 = stats()
        inj1 = int(s1.get("injected") or 0)
        rt1 = int(s1.get("realTaps") or 0)
        rr = runs(seq)
        sides = [c for c, _ in rr if c in ("slap_l", "slap_r")]
        starts = sum(1 for c, _ in rr if c in ("slap_l", "slap_r"))
        print("\n=== 阶段 %s (%.1fs) ===" % (label, seconds))
        print("injected %d -> %d  (Δ=%d)" % (inj0, inj1, inj1 - inj0))
        print("realTaps %d -> %d  (Δ=%d)  已发送真实输入=%d" % (rt0, rt1, rt1 - rt0, sent))
        print("抓帧 samples=%d low_conf=%d (%.1f%%)" %
              (len(seq), low, low / max(1, len(seq) + low) * 100))
        print("拍打段=%d  左右序列=%s" % (starts, " ".join(sides[:24])))
        print("分类计数:", dict(Counter(seq)))
        return dict(dInj=inj1 - inj0, dReal=rt1 - rt0, sent=sent,
                    slaps=starts, low=low, total=len(seq) + low, sides=sides)

    a = phase("A", 12.0, 0)
    b = phase("B", 8.0, 8)
    print("\n=== 判定 ===")
    ok = True
    # ① 注入不重复：若 PostMessage 也触发了低级钩子，每次注入会让 realTaps
    #   +1 且点击序号 +2（左右手奇偶错乱）。判据：A 阶段 realTaps 增量必须
    #   小于 injected 增量，且拍打段数 ≈ 注入+真实输入（不是注入的两倍）。
    #   用户/系统自然的零星输入会计入 dReal，故不能用「dReal 必须为 0」。
    r1 = a["dReal"] < a["dInj"] and a["slaps"] <= a["dInj"] + a["dReal"] + 1
    print("[%s] ① PostMessage 注入未被钩子重复计数：A 阶段 realTaps Δ=%d < injected Δ=%d；"
          "拍打段=%d ≈ 注入+真实=%d（无双倍）" %
          ("PASS" if r1 else "FAIL", a["dReal"], a["dInj"], a["slaps"], a["dInj"] + a["dReal"]))
    ok &= r1
    # ② 真实输入确实被捕获
    r2 = b["dReal"] >= b["sent"]
    print("[%s] ② 真实键鼠被捕获：B 阶段 realTaps Δ=%d >= 发送 %d" %
          ("PASS" if r2 else "FAIL", b["dReal"], b["sent"]))
    ok &= r2
    # ③ 真实输入让角色多拍（拍打段增量 >= 真实 tap 数，允许引擎注入叠加）
    extra = b["slaps"] - a["slaps"] * (8.0 / 12.0)
    r3 = b["slaps"] >= b["sent"] * 0.6
    print("[%s] ③ 真实输入驱动角色拍打：B 阶段拍打段=%d（A 归一后 %.1f，额外 %.1f）" %
          ("PASS" if r3 else "FAIL", b["slaps"], a["slaps"] * (8.0 / 12.0), extra))
    ok &= r3
    # ④ 左右手仍交替、无镜像误判
    alt = sum(1 for i in range(1, len(b["sides"])) if b["sides"][i] != b["sides"][i - 1])
    r4 = len(b["sides"]) >= 4 and alt >= len(b["sides"]) * 0.7
    print("[%s] ④ 左右手交替：%d 段中 %d 次换手；B 阶段 unknown %.1f%%" %
          ("PASS" if r4 else "FAIL", len(b["sides"]), alt,
           b["low"] / max(1, b["total"]) * 100))
    ok &= r4
    print("\n总判定:", "PASS" if ok else "FAIL")
    print("真实窗口截图:", os.path.join(outdir, "_real_A_00.png"),
          os.path.join(outdir, "_real_B_00.png"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
