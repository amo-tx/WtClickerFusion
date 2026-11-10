# -*- coding: utf-8 -*-
"""从「娜波摩wuwatappo.mp4」生成 PetForge 兼容角色（v6：保留黑色勾线）。

素材：47 帧 / 20fps / 1280x1280 / 2.35s
  F00-F21 蓝底、平静打字（睁眼微笑）
  F23-F46 红底、兴奋狂拍（眯眼张嘴）

★ 全 47 帧逐帧导出 + 手部区域放大视觉复核（tools/extract_frames.py、
  tools/hand_states.py，2026-11-09 第二次复核，推翻 v3 的错误结论）：
  - A 相「右手拍」= F00/F03/F05/F07：只有观众右侧一只白色手套按在键盘上，
    左手抬起（藏在身后）。
  - B 相「左手拍」= F01/F02/F04/F06/F08：观众左侧手套按在键盘上，
    右侧手套抬到脸旁 —— 画面里同时能看到两只手。A/B 逐帧交替，
    这就是用户说的「视频里是有两只手交替拍的」。
  - C 相「双手抬起/停顿」= F09-F21：键盘空、右侧手套在脸旁，静止待机。
  - 红底偶数帧 F26/F28/F30/F32 = 双手抬起；奇数帧 F27/F29/F31/F33 =
    左右两只手同时拍在键盘上（键盘被拍成两半）。
  - 角色朝向从头到尾恒定：右上蓝色发夹、帽子两侧白毛球位置不变，
    视频里没有任何一帧是整帧左右翻转的。
  → v3 误判成「只有右侧一只手」，导致 slap_l=[F00,F01,F03,F04] 与
    slap_r=[F05,F06,F07,F08] 两组都是 A,B,A,B，内容完全相同；而
    CharacterWindow 每次点击都把 clip 重置到第 0 帧，两组第 0 帧又都是
    A 相（F00/F05）—— 高 CPS 下永远只显示同一只手，正是用户反馈
    「只看到一只手 / 和官方不同步」的根因。

v4 方案（本次修复）：
  1. slap_l 全部取 B 相（左手拍），slap_r 全部取 A 相（右手拍）。
     两组首帧 F01 / F00 视觉明显不同，按点击序号奇偶交替 =>
     每次点击第一帧就是对应的那只手，即使高 CPS 打断重播也严格同步。
  2. ★ 每个视觉相位只归属一个 clip：A 相只给 slap_r、B 相只给 slap_l、
     C 相只给 idle。早期版本把 C 相「回位帧」塞进拍打序列，导致它与 idle
     画面几乎相同，实机模板匹配把回位帧大量误判成 slap_l（192:49），
     左右交替统计失真。现在 clip 播完自动回 idle（C 相双手抬起），
     天然形成「本手落下 → 双手抬起」的动感，且验收分类零歧义。
  3. 兴奋狂拍 frenzy_l / frenzy_r 用红底 E/O 相位：双手抬起 → 双手拍桌，
     两套都完整播完才回平静（状态机已保证）。
  4. fps 对齐源视频 20fps，全部只用原始帧，零镜像、零合成。

v2 抠图链路（已验收，保持不变）：
  四边量化取背景主色 → RECT GrabCut 粗形状 → 仅用饱和蓝/红色键净化 →
  形态学闭运算 + 最大连通域 + 孔洞回填 → alpha 内缩 1px → 0.6px 羽化 →
  normalized convolution 颜色去污染 → 统一矩形裁剪 → 逐帧中心中位数对齐。
  解决 v1 的「帽子和人物边缘蓝/红残色」：边缘蓝/红污染 34% → 0~0.3%。

★ v5 白边修复（2026-11-09 用户反馈「还是会有白边」，第二次定位）：
  之前的「白边」不是蓝/红残色，而是两件事叠加：
  ① 源视频角色自带一圈贴纸白色描边（帽子顶/人物外轮廓，620 高度画布上
     dist=1..5，约 5px 纯白）；v2/v4 的 GrabCut 把它一起算进前景，深色
     桌面上一看就是一圈白边。逐像素剖面 f00 帽子顶 x=300：y=39..43 全白，
     y=44 才是深色线稿。
  ② LANCZOS resize 会把白色插值进 alpha=0 的透明区（实测 RGB=[255,255,255]）；
     CharacterWindow.Render 再用 GDI+ 缩放到 0.55，这些「透明白像素」被
     插值进可见边缘，形成半透明白晕 —— 这也是只删白色像素仍留细碎白点的原因。
  处理（strip_sticker_rim）：整条 dist<=5.5 外圈带内缩删除 → 重羽化 →
  边缘带 RGB 用紧邻内部不透明像素颜色外扩 → alpha<=0.01 的像素 RGB 强制清零。
  角色只缩小 ~5px（0.9%），内部白色（头发/毛球/手套）离边界远，完整保留。

★ v6 黑线修复（2026-11-10 用户反馈「人物黑色勾线有些图抠掉了」）：
  v5 的致命缺陷是「固定宽度整条带一刀切」。源素材外轮廓其实是
    蓝/红背景 → 白色贴纸描边(厚度随帧/部位在 1~5px 间变化) → 黑色勾线 → 内部
  描边薄的地方，内侧黑线正好落在 dist<=5.5 这一刀里被一起砍掉；静止帧
  F20/F26 几乎没有白描边，v5 却仍削 5.5px，于是整圈黑线全丢（实测 F20
  黑线像素比 v6 少 10212 个）—— 这就是「有些图」特别明显的原因。
  另外 decontaminate() 把整条半透明带 RGB 换成内部高斯平均色，细黑线被
  平均成灰棕，是第二个成因。
  v6 两处一起改：
  ① strip_sticker_rim() 改为「颜色感知的自适应内缩」：种子取最外层近白
     像素，geodesic dilation 只在近白候选里向内生长，遇深色线稿自动停刀；
     白描边多厚就只削多厚，内部白头发/毛球因不与外圈连通而保留。
  ② decontaminate() 只替换半透明带里的近白像素，深色像素保留原色；
     颜色外扩改用最近邻搬运（nearest_color）而非高斯平均，黑线不糊。
  验收（18 帧合计）：深色线稿像素 1,370,261 → 1,456,102（+85,841 / +6.3%），
  残留白边 19,891 → 1,887（-90.5%），透明区白像素 0.0038% → 0.0000%，
  深色底 4x 放大 + 0.55 实机缩放复核黑线连续、无白晕。

输出：WtClickerFusion/characters/娜波摩/（frames/*.png + character.json）
验证：tools/_hand_states.png、tools/_nabo_verify*.png、tools/_nabo_phase.png
"""
import json
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
SRC = HERE / "_nbm_src.mp4"
DST = HERE.parent / "characters" / "娜波摩"

