"""端到端验收：点「测试一次」→ 自定义角色窗口是否真的播放拍打动画。

用法：python tools/verify_pet_anim.py <WtClickerFusion 测试实例 PID>

原理：
1) 把本进程设为 PerMonitorV2 DPI 感知，保证窗口矩形与屏幕抓帧都是物理像素；
2) 枚举该 PID 的顶层窗口，找出主窗体与 WS_EX_LAYERED 角色窗口；
3) 用 BM_CLICK 触发主窗体上的「测试一次」按钮（不移动真实鼠标）；
4) 点击前后各连拍角色窗口矩形，保存到 tools/_anim_frames/；
5) 把每张抓帧与 characters/娜波摩/frames 全部源帧做 alpha 掩膜配色差，
   分组从 character.json 动态读取（idle / slap_l / slap_r / frenzy_l / frenzy_r），
   不硬编码帧号 —— v3 改成 19 张全原始帧后旧版按索引判定会失效。

判据：点击前全部 idle；点击后出现 >=2 张非 idle 帧，即事件驱动链路通。
"""
import ctypes
import glob
import json
import os
import sys
import time
from ctypes import wintypes

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)   # PROCESS_PER_MONITOR_DPI_AWARE
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

from PIL import Image, ImageGrab           # noqa: E402
import numpy as np                         # noqa: E402

u32 = ctypes.windll.user32
BM_CLICK = 0x00F5
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
MK_LBUTTON = 0x0001
GWL_EXSTYLE = -20
WS_EX_LAYERED = 0x00080000
WS_EX_TRANSPARENT = 0x00000020
WS_EX_NOACTIVATE = 0x08000000

EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
u32.EnumWindows.argtypes = [EnumProc, wintypes.LPARAM]
u32.EnumChildWindows.argtypes = [wintypes.HWND, EnumProc, wintypes.LPARAM]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongPtrW.restype = ctypes.c_longlong
u32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.IsWindowVisible.argtypes = [wintypes.HWND]


