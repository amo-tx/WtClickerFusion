# -*- coding: utf-8 -*-
"""验收「娜波摩」v3 动画：无镜像翻转 + 点击序号驱动的逐次同步。

用法：python tools/verify_nabo_states.py <WtClickerFusion PID> [seconds]

原理：
1) PerMonitorV2 DPI 感知，枚举该 PID 的 WS_EX_LAYERED 角色窗口；
2) 连续抓窗口，与 characters/娜波摩/frames/*.png 做「按桌面背景色 alpha
   合成后的全画面」模板匹配；分组直接从 character.json 读取，不硬编码帧号；
3) 判定：
   - 无镜像翻转：v3 素材库里一张镜像帧都没有；若运行中出现翻转画面，
     与所有原帧模板的色差都会很大 => unknown 比例会明显升高。
   - 逐次同步：采样期间 /stats 的 injected 增量 ≈ 拍打 clip 启动次数，
     且 slap_l / slap_r 按点击序号奇偶交替。
   - 兴奋狂拍：概率触发，frenzy_l 段必须先于 frenzy_r 段、两组都出现，
     区段内不回落到平静（不可打断），结束才回平静。
   - 每套拍打序列内部有多张不同帧（证明不是静止帧循环）。

注意：frenzyChance 默认 0.12 + 8s 冷却；确定性验收可临时设为 1.0。
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

from PIL import Image, ImageGrab   # noqa: E402
import numpy as np                 # noqa: E402

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


def pid_of(h):
    p = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def exstyle(h):
    return u32.GetWindowLongPtrW(h, GWL_EXSTYLE) & 0xFFFFFFFF


def find_layered(pid):
    out = []

    def cb(h, lp):
        if pid_of(h) == pid and u32.IsWindowVisible(h):
            ex = exstyle(h)
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


def main():
    if len(sys.argv) < 2:
        print("usage: verify_nabo_states.py <pid> [seconds]")
        return 2
    pid = int(sys.argv[1])
    seconds = float(sys.argv[2]) if len(sys.argv) > 2 else 45.0
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    chdir = os.path.join(root, "characters", "娜波摩")
    outdir = os.path.join(root, "tools", "_nabo_states")
    os.makedirs(outdir, exist_ok=True)
    for f in glob.glob(os.path.join(outdir, "*.png")):
        os.remove(f)

    meta = json.load(open(os.path.join(chdir, "character.json"), encoding="utf-8"))
    frame2clip = {}
    for clip, files in meta["animations"].items():
        for f in files:
            frame2clip[os.path.basename(f)] = clip
    mirrored = [f for f in glob.glob(os.path.join(chdir, "frames", "*.png"))
                if os.path.basename(f).endswith(("_m.png", "_h.png"))]
    print("animations:", {k: len(v) for k, v in meta["animations"].items()},
          " mirror frames in library =", len(mirrored))

    wins = find_layered(pid)
    if not wins:
        print("FAIL 未找到角色分层窗口 pid=%d" % pid)
        return 3
    ph = wins[0]
    box = rect(ph)
    w, h = box[2] - box[0], box[3] - box[1]
    print("layered=0x%X rect=%s size=%dx%d sample=%.1fs" % (ph, box, w, h, seconds))

    mw, mh = max(32, w // 2), max(32, h // 2)
    fps = sorted(glob.glob(os.path.join(chdir, "frames", "*.png")))
    srcs = []
    for p in fps:
        im = Image.open(p).convert("RGBA").resize((mw, mh), Image.LANCZOS)
        a = np.asarray(im).astype(np.float32)
        srcs.append((os.path.basename(p), a[:, :, :3], a[:, :, 3] / 255.0))
    rgb_stack = np.stack([s[1] for s in srcs])
    alpha_stack = np.stack([s[2] for s in srcs])[..., None]
    names = [s[0][:-4] for s in srcs]
    cats = [frame2clip.get(s[0], "unknown") for s in srcs]
    print("src frames=%d match size=%dx%d" % (len(srcs), mw, mh))

    s0 = stats()
    inj0 = int(s0.get("injected") or 0)
    seq, saved, low_conf = [], 0, 0
    t_end = time.time() + seconds
    while time.time() < t_end:
        im = ImageGrab.grab(bbox=box, include_layered_windows=True)
        arr = np.asarray(im.convert("RGB").resize((mw, mh), Image.LANCZOS)).astype(np.float32)
        corner = np.concatenate([arr[:4, :4].reshape(-1, 3), arr[:4, -4:].reshape(-1, 3),
                                 arr[-4:, :4].reshape(-1, 3), arr[-4:, -4:].reshape(-1, 3)])
        bg = np.median(corner, axis=0)
        pred = rgb_stack * alpha_stack + bg[None, None, None, :] * (1 - alpha_stack)
        scores = np.abs(pred - arr[None, ...]).mean(axis=(1, 2, 3))
        k = int(np.argmin(scores))
        conf = float(scores[k])
        label = "unknown" if conf > 42 else cats[k]
        if label == "unknown":
            low_conf += 1
        else:
            seq.append((names[k], label, conf))
        if saved < 12:
            im.save(os.path.join(outdir, "%03d_%s_%s.png" % (saved, label, names[k])))
            saved += 1
        time.sleep(0.055)
    s1 = stats()
    inj1 = int(s1.get("injected") or 0)
    d_inj = inj1 - inj0

    if not seq:
        print("FAIL 未抓到任何可信帧")
        return 7
    total = len(seq) + low_conf
    unknown_rate = low_conf / max(1, total)
    cat_seq = [c for _, c, _ in seq]
    rr = runs(cat_seq)
    print("samples=%d low_conf=%d (%.1f%%) distinct_frames=%d" %
          (len(seq), low_conf, unknown_rate * 100, len(set(n for n, _, _ in seq))))
    print("runs:", " ".join("%s x%d" % (c, n) for c, n in rr[:70]))
    print("category counts:", dict(Counter(cat_seq)))
    print("injected delta during sample =", d_inj)

    # ---- 拍打启动次数：从非 slap 切到 slap_l / slap_r ----
    starts = []
    prev = None
    for c, n in rr:
        if c in ("slap_l", "slap_r") and prev not in ("slap_l", "slap_r"):
            starts.append(c)
        prev = c
    # 同侧连续两段（高 CPS 打断重播）也各算一次
    slap_runs = [c for c, _ in rr if c in ("slap_l", "slap_r")]
    print("slap clip starts=%d  (injected=%d, ratio=%.2f)" %
          (len(starts), d_inj, len(starts) / max(1, d_inj)))
    print("slap side sequence:", " ".join(slap_runs[:30]))

    # ---- 兴奋段：L 段必须先于 R 段，两组都出现，之后才回平静 ----
    segments, cur = [], []
    for c, n in rr:
        if c in ("frenzy_l", "frenzy_r"):
            cur.append((c, n))
        else:
            if cur:
                segments.append(cur)
                cur = []
    if cur:
        segments.append(cur)
    seg_ok = 0
    for i, seg in enumerate(segments):
        sc = [c for c, _ in seg]
        first_l = sc.index("frenzy_l") if "frenzy_l" in sc else 99
        first_r = sc.index("frenzy_r") if "frenzy_r" in sc else 99
        complete = first_l < first_r and first_r < 99
        tail_l = max(j for j, c in enumerate(sc) if c == "frenzy_l") if first_l < 99 else -1
        tail_r = max(j for j, c in enumerate(sc) if c == "frenzy_r") if first_r < 99 else -1
        no_reentry = tail_l < tail_r
        ok = complete and no_reentry
        seg_ok += 1 if ok else 0
        print("  frenzy segment %d: %s => %s" %
              (i, " ".join("%sx%d" % (c, n) for c, n in seg), "OK" if ok else "BROKEN"))
    print("frenzy segments=%d complete=%d" % (len(segments), seg_ok))

    # ---- 交替：相邻拍打段不能总是同一侧 ----
    alt = sum(1 for a, b in zip(slap_runs, slap_runs[1:]) if a != b)
    alt_ok = len(slap_runs) < 2 or alt >= max(1, (len(slap_runs) - 1) // 2)
    # ---- 每套序列内部真的有多张不同帧 ----
    per_clip = {}
    for n, c, _ in seq:
        per_clip.setdefault(c, set()).add(n)
    phase_ok = all(len(per_clip.get(c, set())) >= 2 for c in ("slap_l", "slap_r")
                   if c in per_clip)
    print("per-clip distinct frames:", {k: len(v) for k, v in per_clip.items()})

    no_flip = len(mirrored) == 0 and unknown_rate < 0.25
    sync_ok = d_inj == 0 or len(starts) >= max(1, int(d_inj * 0.5))
    frenzy_ok = not segments or seg_ok == len(segments)
    ok = no_flip and sync_ok and alt_ok and phase_ok and frenzy_ok
    print("VERDICT:", "PASS" if ok else "FAIL",
          "| 无镜像翻转=%s (素材镜像帧%d, unknown %.1f%%)" % (no_flip, len(mirrored), unknown_rate * 100),
          "| 点击同步=%s (注入%d / 拍打段%d)" % (sync_ok, d_inj, len(starts)),
          "| 左右交替=%s" % alt_ok,
          "| 相位有动作=%s" % phase_ok,
          "| 兴奋完整=%s" % frenzy_ok)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