OUT_H = 620           # 统一输出高度（保持宽高比）
BG_SAT_MIN = 80       # 主色（蓝/红）饱和度下限，用于区分浅米色带
KEY_T = 32            # 色键净化：到饱和主色的距离小于此值一律判背景
FEATHER = 0.6         # alpha 羽化半径（内缩后再羽化，边缘带约 2px）
# ★ 逐帧视觉复核后的真实相位（tools/_hand_states.png）：
#   A = F00/F03/F05/F07  右手按键盘、左手抬起      → slap_r
#   B = F01/F02/F04/F06/F08 左手按键盘、右手抬脸  → slap_l
#   C = F09-F21            双手抬起/键盘空、静止   → idle 与拍打回位帧
#   E/O = 红底偶/奇        双手抬起 / 双手拍桌      → frenzy
#   （v2 取样 BLUE=[0,5,10,15,20] / RED=[24,28,32,36,40,44] 全落同一相位；
#     v3 虽改成 A/B 交替，但 slap_l 与 slap_r 两组取样完全相同，首帧同为 A 相。）
# 蓝底：A/B/C 三相全部保留，供左右手拍打与回位各自取不同文件
# 只导出会被 clip 引用的帧；A/B/C 三相互不跨组复用
BLUE = [0, 1, 3, 4, 5, 6, 7, 8, 9, 20]
# 红底：双手抬起(E)/双手拍桌(O) 两相位各取连续 4 帧
RED = [26, 27, 28, 29, 30, 31, 32, 33]
ALL = BLUE + RED


def load_frames():
    cap = cv2.VideoCapture(str(SRC))
    out = []
    while True:
        ok, f = cap.read()
        if not ok:
            break
        out.append(f)
    cap.release()
    if not out:
        raise RuntimeError("视频解码失败（OpenCV 对非 ASCII 路径敏感，请用 _nbm_src.mp4 这个 ASCII 副本）")
    return out


