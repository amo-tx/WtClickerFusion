# -*- coding: utf-8 -*-
"""去掉源视频自带的「贴纸白色描边」，保留角色内部白色（头发/毛球/手套）。

原理（v5）：
  源帧里角色外轮廓是「蓝/红背景 → 纯白描边(2~4px) → 深色线稿 → 角色内部」。
  v2/v4 的 GrabCut 把这条白描边一起算进前景，放到深色桌面就成一圈明显白边
  （用户反馈「还是会有白边」）。边缘带亮度剖面显示白边与内部白色一样亮，
  说明不是蓝/红残色，而是真正的白色像素。

  只对「距前景边界 <= RIM_W 且近白」的像素削 alpha；内部大块白色（头发、
  毛球、手套）离边界远，完全不受影响。削掉的区域再用「非白不透明前景」
  的 normalized convolution 把内侧线稿颜色外扩过去，避免留下半透明白晕。

用法：python strip_white_rim.py [--apply]
  不带 --apply 只生成对照图 _white_edge_trial.png / _white_edge_trial_zoom.png
  带 --apply 直接覆写 characters/娜波摩/frames/*.png（先备份到 _nabo_v4_backup）
"""
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
DST = HERE.parent / "characters" / "娜波摩" / "frames"
BG = (38, 42, 52, 255)
# ★ 逐像素剖面实测（f00 帽子顶 x=300）：y=39..43 全是纯白描边，dist=1..5，
#   y=44 才是深色线稿(dist=6)。RIM_W 必须 >=5 才能把白描边整条删干净；
#   3.2 只削掉一半，剩下的白像素会被颜色外扩当成「内部前景」又刷回边缘，
#   在深色桌面上表现为一串细碎白点。
RIM_W = 5.5          # 描边最大宽度（620 高度画布上的像素）
LUM_MIN = 150        # 近白亮度下限（含抗锯齿的灰白）
SAT_MAX = 75         # 近白饱和度上限（头发米白带一点黄，约 20~35）


def strip(im):
    arr = np.asarray(im.convert("RGBA")).copy()
    rgb = arr[..., :3].astype(np.float32)
    a = arr[..., 3].astype(np.float32) / 255.0

    fg = (a > 0.5).astype(np.uint8)
    dist = cv2.distanceTransform(fg, cv2.DIST_L2, 5)

    lum = rgb.mean(2)
    sat = rgb.max(2) - rgb.min(2)
    white = (lum > LUM_MIN) & (sat < SAT_MAX)

    # ★ 只删「白色像素」会留下半透明的抗锯齿残边，放大后是一串细碎白点
    #   （两轮试做放大图实锤）。改为把整条外圈带内缩：白描边连同它的
    #   灰白过渡一起删掉，深色线稿直接成为新边缘；角色只缩小 ~3px
    #   （620 高度画布的 0.5%），肉眼不可见，但边缘连续干净。
    rim = (dist > 0) & (dist <= RIM_W)
    a2 = a.copy()
    a2[rim] = 0.0
    a2 = cv2.GaussianBlur(a2.astype(np.float32), (0, 0), 0.55)

    # 颜色外扩：新边缘带的 RGB 全部取自紧邻的内部不透明像素（帽子处是深色
    #   线稿、毛球处是纯白），避免透明区纯黑被 GDI+ 缩放插值渗出灰边，
    #   也避免残留白描边 RGB 形成白晕。
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
    return Image.fromarray(out, "RGBA"), int(rim.sum())


def on_dark(im, size=None):
    im = im.convert("RGBA")
    if size:
        im = im.resize(size, Image.LANCZOS)
    bg = Image.new("RGBA", im.size, BG)
    bg.alpha_composite(im)
    return bg.convert("RGB")


def font(sz):
    p = "C:/Windows/Fonts/msyh.ttc"
    return ImageFont.truetype(p, sz) if Path(p).exists() else ImageFont.load_default()


def main():
    apply = "--apply" in sys.argv
    ids = [0, 1, 27]
    PW, PH = 340, 380
    sheet = Image.new("RGB", (PW * 3, PH * 2 + 56), (236, 236, 236))
    d = ImageDraw.Draw(sheet)

    stats = []
    for c, i in enumerate(ids):
        p = DST / f"f{i:02d}.png"
        im = Image.open(p).convert("RGBA")
        new, n = strip(im)
        stats.append((f"f{i:02d}.png", n))
        x = c * PW
        d.text((x + 8, 6), f"F{i:02d} 修复前（有白边）", fill=(0, 0, 0), font=font(16))
        t = on_dark(im)
        t.thumbnail((PW - 12, PH - 34), Image.LANCZOS)
        sheet.paste(t, (x + 6, 28))
        d.text((x + 8, PH + 28), f"F{i:02d} 修复后（削 {n} px）", fill=(0, 0, 0), font=font(16))
        t = on_dark(new)
        t.thumbnail((PW - 12, PH - 34), Image.LANCZOS)
        sheet.paste(t, (x + 6, PH + 50))
    sheet.save(HERE / "_white_edge_trial.png")
    print("trial ->", HERE / "_white_edge_trial.png")

    # 帽子顶部 + 右侧毛球放大对照（确认内部白色没被误削）
    im = Image.open(DST / "f00.png").convert("RGBA")
    new, _ = strip(im)
    a = np.asarray(im)[..., 3]
    ys, xs = np.where(a > 40)
    x0, y0, x1, y1 = int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())
    regions = [
        ("帽子顶部", (x0 + int((x1-x0)*.10), y0, x0 + int((x1-x0)*.80), y0 + int((y1-y0)*.24))),
        ("右侧毛球", (x0 + int((x1-x0)*.64), y0 + int((y1-y0)*.10), x1, y0 + int((y1-y0)*.50))),
    ]
    CW, CH = 380, 300
    zoom = Image.new("RGB", (CW * 2, CH * 2 + 50), (236, 236, 236))
    dz = ImageDraw.Draw(zoom)
    for r, (lab, box) in enumerate(regions):
        for c, (name, pic) in enumerate([("修复前", im), ("修复后", new)]):
            crop = on_dark(pic).crop(box)
            z = crop.resize((CW - 12, int((CW - 12) * crop.height / max(1, crop.width))), Image.LANCZOS)
            z = z.crop((0, 0, z.width, min(z.height, CH - 36)))
            x, y = c * CW, r * CH + 26
            dz.text((x + 8, r * CH + 4), f"{lab} · {name}", fill=(0, 0, 0), font=font(15))
            zoom.paste(z, (x + 6, y))
    zoom.save(HERE / "_white_edge_trial_zoom.png")
    print("zoom ->", HERE / "_white_edge_trial_zoom.png")

    # 全部 18 帧的削除量 + 边缘带亮度变化
    print("\n=== 全帧统计 ===")
    tot = 0
    for p in sorted(DST.glob("*.png")):
        im = Image.open(p).convert("RGBA")
        new, n = strip(im)
        tot += n
        arr = np.asarray(im)[..., :3].astype(np.float32)
        al = np.asarray(im)[..., 3].astype(np.float32) / 255
        band = (al > 0.02) & (al < 0.98)
        print(f"  {p.name}: 削除 {n:5d} px")
        if apply:
            new.save(p)
    print("总削除像素", tot)

    if apply:
        print("✅ 已覆写", DST)
    else:
        print("（仅生成对照图，未改动素材；加 --apply 才会覆写）")


if __name__ == "__main__":
    main()
