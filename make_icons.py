"""简约风应用图标：纯色底 + 白色扁平书堆"""
from PIL import Image, ImageDraw
import os

OUT = "/workspace/NovelGrabber/app/src/main/res"
BG = (37, 99, 235, 255)        # #2563EB
FG = (255, 255, 255, 255)


def draw_books(d, cx, cy, s):
    """扁平白色书堆：三本圆角矩形 + 底部托板，无描边"""
    books = [
        (150, 34, 0,   -56),
        (190, 38, -6,  -14),
        (220, 42, 6,   28),
    ]
    for w, h, dx, dy in books:
        x0 = cx + (dx - w / 2) * s
        y0 = cy + (dy - h / 2) * s
        d.rounded_rectangle((x0, y0, x0 + w * s, y0 + h * s), radius=10 * s, fill=FG)
    # 底部托板（桌面横线）
    x0 = cx - 140 * s
    y0 = cy + 62 * s
    d.rounded_rectangle((x0, y0, x0 + 280 * s, y0 + 12 * s), radius=6 * s, fill=FG)


def make_icon(size):
    """legacy 图标：蓝色圆角方形 + 白色书堆"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = size * 0.22
    d.rounded_rectangle((0, 0, size, size), radius=r, fill=BG)
    draw_books(d, size / 2, size / 2, s=size / 320)
    return img


def make_foreground(size):
    """自适应前景：图案居中占 ~72%"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    draw_books(d, size / 2, size / 2, s=size / 320 * 0.72)
    return img


def make_background(size):
    img = Image.new("RGBA", (size, size))
    d = ImageDraw.Draw(img)
    d.rectangle((0, 0, size, size), fill=BG)
    return img


for dpi, px in [("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)]:
    p = f"{OUT}/mipmap-{dpi}"
    os.makedirs(p, exist_ok=True)
    make_icon(px).save(f"{p}/ic_launcher.png")
    make_icon(px).save(f"{p}/ic_launcher_round.png")

for dpi, px in [("mdpi", 108), ("hdpi", 162), ("xhdpi", 216), ("xxhdpi", 324), ("xxxhdpi", 432)]:
    p = f"{OUT}/mipmap-{dpi}"
    make_foreground(px).save(f"{p}/ic_launcher_foreground.png")
    make_background(px).save(f"{p}/ic_launcher_background.png")

print("minimal icons generated")
