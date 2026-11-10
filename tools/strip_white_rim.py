# -*- coding: utf-8 -*-
"""检查/复现「贴纸白描边」去除效果（统一调用 import_nabo.py 的 v6 算法）。

★ 2026-11-10：本文件早期的 v5 实现是「固定 dist<=5.5 整条带一刀切」，
  会把描边薄的帧里紧邻的黑色勾线一起削掉（用户反馈「黑色勾线有些图
  抠掉了」）。现在不再保留第二份算法，直接复用 import_nabo.strip_sticker_rim，
  避免两个脚本逻辑分叉把素材削坏。v6 原理见 import_nabo.py 模块顶部。

用法：python strip_white_rim.py [--apply]
  不带 --apply 只生成对照图 _white_edge_trial.png / _white_edge_trial_zoom.png
  带 --apply 直接覆写 characters/娜波摩/frames/*.png
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from import_nabo import strip_sticker_rim as strip   # 单一算法源：v6

HERE = Path(__file__).parent
DST = HERE.parent / "characters" / "娜波摩" / "frames"
BG = (38, 42, 52, 255)


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
