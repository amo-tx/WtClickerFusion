# -*- coding: utf-8 -*-
"""白边定位：源帧 / 抠图后 / 深色背景 / 0.55 缩放模拟 / 边缘 4x 放大。"""
from pathlib import Path
import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
DST = HERE.parent / "characters" / "娜波摩" / "frames"
SRC = HERE / "_nbm_src.mp4"
BG = (38, 42, 52, 255)          # 深色桌面
DARK = (38, 42, 52)


def src_frame(i):
    cap = cv2.VideoCapture(str(SRC))
    for k in range(i + 1):
        ok, f = cap.read()
        if not ok:
            raise RuntimeError("frame missing")
    cap.release()
    return Image.fromarray(cv2.cvtColor(f, cv2.COLOR_BGR2RGB))


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


# 三张证据：源帧、抠图原尺寸深色底、0.55 缩放（模拟窗口实际显示）
ids = [0, 1, 27]
PW, PH = 380, 420
sheet = Image.new("RGB", (PW * 3, PH * 2 + 60), (235, 235, 235))
d = ImageDraw.Draw(sheet)
for c, i in enumerate(ids):
    im = Image.open(DST / f"f{i:02d}.png")
    s = src_frame(i)
    x = c * PW
    d.text((x + 8, 6), f"F{i:02d} 源视频（蓝/红底）", fill=(0, 0, 0), font=font(17))
    t = s.copy()
    t.thumbnail((PW - 12, PH - 34), Image.LANCZOS)
    sheet.paste(t, (x + 6, 30))
    d.text((x + 8, PH + 34), f"F{i:02d} 抠图 → 深色桌面", fill=(0, 0, 0), font=font(17))
    t = on_dark(im)
    t.thumbnail((PW - 12, PH - 34), Image.LANCZOS)
    sheet.paste(t, (x + 6, PH + 58))
sheet.save(HERE / "_white_edge_full.png")
print("full ->", HERE / "_white_edge_full.png")

# 边缘放大：帽子顶部 + 左侧人物轮廓 + 键盘/手部
# 用 f00 的 alpha 找到轮廓带，裁 3 个区域，源帧与抠图结果并排 4x
im = Image.open(DST / f"f00.png").convert("RGBA")
a = np.asarray(im)
al = a[:, :, 3]
ys, xs = np.where(al > 40)
print("f00 bbox", xs.min(), ys.min(), xs.max(), ys.max())
regions = [
    ("帽子顶部", (int(xs.min() + (xs.max()-xs.min())*0.18), int(ys.min()),
                 int(xs.min() + (xs.max()-xs.min())*0.72), int(ys.min() + (ys.max()-ys.min())*0.26))),
    ("左侧轮廓", (int(xs.min()), int(ys.min() + (ys.max()-ys.min())*0.30),
                 int(xs.min() + (xs.max()-xs.min())*0.32), int(ys.min() + (ys.max()-ys.min())*0.70))),
    ("右侧轮廓", (int(xs.min() + (xs.max()-xs.min())*0.70), int(ys.min() + (ys.max()-ys.min())*0.30),
                 int(xs.max()), int(ys.min() + (ys.max()-ys.min())*0.70))),
]
CW, CH = 300, 260
zoom = Image.new("RGB", (CW * 3, CH * 2 + 70), (235, 235, 235))
dz = ImageDraw.Draw(zoom)
s = src_frame(0)
# 源帧与抠图后画布尺寸不同：抠图是按统一 crop box 裁过的，用 alpha bbox 对齐到源帧 bbox 的比例不可靠
# 这里只放大「抠图后深色背景」，另附一张源帧整体缩略供参照
for c, (lab, box) in enumerate(regions):
    box = (max(0, box[0]), max(0, box[1]), min(im.width, box[2]), min(im.height, box[3]))
    crop = on_dark(im).crop(box)
    z = crop.resize((CW - 12, int((CW - 12) * crop.height / max(1, crop.width))), Image.LANCZOS)
    zz = z.crop((0, 0, z.width, min(z.height, CH - 40)))
    x = c * CW
    dz.text((x + 8, 6), lab + " · 原尺寸 4x", fill=(0, 0, 0), font=font(16))
    zoom.paste(zz, (x + 6, 30))
    # 模拟窗口 0.55 缩放后再放大回来看插值白边
    scaled = on_dark(im, (round(im.width * .55), round(im.height * .55)))
    sc = scaled.crop(tuple(int(v * .55) for v in box))
    z2 = sc.resize((CW - 12, int((CW - 12) * sc.height / max(1, sc.width))), Image.LANCZOS)
    z2 = z2.crop((0, 0, z2.width, min(z2.height, CH - 40)))
    dz.text((x + 8, CH + 36), lab + " · 0.55 缩放后 4x", fill=(0, 0, 0), font=font(16))
    zoom.paste(z2, (x + 6, CH + 60))
zoom.save(HERE / "_white_edge_zoom.png")
print("zoom ->", HERE / "_white_edge_zoom.png")

# 定量：边缘带里「白色描边」到底有多宽、多亮
print("\n=== 边缘带亮度剖面（沿 alpha 从内到外） ===")
for name in ["f00.png", "f01.png", "f27.png"]:
    arr = np.asarray(Image.open(DST / name).convert("RGBA"))
    rgb = arr[:, :, :3].astype(np.float32)
    al = arr[:, :, 3].astype(np.float32)
    lum = rgb.mean(2)
    print(name)
    for lo, hi in [(250, 256), (200, 250), (128, 200), (40, 128), (5, 40)]:
        m = (al >= lo) & (al < hi)
        if m.sum():
            print(f"  alpha {lo:3d}-{hi:3d}: n={m.sum():6d} 亮度均值={lum[m].mean():6.1f} 近白(>220)={(lum[m]>220).mean()*100:5.1f}%")