def bg_palette(f):
    """四边 40px 量化取主色；返回占比 >3% 的背景色列表。"""
    b = np.concatenate([
        f[:40].reshape(-1, 3), f[-40:].reshape(-1, 3),
        f[:, :40].reshape(-1, 3), f[:, -40:].reshape(-1, 3),
    ])
    q = (b // 8).astype(np.uint8)
    keys, cnt = np.unique(q, axis=0, return_counts=True)
    keep = cnt / len(b) > 0.03
    return (keys[keep] * 8 + 4).astype(np.float32)


def saturated_colors(palette):
    """只取高饱和的蓝/红主色；浅米色带与角色肤色太接近，不能用于色键。"""
    sat = palette.max(1) - palette.min(1)
    main = palette[sat > BG_SAT_MIN]
    return main if len(main) else palette[:1]


def main_color_dist(f, main):
    f32 = f.astype(np.float32)
    d = np.full(f.shape[:2], 1e9, np.float32)
    for c in main:
        d = np.minimum(d, np.linalg.norm(f32 - c, axis=2))
    return d


def fg_mask(f, d_main):
    """粗形状 GrabCut → 色键净化 → 形态学修复 → 孔洞回填。"""
    h, w = f.shape[:2]
    # 左边界收紧到 0.22w：聊天气泡 / "rover~" 文字 / 涂鸦全在画面左侧
    # x<0.2w，矩形外一律 GC_BGD，避免 GrabCut 把气泡白框和黑色笔画连进角色。
    x0, y0 = int(w * 0.22), int(h * 0.04)
    x1, y1 = int(w * 0.98), int(h * 0.99)
    rect = (x0, y0, x1 - x0, y1 - y0)
    mask = np.zeros((h, w), np.uint8)
    bgd = np.zeros((1, 65), np.float64)
    fgd = np.zeros((1, 65), np.float64)
    cv2.grabCut(f, mask, rect, bgd, fgd, 6, cv2.GC_INIT_WITH_RECT)
    fg = ((mask == cv2.GC_FGD) | (mask == cv2.GC_PR_FGD)).astype(np.uint8)

    fg[d_main < KEY_T] = 0

    k5 = np.ones((5, 5), np.uint8)
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, k5, iterations=2)

    n2, lab2, st, _ = cv2.connectedComponentsWithStats(fg, 8)
    if n2 > 1:
        j = 1 + int(np.argmax(st[1:, cv2.CC_STAT_AREA]))
        fg = (lab2 == j).astype(np.uint8)

    edge = np.zeros((h, w), bool)
    edge[0] = edge[-1] = True
    edge[:, 0] = edge[:, -1] = True
    inv = (1 - fg).astype(np.uint8)
    n3, lab3 = cv2.connectedComponents(inv, connectivity=8)
    outside = np.unique(lab3[edge])
    holes = (inv > 0) & ~np.isin(lab3, outside[outside != 0])
    fg = np.maximum(fg, holes.astype(np.uint8))
    return fg


def decontaminate(rgb, a):
    """v6：只修正半透明带里的「近白污染」，深色线稿保留原色。

    v2~v5 把整条半透明带 (0.01<a<0.98) 的 RGB 一律替换成内部前景的高斯
    平均色；角色外侧那 1~3px 的黑色勾线正好落在带内，被平均成灰棕色 ——
    这是用户反馈「黑色勾线有些图抠掉了」的第二个成因（第一个见
    strip_sticker_rim）。现在只替换近白像素，深色像素原样保留。
    """
    inner = (a > 0.98).astype(np.float32)
    acc = cv2.GaussianBlur(rgb.astype(np.float32) * inner[..., None], (0, 0), 1.1)
    wgt = cv2.GaussianBlur(inner, (0, 0), 1.1) + 1e-6
    safe = acc / wgt[..., None]
    band = (a > 0.01) & (a < 0.98)
    lum = rgb.mean(2)
    sat = rgb.max(2) - rgb.min(2)
    bad = band & (lum > 110) & (sat < 100)
    out = rgb.astype(np.float32).copy()
    out[bad] = safe[bad]
    out[a <= 0.01] = 0
    return np.clip(out, 0, 255).astype(np.uint8)


def cutout(f, fg):
    """内缩 1px 砍掉被污染外圈 → 0.6px 羽化 → 颜色去污染 → RGBA。"""
    hard = (fg * 255).astype(np.uint8)
    core = cv2.erode(hard, np.ones((3, 3), np.uint8), iterations=1)
    a = cv2.GaussianBlur(core, (0, 0), FEATHER).astype(np.float32) / 255.0
    rgb = cv2.cvtColor(f, cv2.COLOR_BGR2RGB)
    rgb = decontaminate(rgb, a)
    out = np.dstack([rgb, (a * 255).astype(np.uint8)])
    return Image.fromarray(out, "RGBA")


