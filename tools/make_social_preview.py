"""生成 GitHub Social Preview（1280x640）"""
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import os

W, H = 1280, 640
BASE = r"C:\奇怪的文档\KeyboardCounter"

YAHEI = r"C:\Windows\Fonts\msyh.ttc"
YAHEI_B = r"C:\Windows\Fonts\msyhbd.ttc"
CONSOLA_B = r"C:\Windows\Fonts\consolab.ttf"


def f(path, size, index=0):
    return ImageFont.truetype(path, size, index=index)


# ---------- 背景（RGBA，先渐变再叠网格） ----------
bg = Image.new("RGBA", (W, H), (0, 0, 0, 255))
d = ImageDraw.Draw(bg)
for y in range(H):
    t = y / H
    d.line([(0, y), (W, y)], fill=(int(10 + 10 * t), int(16 + 14 * t), int(28 + 22 * t), 255))

glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
gd.ellipse([700, -180, 1420, 400], fill=(64, 132, 255, 70))
gd.ellipse([-300, 360, 420, 920], fill=(0, 190, 170, 30))
glow = glow.filter(ImageFilter.GaussianBlur(130))
bg = Image.alpha_composite(bg, glow)

grid = Image.new("RGBA", (W, H), (0, 0, 0, 0))
gdd = ImageDraw.Draw(grid)
for x in range(0, W, 48):
    gdd.line([(x, 0), (x, H)], fill=(255, 255, 255, 8), width=1)
for y in range(0, H, 48):
    gdd.line([(0, y), (W, y)], fill=(255, 255, 255, 8), width=1)
bg = Image.alpha_composite(bg, grid)

d = ImageDraw.Draw(bg)
LEFT = 84

# ---------- 徽标 ----------
chip_font = f(YAHEI, 17)
badge_text = "WPF  ·  .NET 8  ·  Windows"
bb = d.textbbox((0, 0), badge_text, font=chip_font)
bw, bh = bb[2] - bb[0], bb[3] - bb[1]
d.rounded_rectangle([LEFT, 70, LEFT + bw + 30, 70 + bh + 18],
                    radius=14, fill=(34, 64, 122, 255), outline=(92, 142, 224, 255), width=1)
d.text((LEFT + 15, 70 + 8), badge_text, font=chip_font, fill=(196, 218, 255))

# ---------- 标题 ----------
d.text((LEFT, 124), "KeyboardCounter", font=f(CONSOLA_B, 62), fill=(255, 255, 255, 255))
d.text((LEFT, 212), "按键统计  +  划词翻译", font=f(YAHEI_B, 30), fill=(124, 182, 255, 255))

# ---------- 卖点（两列对齐） ----------
head_font = f(YAHEI_B, 21)
desc_font = f(YAHEI, 19)
rows = [
    ("实时体验", "按键 / CPM 打字速度 / 网速流量 / 天气"),
    ("离线词典", "ECDICT 76 万词条 + CC-CEDICT 12.5 万条"),
    ("零打扰", "UIA 取词，默认完全不碰剪贴板"),
]
y = 300
for head, desc in rows:
    d.text((LEFT, y), head, font=head_font, fill=(255, 255, 255, 255))
    d.text((LEFT + 132, y + 2), desc, font=desc_font, fill=(184, 200, 222, 255))
    y += 58

# ---------- 右侧卡片 ----------
def paste_card(base, img_path, x, y, scale=1.0, radius=14, shadow=16, border=(64, 80, 108)):
    im = Image.open(img_path).convert("RGBA")
    if scale != 1.0:
        im = im.resize((max(1, int(im.width * scale)), max(1, int(im.height * scale))), Image.LANCZOS)
    w, h = im.size
    card = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], radius=radius, fill=255)
    card.paste(im, (0, 0), mask)

    sh = Image.new("RGBA", (w + shadow * 4, h + shadow * 4), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle(
        [shadow * 2, shadow * 2, shadow * 2 + w, shadow * 2 + h], radius=radius, fill=(0, 0, 0, 180))
    sh = sh.filter(ImageFilter.GaussianBlur(shadow))
    base.alpha_composite(sh, (x - shadow * 2, y - shadow * 2))

    bd = Image.new("RGBA", (w + 2, h + 2), (0, 0, 0, 0))
    ImageDraw.Draw(bd).rounded_rectangle([0, 0, w + 1, h + 1], radius=radius + 1,
                                         outline=border + (255,), width=1)
    base.alpha_composite(bd, (x - 1, y - 1))
    base.alpha_composite(card, (x, y))
    return w, h


paste_card(bg, os.path.join(BASE, "images", "screenshot2.png"), 950, 78, scale=1.45, radius=18)
paste_card(bg, os.path.join(BASE, "images", "screenshot.png"), 706, 322, scale=0.9, radius=10)
paste_card(bg, os.path.join(BASE, "images", "translate-popup-zh.png"), 706, 392, scale=1.02, radius=14)

# ---------- 页脚 ----------
ImageDraw.Draw(bg).text((LEFT, H - 58), "github.com/id270/KeyboardCounter",
                        font=f(CONSOLA_B, 17), fill=(126, 144, 170, 255))

out = os.path.join(BASE, "images", "social-preview.png")
bg.convert("RGB").save(out, "PNG", optimize=True)
print("saved", out, os.path.getsize(out), "bytes", bg.size)
