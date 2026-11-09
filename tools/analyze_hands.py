# -*- coding: utf-8 -*-
"""定量分析逐帧左右手相位：在桌面/手部 ROI 内统计白色手套像素与帧间差分。

输出：tools/_hands_lr.png（左右半区逐帧条形图）、tools/_hands_matrix.png（左右 ROI 缩略图矩阵）、
      并打印每帧 L/R 差分量，用于判定真正的「左手拍 / 右手拍」帧。
"""
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
SRC = HERE / "_nbm_src.mp4"
FONT = "C:/Windows/Fonts/msyh.ttc"


def font(sz):
    return ImageFont.truetype(FONT, sz) if Path(FONT).exists() else ImageFont.load_default()


def main():
    cap = cv2.VideoCapture(str(SRC))
    fs = []
    while True:
        ok, f = cap.read()
        if not ok:
            break
        fs.append(f)
    cap.release()
    n = len(fs)
    h, w = fs[0].shape[:2]

    # 手部/桌面区：y 0.58~0.90，左右各取角色中线两侧
    y0, y1 = int(h * 0.58), int(h * 0.90)
    lx0, lx1 = int(w * 0.22), int(w * 0.50)
    rx0, rx1 = int(w * 0.50), int(w * 0.80)
    L = [f[y0:y1, lx0:lx1] for f in fs]
    R = [f[y0:y1, rx0:rx1] for f in fs]

    # 逐帧差（相邻帧）与相对静止基准帧（蓝底 F16 / 红底 F40 各自取中位帧）
    def diff(a, b):
        return float(np.mean(np.abs(a.astype(np.float32) - b.astype(np.float32))))

    dl = [0.0] + [diff(L[i], L[i - 1]) for i in range(1, n)]
    dr = [0.0] + [diff(R[i], R[i - 1]) for i in range(1, n)]

    # 白色手套像素占比（近白、低饱和；排除蓝/红背景）
    def glove_ratio(im):
        rgb = cv2.cvtColor(im, cv2.COLOR_BGR2RGB).astype(np.int16)
        mx, mn = rgb.max(2), rgb.min(2)
        return float(((mn > 195) & ((mx - mn) < 28)).mean())

    gl = [glove_ratio(x) for x in L]
    gr = [glove_ratio(x) for x in R]

    print("帧  L差   R差   L白%  R白%   判定")
    for i in range(n):
        side = "L" if dl[i] > dr[i] * 1.15 else ("R" if dr[i] > dl[i] * 1.15 else "-")
        print(f"F{i:02d} {dl[i]:6.2f} {dr[i]:6.2f} {gl[i]*100:6.2f} {gr[i]*100:6.2f}   {side}")

    # 条形图
    BW, BH = 26, 420
    chart = Image.new("RGB", (n * BW + 90, BH + 110), (250, 250, 250))
    d = ImageDraw.Draw(chart)
    top, base = 30, BH + 30
    mx = max(max(dl), max(dr), 1)
    for i in range(n):
        x = 70 + i * BW
        hl = int(dl[i] / mx * BH)
        hr = int(dr[i] / mx * BH)
        d.rectangle([x + 1, base - hl, x + BW // 2 - 1, base], fill=(220, 60, 60))
        d.rectangle([x + BW // 2 + 1, base - hr, x + BW - 2, base], fill=(50, 100, 220))
        if i % 2 == 0:
            d.text((x + 2, base + 6), f"{i:02d}", fill=(0, 0, 0), font=font(12))
        if i in (0, 9, 18, 22, 23, 24, 30, 40, 46):
            d.line([(70 + i * BW, top), (70 + i * BW, base)], fill=(160, 160, 160))
            d.text((70 + i * BW + 2, 8), f"F{i:02d}", fill=(0, 0, 0), font=font(14))
    d.text((6, 8), "L", fill=(220, 60, 60), font=font(18))
    d.text((34, 8), "R", fill=(50, 100, 220), font=font(18))
    d.text((6, base + 40), "红=左半区帧差  蓝=右半区帧差（20fps 逐帧）", fill=(0, 0, 0), font=font(15))
    chart.save(HERE / "_hands_lr.png")
    print("chart ->", HERE / "_hands_lr.png")

    # ROI 缩略图矩阵：每行一帧，左格=L区，右格=R区
    TW, TH = 150, 92
    mat = Image.new("RGB", (TW * 2 + 64, n * (TH + 4) + 30), (245, 245, 245))
    dm = ImageDraw.Draw(mat)
    for i in range(n):
        y = 26 + i * (TH + 4)
        for c, roi in enumerate((L[i], R[i])):
            im = Image.fromarray(cv2.cvtColor(roi, cv2.COLOR_BGR2RGB))
            im.thumbnail((TW - 4, TH - 4), Image.LANCZOS)
            mat.paste(im, (62 + c * TW + 2, y + 2))
        dm.text((4, y + 30), f"F{i:02d}", fill=(0, 0, 0), font=font(16))
    mat.save(HERE / "_hands_matrix.png")
    print("matrix ->", HERE / "_hands_matrix.png")


if __name__ == "__main__":
    main()