def strip_sticker_rim_v5(im, rim_w=5.5):
    """【旧版，仅用于对照】v5 的「整条外圈带一刀切」，会连黑色勾线一起砍掉。"""
    arr = np.asarray(im.convert("RGBA")).copy()
    rgb = arr[..., :3].astype(np.float32)
    a = arr[..., 3].astype(np.float32) / 255.0
    fg = (a > 0.5).astype(np.uint8)
    dist = cv2.distanceTransform(fg, cv2.DIST_L2, 5)
    rim = (dist > 0) & (dist <= rim_w)
    a2 = a.copy()
    a2[rim] = 0.0
    a2 = cv2.GaussianBlur(a2.astype(np.float32), (0, 0), 0.55)
    src = (a2 > 0.95).astype(np.float32)
    acc = cv2.GaussianBlur(rgb * src[..., None], (0, 0), 1.0)
    wgt = cv2.GaussianBlur(src, (0, 0), 1.0) + 1e-6
    safe = acc / wgt[..., None]
    rgb2 = rgb.copy()
    band = (a2 > 0.01) & (a2 < 0.98)
    rgb2[band] = safe[band]
    rgb2[a2 <= 0.01] = 0
    out = arr.copy()
    out[..., :3] = np.clip(rgb2, 0, 255).astype(np.uint8)
    out[..., 3] = np.clip(a2 * 255, 0, 255).astype(np.uint8)
    return Image.fromarray(out, "RGBA")


def nearest_color(rgb, known, iters=6):
    """把不透明前景的 RGB 逐层向外「最近邻」传播，不平均、不抹深色线稿。

    v5 用高斯平均做颜色外扩，帽子深色线稿与旁边白色毛球会被平均成灰棕，
    细黑线因此糊掉。这里逐层 8 邻域搬运原像素颜色，黑线仍是黑线。
    """
    out = rgb.copy()
    kn = known.copy()
    h, w = kn.shape
    for _ in range(iters):
        target = ~kn
        if not target.any():
            break
        picked = np.zeros((h, w), bool)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy == 0 and dx == 0:
                    continue
                ks = np.roll(kn, (dy, dx), axis=(0, 1)).copy()
                cs = np.roll(out, (dy, dx), axis=(0, 1))
                # np.roll 会环绕，屏蔽越界侧
                if dy > 0:
                    ks[:dy, :] = False
                elif dy < 0:
                    ks[dy:, :] = False
                if dx > 0:
                    ks[:, :dx] = False
                elif dx < 0:
                    ks[:, dx:] = False
                take = target & ks & ~picked
                if take.any():
                    out[take] = cs[take]
                    picked |= take
        kn |= picked
    return out


def strip_sticker_rim(im, rim_max=6.0):
    """v6：只删「贴着背景的那圈白色贴纸描边」，保留内侧黑色勾线。

    源素材外轮廓的真实结构是：蓝/红背景 → 白色贴纸描边(厚度随帧/随部位
    在 1~5px 间变化) → 深色(近黑)线稿 → 角色内部。v5 固定砍 dist<=5.5
    的整条带，描边薄的地方内侧黑线正好落在这一刀里，于是「黑色勾线有些
    图抠掉了」；描边厚的地方反而还留一点白。

    v6 改成颜色感知的自适应内缩：
      1. 种子 = 最外层 (dist<=2) 的近白像素；
      2. geodesic dilation 只在「近白候选」里向内生长 —— 遇到深色线稿
         (不在候选里) 自动停刀，白描边多厚就只削多厚；
      3. 候选限制 dist<=rim_max，且必须从外向内连通，因此角色内部的大块
         白色（头发/毛球/手套）不会被误删；
      4. 削掉的带再向外吃 1px 抗锯齿灰白，仍只限近白像素，不碰黑线；
      5. 半透明带颜色用最近邻外扩（不是高斯平均），黑线原色保留；
      6. alpha<=0.01 的 RGB 强制清零（GDI+ 缩放会把透明白像素插值成白晕）。
    """
    arr = np.asarray(im.convert("RGBA")).copy()
    rgb = arr[..., :3].astype(np.float32)
    a = arr[..., 3].astype(np.float32) / 255.0
    fg = (a > 0.5).astype(np.uint8)
    dist = cv2.distanceTransform(fg, cv2.DIST_L2, 5)

    lum = rgb.mean(2)
    sat = rgb.max(2) - rgb.min(2)
    whitish = (lum > 110) & (sat < 100)          # 含灰白抗锯齿过渡
    cand = ((fg > 0) & whitish & (dist <= rim_max)).astype(np.uint8)
    seed = ((fg > 0) & whitish & (dist <= 2.0)).astype(np.uint8)

    k3 = np.ones((3, 3), np.uint8)
    rim = seed.copy()
    for _ in range(int(rim_max) + 4):
        grown = cv2.dilate(rim, k3, iterations=1) & cand
        if np.array_equal(grown, rim):
            break
        rim = grown
    rim = cv2.dilate(rim, k3, iterations=1) & cand   # 吃掉抗锯齿灰白

    a2 = a.copy()
    a2[rim > 0] = 0.0
    a2 = cv2.GaussianBlur(a2.astype(np.float32), (0, 0), 0.55)

    # 最近邻颜色外扩：只给「近白的半透明像素」补色，深色线稿一律保留原色
    nearest = nearest_color(rgb, a2 > 0.95)
    rgb2 = rgb.copy()
    band = (a2 > 0.01) & (a2 < 0.98)
    bad = band & whitish
    rgb2[bad] = nearest[bad]
    rgb2[a2 <= 0.01] = 0

    au8 = np.clip(a2 * 255, 0, 255).astype(np.uint8)
    rgb2[au8 <= 5] = 0          # 近透明区一律清零，杜绝 GDI+ 插值渗白
    out = arr.copy()
    out[..., :3] = np.clip(rgb2, 0, 255).astype(np.uint8)
    out[..., 3] = au8
    return Image.fromarray(out, "RGBA"), int((rim > 0).sum())