def text(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def pid_of(h):
    p = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def exstyle(h):
    return u32.GetWindowLongPtrW(h, GWL_EXSTYLE) & 0xFFFFFFFF


def top_windows(pid):
    out = []

    def cb(h, lp):
        if pid_of(h) == pid and u32.IsWindowVisible(h):
            out.append(h)
        return True

    u32.EnumWindows(EnumProc(cb), 0)
    return out


def child_with_text(parent, wanted):
    out = []

    def cb(h, lp):
        if text(h).strip() == wanted:
            out.append(h)
        return True

    u32.EnumChildWindows(parent, EnumProc(cb), 0)
    return out


def clip_map(root):
    """源帧文件名 -> clip 名，直接读 character.json，避免素材改版后误判。"""
    p = os.path.join(root, "characters", "娜波摩", "character.json")
    meta = json.load(open(p, encoding="utf-8"))
    out = {}
    for clip, files in meta["animations"].items():
        for f in files:
            out[os.path.basename(f)] = clip
    return out


def watch(pid, root, seconds=3.0):
    """不点按钮，纯观察正在连点的实例：自然注入是否持续驱动拍打动画。"""
    import numpy as np
    outdir = os.path.join(root, "tools", "_anim_watch")
    os.makedirs(outdir, exist_ok=True)
    for f in glob.glob(os.path.join(outdir, "*.png")):
        os.remove(f)
    ph = None
    for h in top_windows(pid):
        ex = exstyle(h) & 0xFFFFFFFF
        if ex & WS_EX_LAYERED and ex & WS_EX_NOACTIVATE:
            ph = h
    if not ph:
        print("FAIL 未找到角色分层窗口")
        return 3
    box = rect(ph)
    w, h = box[2] - box[0], box[3] - box[1]
    print("watch layered=0x%X rect=%s size=%dx%d" % (ph, box, w, h))
    fps = sorted(glob.glob(os.path.join(root, "characters", "娜波摩", "frames", "*.png")))
    srcs = []
    for p in fps:
        im = Image.open(p).convert("RGBA").resize((w, h), Image.LANCZOS)
        a = np.asarray(im)
        srcs.append((os.path.basename(p), a[:, :, :3].astype(np.float32), a[:, :, 3]))
    common = np.ones((h, w), dtype=bool)
    for _, _, alpha in srcs:
        common &= alpha > 230
    n = int(seconds / 0.07)
    labels = []
    for i in range(n):
        im = ImageGrab.grab(bbox=box, include_layered_windows=True)
        arr = np.asarray(im.convert("RGB")).astype(np.float32)
        scores = [float(np.abs(arr[common] - rgb[common]).mean()) for _, rgb, _ in srcs]
        k = int(np.argmin(scores))
        labels.append(srcs[k][0][:-4])
        if i < 6:
            im.save(os.path.join(outdir, "%02d_%s.png" % (i, labels[-1])))
        time.sleep(0.07)
    cmap = clip_map(root)
    slap = sum(cmap.get(f + ".png", "idle") != "idle" for f in labels)
    print("frames=%d slap=%d idle=%d sequence=%s"
          % (len(labels), slap, len(labels) - slap, " ".join(labels)))
    print("VERDICT:", "PASS 连点注入持续驱动角色拍打" if slap >= 2 else "FAIL 未观察到自然拍打")
    return 0 if slap >= 2 else 1


def main():
    if len(sys.argv) < 2:
        print("usage: verify_pet_anim.py <pid> [log] | verify_pet_anim.py watch <pid>")
        return 2
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if sys.argv[1] == "watch":
        return watch(int(sys.argv[2]), root)
    pid = int(sys.argv[1])
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    outdir = os.path.join(root, "tools", "_anim_frames")
    os.makedirs(outdir, exist_ok=True)
    for f in glob.glob(os.path.join(outdir, "*.png")):
        os.remove(f)

    wins = top_windows(pid)
    print("PID", pid, "visible top-level windows:", len(wins))
    main_hwnd = None
    pet_hwnd = None
    for h in wins:
        t, c, r, ex = text(h), cls(h), rect(h), exstyle(h)
        flags = []
        if ex & WS_EX_LAYERED:
            flags.append("LAYERED")
        if ex & WS_EX_TRANSPARENT:
            flags.append("TRANSPARENT")
        if ex & WS_EX_NOACTIVATE:
            flags.append("NOACTIVATE")
        print("  hwnd=0x%X class=%-34s rect=%s ex=%s title=%r"
              % (h, c, r, "+".join(flags) or "-", t[:60]))
        if "WuwaTappo" in t and "融合版" in t:
            main_hwnd = h
        elif ex & WS_EX_LAYERED and ex & WS_EX_NOACTIVATE:
            pet_hwnd = h

    if main_hwnd is None or pet_hwnd is None:
        print("FAIL: 未同时找到主窗体与角色窗口")
        return 3
    btns = child_with_text(main_hwnd, "测试一次")
    if not btns:
        print("FAIL: 未找到「测试一次」按钮")
        return 4
    btn = btns[0]
    # 提权/后台启动的实例可能处于最小化状态；最小化窗体上的 Button 收不到 BM_CLICK，
    # 先恢复并置前，再发送点击。
    u32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
    u32.SetForegroundWindow.argtypes = [wintypes.HWND]
    u32.ShowWindow(main_hwnd, 9)          # SW_RESTORE
    u32.SetForegroundWindow(main_hwnd)
    time.sleep(0.4)
    print("main=0x%X pet=0x%X button=0x%X main_rect=%s pet_rect=%s"
          % (main_hwnd, pet_hwnd, btn, rect(main_hwnd), rect(pet_hwnd)))

    l, t, r, b = rect(pet_hwnd)
    box = (l, t, r, b)
    w, h = r - l, b - t
    print("capture box", box, "size", w, h)

    def grab(tag):
        im = ImageGrab.grab(bbox=box, include_layered_windows=True)
        im.save(os.path.join(outdir, tag + ".png"))

    # 基线：确认静止状态下只有 idle 帧
    for i in range(6):
        grab("before_%02d" % i)
        time.sleep(0.07)
    logp = sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, "dist_new", "WtClicker.log")
    try:
        with open(logp, "rb") as f:
            pos = f.seek(0, 2)
    except OSError:
        pos = 0
    # 「测试一次」是 RoundedButton 自绘控件（WindowsForms10.Window.8，不是原生
    # BUTTON），BM_CLICK 对它无效；改用一对鼠标按下/抬起消息触发 Click 事件。
    bl, bt, br, bb = rect(btn)
    bw, bh = br - bl, bb - bt
    x, y = int(bw / 2), int(bh / 2)
    lp = (y << 16) | (x & 0xFFFF)
    t0 = time.time()
    u32.SendMessageW(btn, WM_LBUTTONDOWN, MK_LBUTTON, lp)
    u32.SendMessageW(btn, WM_LBUTTONUP, 0, lp)
    print("mouse click sent at (%d,%d) of %dx%d, returned after %.3fs"
          % (x, y, bw, bh, time.time() - t0))
    # v3 拍打序列 4 帧 @12fps = 333ms，采样 2 秒足够覆盖
    for i in range(30):
        grab("after_%02d" % i)
        time.sleep(0.06)
    try:
        with open(logp, "rb") as f:
            f.seek(pos)
            added = f.read().decode("utf-8", errors="replace")
        print("LOG AFTER CLICK:", " | ".join(added.strip().splitlines()[-4:]) or "(无新增日志)")
    except OSError as ex:
        print("LOG READ FAIL", ex)

    # ---------------- 与源帧比对 ----------------
    fps = sorted(glob.glob(os.path.join(root, "characters", "娜波摩", "frames", "*.png")))
    if len(fps) < 10:
        print("FAIL: 源帧不足", len(fps))
        return 5
    srcs = []
    for p in fps:
        im = Image.open(p).convert("RGBA").resize((w, h), Image.LANCZOS)
        a = np.asarray(im)
        srcs.append((os.path.basename(p), a[:, :, :3].astype(np.float32), a[:, :, 3]))
    common = np.ones((h, w), dtype=bool)
    for _, _, alpha in srcs:
        common &= alpha > 230
    print("common opaque mask pixels:", int(common.sum()), "/", w * h)
    if common.sum() < 500:
        print("FAIL: 掩膜过小，抓帧可能没拍到角色")
        return 6

    def classify(path):
        arr = np.asarray(Image.open(path).convert("RGB")).astype(np.float32)
        scores = [float(np.abs(arr[common] - rgb[common]).mean()) for _, rgb, _ in srcs]
        k = int(np.argmin(scores))
        return k, srcs[k][0], scores[k]

    for phase in ("before", "after"):
        rows = []
        for p in sorted(glob.glob(os.path.join(outdir, phase + "_*.png"))):
            k, name, sc = classify(p)
            rows.append("%s:%s(%.1f)" % (os.path.basename(p)[:-4], name[:-4], sc))
        print(phase.upper(), " ".join(rows))

    cmap = clip_map(root)
    before = [cmap.get(classify(p)[1], "idle") for p in sorted(glob.glob(os.path.join(outdir, "before_*.png")))]
    after = [cmap.get(classify(p)[1], "idle") for p in sorted(glob.glob(os.path.join(outdir, "after_*.png")))]
    nb = sum(c != "idle" for c in before)
    na = sum(c != "idle" for c in after)
    print("RESULT idle_before=%d/%d nonidle_before=%d nonidle_after=%d/%d"
          % (len(before) - nb, len(before), nb, na, len(after)))
    print("after clips:", " ".join(after[:20]))
    ok = nb == 0 and na >= 2
    print("VERDICT:", "PASS 角色拍打动画已由测试注入驱动" if ok else "FAIL 未观察到拍打帧")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
