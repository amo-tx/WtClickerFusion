# -*- coding: utf-8 -*-
"""把代表帧的手部区域放大并排，供人工视觉判定「哪只手在拍」。"""
from pathlib import Path
import cv2
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
OUT = HERE / "_frames_all"
FONT = "C:/Windows/Fonts/msyh.ttc"


def font(sz):
    return ImageFont.truetype(FONT, sz) if Path(FONT).exists() else ImageFont.load_default()


def main():
    idxs = [0, 1, 3, 4, 9, 16, 23, 24, 25, 26, 27, 28, 29, 30, 31, 40]
    TW, TH, LH, COLS = 460, 360, 34, 4
    rows = (len(idxs) + COLS - 1) // COLS
    sheet = Image.new("RGB", (COLS * TW, rows * (TH + LH)), (240, 240, 240))
    d = ImageDraw.Draw(sheet)
    for k, i in enumerate(idxs):
        im = Image.open(OUT / f"F{i:02d}.png").convert("RGB")
        w, h = im.size
        # 只保留脸下方~桌面，排除左侧气泡与帽子
        im = im.crop((int(w * 0.30), int(h * 0.62), int(w * 0.86), int(h * 0.96)))
        im = im.resize((TW - 8, TH - 8), Image.LANCZOS)
        c, r = k % COLS, k // COLS
        x, y = c * TW, r * (TH + LH)
        sheet.paste(im, (x + 4, y + LH + 4))
        d.text((x + 8, y + 6), f"F{i:02d}", fill=(10, 10, 10), font=font(20))
    sheet.save(HERE / "_hand_states.png")
    print("->", HERE / "_hand_states.png", sheet.size)


if __name__ == "__main__":
    main()