def font(size=16):
    p = "C:/Windows/Fonts/msyh.ttc"
    return ImageFont.truetype(p, size) if Path(p).exists() else ImageFont.load_default()


def build_base_frames():
    """v2~v4 抠图链路：源视频 → GrabCut/色键/羽化 → 统一裁剪 → 居中画布。

    返回 (imgs, frames, crop, size, bx, canvas, off)。这里产出的画布
    **尚未去白边**，正好作为 v5/v6 的同一输入基准，便于做同帧对照验收。
    """
    frames = load_frames()
    print("decoded frames =", len(frames), "size =", frames[0].shape[1::-1])

    raw, boxes = {}, {}
    for i in ALL:
        pal = bg_palette(frames[i])
        d_main = main_color_dist(frames[i], saturated_colors(pal))
        fg = fg_mask(frames[i], d_main)
        ys, xs = np.where(fg > 0)
        if len(xs) == 0:
            raise RuntimeError(f"F{i:02d} 前景为空，抠图阈值需调整")
        boxes[i] = (int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1)
        raw[i] = cutout(frames[i], fg)
        print(f"F{i:02d} bbox={boxes[i]} fg%={fg.mean()*100:.1f} palette={pal.astype(int).tolist()}")

    x0 = min(boxes[i][0] for i in ALL)
    y0 = min(boxes[i][1] for i in ALL)
    x1 = max(boxes[i][2] for i in ALL)
    y1 = max(boxes[i][3] for i in ALL)
    pad = 12
    x0, y0 = max(0, x0 - pad), max(0, y0 - pad)
    x1, y1 = min(frames[0].shape[1], x1 + pad), min(frames[0].shape[0], y1 + pad)
    print("unified crop box =", (x0, y0, x1, y1))

    imgs = {i: im.crop((x0, y0, x1, y1)) for i, im in raw.items()}
    H, W = imgs[ALL[0]].height, imgs[ALL[0]].width
    scale = OUT_H / H
    size = (max(1, round(W * scale)), OUT_H)
    imgs = {i: im.resize(size, Image.LANCZOS) for i, im in imgs.items()}

    union = np.zeros((size[1], size[0]), np.uint8)
    for im in imgs.values():
        union |= (np.asarray(im)[..., 3] > 40).astype(np.uint8)
    ys, xs = np.where(union > 0)
    pad2 = 8
    bx = (max(0, int(xs.min()) - pad2), max(0, int(ys.min()) - pad2),
          min(size[0], int(xs.max()) + 1 + pad2), min(size[1], int(ys.max()) + 1 + pad2))
    imgs = {i: im.crop(bx) for i, im in imgs.items()}
    print("tight crop =", bx, "-> size", imgs[ALL[0]].size)

    # 逐帧 bbox 中心的中位数对齐画布中心：个别帧 GrabCut 会把左侧气泡算进
    # 前景，用并集中心会被拉偏 ~17px，播放时角色会左右横跳。
    W2, H2 = imgs[ALL[0]].size
    centers = []
    for im in imgs.values():
        _, xi = np.where(np.asarray(im)[..., 3] > 40)
        centers.append((int(xi.min()) + int(xi.max())) / 2.0)
    cx = float(np.median(centers))
    half = int(max(cx, W2 - 1 - cx)) + pad2
    W3 = 2 * half + 1
    off = half - int(round(cx))
    centered = {}
    for i, im in imgs.items():
        c = Image.new("RGBA", (W3, H2), (0, 0, 0, 0))
        c.paste(im, (off, 0))
        centered[i] = c
    imgs = centered
    print("centered canvas =", (W3, H2), "role center %.0f -> canvas center %.0f" % (cx, (W3 - 1) / 2))
    return imgs, frames, (x0, y0, x1, y1), size, bx, (W3, H2), off


