# -*- coding: utf-8 -*-
"""逐帧导出「娜波摩wuwatappo.mp4」全部 47 帧 + 整帧/手部特写拼图。

用途：人工视觉复核源视频里到底有几只手、左右手交替的真实相位。
输出：tools/_frames_all/Fxx.png、tools/_sheet_full_N.png、tools/_sheet_hands_N.png、
      tools/_sheet_overview.png
"""
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
ORIG = r"C:\Users\Admin\Downloads\🎮 鸣潮\娜波摩wuwatappo.mp4"
COPY = HERE / "_nbm_src.mp4"
OUT = HERE / "_frames_all"


def load(path):
    cap = cv2.VideoCapture(str(path))
    fs = []
    while True:
        ok, f = cap.read()
        if not ok:
            break
        fs.append(f)
    cap.release()
    return fs


def font(size):
    p = "C:/Windows/Fonts/msyh.ttc"
    return ImageFont.truetype(p, size) if Path(p).exists() else ImageFont.load_default()


def tile_sheet(frames, idxs, name, crop=None, tw=420, th=420, cols=3, label_h=32):
    rows = (len(idxs) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * tw, rows * (th + label_h)), (235, 235, 235))
    d = ImageDraw.Draw(sheet)
    for k, i in enumerate(idxs):
        im = Image.fromarray(cv2.cvtColor(frames[i], cv2.COLOR_BGR2RGB))
        if crop:
            h, w = frames[i].shape[:2]
            x0, y0, x1, y1 = crop
            im = im.crop((int(w * x0), int(h * y0), int(w * x1), int(h * y1)))
        im.thumbnail((tw - 8, th - 8), Image.LANCZOS)
        c, r = k % cols, k // cols
        x, y = c * tw, r * (th + label_h)
        sheet.paste(im, (x + (tw - im.width) // 2, y + label_h + (th - im.height) // 2))
        d.text((x + 8, y + 5), f"F{i:02d}   t={i/20.0:.2f}s", fill=(10, 10, 10), font=font(19))
    sheet.save(HERE / name)
    print("sheet ->", HERE / name, sheet.size)


def main():
    fs = load(ORIG)
    if len(fs) < 40:
        print("原路径读取失败，改用 ASCII 副本；原路径解出", len(fs))
        fs = load(COPY)
    copy_frames = load(COPY)
    print("orig frames =", len(fs), "ascii copy frames =", len(copy_frames),
          "size =", fs[0].shape[1::-1])
    if len(copy_frames) == len(fs):
        diff = np.mean(np.abs(fs[0].astype(float) - copy_frames[0].astype(float)))
        print("F00 原视频 vs ASCII副本 平均像素差 =", round(diff, 3))

    OUT.mkdir(exist_ok=True)
    for i, f in enumerate(fs):
        cv2.imwrite(str(OUT / f"F{i:02d}.png"), f)
    print("saved", len(fs), "frames ->", OUT)

    # 全 47 帧总览
    tile_sheet(fs, list(range(len(fs))), "_sheet_overview.png", tw=190, th=190, cols=7)

    # 分段大图：整帧 12 帧一组
    for n, s in enumerate(range(0, len(fs), 12)):
        tile_sheet(fs, list(range(s, min(s + 12, len(fs)))), f"_sheet_full_{n}.png")

    # 手部/桌面区域特写（下半部分，左右各留全宽）
    for n, s in enumerate(range(0, len(fs), 12)):
        tile_sheet(fs, list(range(s, min(s + 12, len(fs)))), f"_sheet_hands_{n}.png",
                   crop=(0.10, 0.46, 0.98, 0.95), tw=420, th=330)


if __name__ == "__main__":
    main()
