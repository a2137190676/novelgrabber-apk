"""生成 Android 自适应图标：卡通风格一摞桌面小说（圆弧过渡）"""
from PIL import Image, ImageDraw
import os

OUT = "/workspace/NovelGrabber/app/src/main/res"

# 液态玻璃浅色渐变背景色
BG_TOP = (214, 232, 255)
BG_BOT = (240, 248, 255)


def rounded_rect(draw, xy, radius, fill, outline=None, width=0):
    draw.rounded_rectangle(xy, radius=radius, fill=fill, outline=outline, width=width)


def draw_books(draw, cx, cy, s):
    """在 (cx,cy) 中心、以 s 为缩放画一摞 4 本书 + 小书签"""
    # 书本堆叠（从下往上），每本颜色、宽度、错位不同，圆角大 => 卡通感
    books = [
        # (w, h, dx, dy, color, edge)
        (300, 46, 0,   96,  (255, 138, 128), (214, 90, 84)),    # 红
        (270, 44, 10,  52,  (255, 202, 92),  (226, 158, 52)),   # 黄
        (300, 46, -6,  8,   (126, 200, 138), (74, 148, 88)),    # 绿
        (240, 44, 14, -36,  (130, 168, 255), (74, 112, 210)),   # 蓝
    ]
    for w, h, dx, dy, col, edge in books:
        x0 = cx + (dx - w) * s / 2
        y0 = cy + (dy - h) * s / 2
        x1 = x0 + w * s
        y1 = y0 + h * s
        r = 18 * s
        # 书页白边（右侧）
        rounded_rect(draw, (x0, y0, x1, y1), r, (255, 255, 255), (0, 0, 0, 0))
        rounded_rect(draw, (x0, y0, x1 - 26 * s, y1), r, col, edge, max(2, int(3 * s)))
        # 书脊装饰线
        ly = (y0 + y1) / 2
        draw.line([(x0 + 22 * s, ly), (x1 - 34 * s, ly)], fill=(255, 255, 255, 170), width=max(2, int(4 * s)))

    # 顶部书上方的小书签（丝带）
    bx = cx + 66 * s
    by_top = cy - 78 * s
    rounded_rect(draw, (bx - 16 * s, by_top, bx + 16 * s, by_top + 60 * s), 8 * s, (255, 105, 140), (210, 60, 100), max(2, int(3 * s)))
    draw.polygon([(bx - 16 * s, by_top + 34 * s), (bx + 16 * s, by_top + 34 * s), (bx, by_top + 62 * s)], fill=(255, 105, 140))


def make_icon(size):
    """完整方形图标（用于 legacy / 预览）：圆形安全区内画图案，外部透明由系统裁圆角"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # 背景整圆（legacy 圆形图标）
    d.ellipse((0, 0, size, size), fill=BG_TOP)
    # 渐变叠加
    grad = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    for y in range(size):
        t = y / size
        c = tuple(int(BG_TOP[i] + (BG_BOT[i] - BG_TOP[i]) * t) for i in range(3)) + (255,)
        gd.line([(0, y), (size, y)], fill=c)
    mask = Image.new("L", (size, size), 0)
    md = ImageDraw.Draw(mask)
    md.ellipse((0, 0, size, size), fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    draw_books(d, size / 2, size / 2, s=size / 432)
    return img


def make_foreground(size):
    """自适应图标前景：108dp 安全区，图案只占中间 ~60%"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    draw_books(d, size / 2, size / 2, s=size / 432 * 0.78)
    return img


def make_background(size):
    """自适应图标背景：液态玻璃浅色渐变"""
    img = Image.new("RGBA", (size, size))
    d = ImageDraw.Draw(img)
    for y in range(size):
        t = y / size
        c = tuple(int(BG_TOP[i] + (BG_BOT[i] - BG_TOP[i]) * t) for i in range(3)) + (255,)
        d.line([(0, y), (size, y)], fill=c)
    return img


# legacy mipmap（方形圆角由系统处理，此处直接圆形）
for dpi, px in [("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)]:
    p = f"{OUT}/mipmap-{dpi}"
    os.makedirs(p, exist_ok=True)
    make_icon(px).save(f"{p}/ic_launcher.png")
    make_icon(px).save(f"{p}/ic_launcher_round.png")

# adaptive icon 图层
for dpi, px in [("mdpi", 108), ("hdpi", 162), ("xhdpi", 216), ("xxhdpi", 324), ("xxxhdpi", 432)]:
    p = f"{OUT}/mipmap-{dpi}"
    make_foreground(px).save(f"{p}/ic_launcher_foreground.png")
    make_background(px).save(f"{p}/ic_launcher_background.png")

print("icons generated")