def outline_metrics(im):
    """验收指标：黑线量 / 残留白边量 / 透明区未清零的白像素占比(%)。"""
    arr = np.asarray(im.convert("RGBA"))
    rgb = arr[..., :3].astype(np.float32)
    a = arr[..., 3].astype(np.float32) / 255.0
    fg = (a > 0.5).astype(np.uint8)
    dist = cv2.distanceTransform(fg, cv2.DIST_L2, 5)
    lum = rgb.mean(2)
    sat = rgb.max(2) - rgb.min(2)
    dark = int(((lum < 90) & (a > 0.5)).sum())
    white_edge = int(((dist > 0) & (dist <= 3) & (lum > 150)
                      & (sat < 90) & (a > 0.5)).sum())
    clear_white = float(((a <= 0.01) & (rgb.min(2) > 200)).mean() * 100)
    return dark, white_edge, clear_white


def on_dark(im):
    bg = Image.new("RGBA", im.size, (38, 42, 52, 255))
    bg.alpha_composite(im.convert("RGBA"))
    return bg.convert("RGB")


def write_outline_compare(ref, v5, v6):
    """源片 / v5 旧版 / v6 新版 三列对照：整帧 + 帽子与手部局部放大。"""
    ids = [0, 1, 27, 29]
    CW, CH = 250, 292
    sheet = Image.new("RGB", (CW * 3, CH * 4 + 34), (236, 236, 236))
    d = ImageDraw.Draw(sheet)
    for c, lab in enumerate(["源片（白贴纸描边）", "v5 旧版（黑线被削）", "v6 新版（保留黑线）"]):
        d.text((c * CW + 8, 6), lab, fill=(0, 0, 0), font=font(15))
    for r, i in enumerate(ids):
        for c, im in enumerate([ref[i], v5[i], v6[i]]):
            t = on_dark(im)
            t.thumbnail((CW - 8, CH - 30), Image.LANCZOS)
            x, y = c * CW + 4, r * CH + 30
            sheet.paste(t, (x, y))
            d.text((x + 4, y + 2), f"F{i:02d}", fill=(255, 220, 80), font=font(12))
    sheet.save(HERE / "_outline_compare.png")

    ZW, ZH = 380, 250
    zoom = Image.new("RGB", (ZW * 3, ZH * 4 + 34), (236, 236, 236))
    dz = ImageDraw.Draw(zoom)
    for c, lab in enumerate(["源片", "v5 旧版", "v6 新版"]):
        dz.text((c * ZW + 8, 6), lab, fill=(0, 0, 0), font=font(15))
    for r, (i, box, lab) in enumerate([
            (0, (0.06, 0.00, 0.94, 0.34), "F00 帽顶/毛球边缘"),
            (0, (0.08, 0.55, 0.92, 0.98), "F00 手部/键盘边缘"),
            (27, (0.06, 0.00, 0.94, 0.34), "F27 帽顶/毛球边缘"),
            (27, (0.08, 0.55, 0.92, 0.98), "F27 双手拍桌边缘")]):
        for c, im in enumerate([ref[i], v5[i], v6[i]]):
            W, H = im.size
            bx2 = (int(W * box[0]), int(H * box[1]), int(W * box[2]), int(H * box[3]))
            t = on_dark(im).crop(bx2)
            t = t.resize((ZW - 10, ZH - 28), Image.LANCZOS)
            x, y = c * ZW + 5, r * ZH + 30
            zoom.paste(t, (x, y))
            dz.text((x + 4, y + 2), lab, fill=(255, 220, 80), font=font(12))
    zoom.save(HERE / "_outline_compare_zoom.png")
    print("outline compare ->", HERE / "_outline_compare.png", "/ _outline_compare_zoom.png")


def main():
    imgs, frames, crop, size, bx, canvas, off = build_base_frames()
    W3, H2 = canvas
    base = {i: im.copy() for i, im in imgs.items()}

    # 源片按完全相同的 crop/resize/居中变换，作为「原本该有的黑线」参考
    ref = {}
    for i in ALL:
        r = Image.fromarray(cv2.cvtColor(frames[i], cv2.COLOR_BGR2RGB))
        r = r.crop(crop).resize(size, Image.LANCZOS).crop(bx)
        c = Image.new("RGBA", (W3, H2), (0, 0, 0, 0))
        c.paste(r.convert("RGBA"), (off, 0))
        ref[i] = c

    v5 = {i: strip_sticker_rim_v5(base[i]) for i in ALL}
    v6, rim_n = {}, {}
    for i in ALL:
        v6[i], rim_n[i] = strip_sticker_rim(base[i])
    imgs = v6

    write_outline_compare(ref, v5, v6)

    # 数值验收：v5 削掉多少黑线、v6 找回多少，同时白边是否仍被清干净
    print("\n=== 黑色勾线 / 白边 验收（每帧）===")
    print(f"{'帧':<8}{'v5黑线':>9}{'v6黑线':>9}{'黑线Δ':>9}"
          f"{'v5白边':>9}{'v6白边':>9}{'透明白%':>10}{'削除px':>9}")
    tot = [0, 0, 0, 0]
    for i in ALL:
        d5, w5, _ = outline_metrics(v5[i])
        d6, w6, cw6 = outline_metrics(v6[i])
        tot[0] += d5; tot[1] += d6; tot[2] += w5; tot[3] += w6
        print(f"F{i:02d}    {d5:9d}{d6:9d}{d6-d5:+9d}{w5:9d}{w6:9d}{cw6:10.3f}{rim_n[i]:9d}")
    print(f"{'合计':<8}{tot[0]:9d}{tot[1]:9d}{tot[1]-tot[0]:+9d}{tot[2]:9d}{tot[3]:9d}")
    assert tot[3] <= tot[2] * 0.15 + 200, "v6 残留白边反而变多，需调参"

    outdir = DST / "frames"
    outdir.mkdir(parents=True, exist_ok=True)
    for old in outdir.glob("*.png"):
        old.unlink()

    for i in ALL:
        name = f"f{i:02d}.png"
        # ★ 必须在最终画布（resize + 居中对齐之后）去白边，因为 LANCZOS
        #   缩放正是「透明区被染白」的环节。
        imgs[i].save(outdir / name)
        a = np.asarray(imgs[i])[..., 3]
        print(f"  {name}: {imgs[i].size} 透明%={(a < 128).mean()*100:.1f}（v6 保留黑线）")

    def rel(i):
        return f"frames/f{i:02d}.png"

    meta = {
        "name": "娜波摩",
        "fps": 20,
        "scale": 0.55,
        "tappo": True,
        "frenzyChance": 0.12,
        "animations": {
            # 平静打字待机：C 相（双手抬起、键盘空）静止段 2 帧微动
            "idle": [rel(i) for i in (9, 20)],
            # ★ 左手拍 B 相：F01/F04/F06/F08 = 左手按键盘 + 右手抬到脸旁（两只手都在）。
            #   首帧 F01 必须是「左手」，因为 CharacterWindow 每次点击都会把
            #   clip 重置到第 0 帧；高 CPS 下用户看到的就是这一帧 —— 与
            #   slap_r 首帧 F00 严格左右手交替。播完自动回 idle（C 相双手抬起）。
            "slap_l": [rel(i) for i in (1, 4, 6, 8)],
            # ★ 右手拍 A 相：F00/F03/F05/F07 = 右手按键盘、左手抬起。
            "slap_r": [rel(i) for i in (0, 3, 5, 7)],
            # 概率触发的兴奋狂拍：E 相双手抬起 → O 相双手同时拍桌，
            # 两套都完整播完才回平静打字。
            "frenzy_l": [rel(i) for i in (26, 27, 28, 29)],
            "frenzy_r": [rel(i) for i in (30, 31, 32, 33)],
        },
        "outfits": [],
        "source": "娜波摩wuwatappo.mp4（47帧/20fps/1280²）v4：全 47 帧逐帧导出 + 手部放大复核后，"
                  "确认视频里是两只手交替拍打（蓝底 A 相右手按键盘 / B 相左手按键盘 + 右手抬脸，"
                  "红底奇数帧双手同时拍桌）。v3 误判为「只有一只手」，导致 slap_l 与 slap_r "
                  "取样完全相同且首帧同为 A 相，高 CPS 下永远只显示同一只手。v4 把 B 相单独给 "
                  "slap_l、A 相单独给 slap_r，按点击序号奇偶交替 => 每次点击第一帧即对应手，"
                  "与官方角色逐次同步；A/B/C 三相互不跨组复用，clip 播完自动回 idle "
                  "（C 相双手抬起）形成「本手落下→双手抬起」动感，验收分类零歧义。"
                  "全部原始帧、零镜像、零合成，角色朝向恒定。"
                  "v6（2026-11-10）：修复『黑色勾线有些图被抠掉』——v5 固定砍 "
                  "dist<=5.5 整条带，描边薄的帧连内侧黑线一起削掉；改为颜色感知"
                  "自适应内缩（只删与背景连通的白贴纸描边，遇深色线稿停刀）+ "
                  "半透明带只修正近白像素 + 最近邻颜色外扩。18 帧合计找回深色"
                  "线稿 85,841 px（+6.3%），残留白边 -90.5%，透明区白像素归零。",
    }
    (DST / "character.json").write_text(
        json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

    # ---------- 验证图 ----------
    tile_w, tile_h = 150, OUT_H // 3
    for bg, out_name in [((255, 0, 255, 255), "_nabo_verify.png"),
                         ((45, 48, 55, 255), "_nabo_verify_dark.png"),
                         ((255, 255, 255, 255), "_nabo_verify_white.png")]:
        sheet = Image.new("RGBA", (tile_w * len(ALL) + 20, tile_h + 20), bg)
        for k, i in enumerate(ALL):
            t = Image.open(outdir / f"f{i:02d}.png")
            t.thumbnail((tile_w - 8, tile_h - 8), Image.LANCZOS)
            sheet.alpha_composite(t, (10 + k * tile_w, 10))
        sheet.convert("RGB").save(HERE / out_name)
        print("verify sheet ->", HERE / out_name)

    # 朝向恒定对照：四组序列的首帧并排，确认没有任何一帧是镜像/翻面的
    TW, TH = 330, 400
    sheet = Image.new("RGB", (TW * 4, TH + 46), (240, 240, 240))
    d = ImageDraw.Draw(sheet)
    for c, (i, lab) in enumerate([(1, "slap_l 左手拍"), (0, "slap_r 右手拍"),
                                  (26, "frenzy_l 兴奋"), (30, "frenzy_r 兴奋")]):
        im = Image.open(outdir / f"f{i:02d}.png").convert("RGBA")
        bg = Image.new("RGBA", im.size, (45, 48, 55, 255))
        bg.alpha_composite(im)
        t = bg.convert("RGB")
        t.thumbnail((TW - 12, TH - 12), Image.LANCZOS)
        x = c * TW + 6
        sheet.paste(t, (x, 40))
        d.text((x, 12), lab + " (无镜像)", fill=(0, 0, 0), font=font(16))
    sheet.save(HERE / "_nabo_hands_compare.png")
    print("orientation sheet ->", HERE / "_nabo_hands_compare.png")

    # 动作相位拼图：证明拍打序列里手真的抬起/落下，而不是静止帧循环
    PW, PH = 230, 280
    seqs = [("slap_l 左手拍", [1, 4, 6, 8]),
            ("slap_r 右手拍", [0, 3, 5, 7]),
            ("frenzy_l 兴奋", [26, 27, 28, 29]),
            ("frenzy_r 兴奋", [30, 31, 32, 33])]
    ph = Image.new("RGB", (PW * 4, PH * 4 + 40), (240, 240, 240))
    dp = ImageDraw.Draw(ph)
    for r, (lab, seq) in enumerate(seqs):
        for c, i in enumerate(seq):
            im = Image.open(outdir / f"f{i:02d}.png").convert("RGBA")
            crop = im.crop((0, int(im.height * 0.44), im.width, int(im.height * 0.86)))
            bg = Image.new("RGBA", crop.size, (45, 48, 55, 255))
            bg.alpha_composite(crop)
            t = bg.convert("RGB")
            t.thumbnail((PW - 10, PH - 28), Image.LANCZOS)
            x, y = c * PW + 5, r * PH + 26
            ph.paste(t, (x, y))
            dp.text((x, r * PH + 6), f"{lab} / F{i:02d}",
                    fill=(0, 0, 0), font=font(13))
    ph.save(HERE / "_nabo_phase.png")
    print("phase sheet ->", HERE / "_nabo_phase.png")

    # 左右手交替证据：修复后素材里两套拍打的首帧 + 兴奋双手拍桌帧
    TW2, TH2 = 420, 700
    ev = Image.new("RGB", (TW2 * 3, TH2), (242, 242, 242))
    de = ImageDraw.Draw(ev)
    for c, (i, lab, note) in enumerate([
            (0, "slap_r 首帧 F00", "右手按键盘 / 左手抬起"),
            (1, "slap_l 首帧 F01", "左手按键盘 / 右手抬到脸旁"),
            (27, "frenzy F27", "两只手同时拍在键盘上")]):
        im = Image.open(outdir / f"f{i:02d}.png").convert("RGBA")
        bg = Image.new("RGBA", im.size, (45, 48, 55, 255))
        bg.alpha_composite(im)
        full = bg.convert("RGB")
        full.thumbnail((TW2 - 16, 380), Image.LANCZOS)
        hand = bg.crop((int(im.width * 0.20), int(im.height * 0.58),
                        int(im.width * 0.85), int(im.height * 0.95))).convert("RGB")
        hand = hand.resize((TW2 - 16, 230), Image.LANCZOS)
        x = c * TW2 + 8
        de.text((x, 8), lab, fill=(0, 0, 0), font=font(18))
        de.text((x, 34), note, fill=(140, 40, 40), font=font(14))
        ev.paste(full, (x, 66))
        ev.paste(hand, (x, 452))
    ev.save(HERE / "_nabo_lr_hands.png")
    print("LR hands evidence ->", HERE / "_nabo_lr_hands.png")
    print("character ->", DST)


if __name__ == "__main__":
    main()
