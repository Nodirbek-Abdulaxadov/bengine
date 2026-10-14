# Bengine Data Studio package icons (bengine-studio.png, .ico, .icns): a gold database
# cylinder — the same symbol the explorer tree uses for a database — on the dark
# Studio background, with an orange explorer chevron. Drawn here, so there is no
# separate source image. Re-run after changing the design:
#   python3 -I deploy/studio/make-icons.py      (requires Pillow)
# The design is drawn at 4x and downsampled for smooth edges; downsampling uses
# premultiplied alpha ("RGBa") so transparent corners do not darken the rim.
from pathlib import Path

from PIL import Image, ImageDraw

here = Path(__file__).resolve().parent
S = 4096  # supersampled canvas


def px(v):
    return int(v * S / 1024)


icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(icon)

# Background: rounded square with a subtle vertical gradient.
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), radius=px(225), fill=255)
gradient = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(gradient)
top, bottom = (34, 44, 58), (14, 19, 27)
for y in range(S):
    t = y / (S - 1)
    gd.line([(0, y), (S, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
icon.paste(gradient, (0, 0), mask)
d.rounded_rectangle((px(6), px(6), S - px(6), S - px(6)), radius=px(220), outline=(52, 64, 82, 255), width=px(10))

# Database cylinder: body, two bands, top ellipse.
left, right = px(262), px(762)
rx, ry = (right - left) // 2, px(78)
top_y, bottom_y = px(238), px(742)
gold, gold_dark, gold_light = (201, 151, 58, 255), (166, 120, 40, 255), (240, 192, 96, 255)
d.rectangle((left, top_y, right, bottom_y), fill=gold)
d.ellipse((left, bottom_y - ry, right, bottom_y + ry), fill=gold)
for band_y in (px(406), px(574)):
    d.arc((left, band_y - ry, right, band_y + ry), start=0, end=180, fill=gold_dark, width=px(22))
d.ellipse((left, top_y - ry, right, top_y + ry), fill=gold_light)
d.ellipse((left + px(36), top_y - ry + px(14), right - px(36), top_y + ry - px(14)), outline=(255, 222, 150, 255), width=px(8))

# Explorer chevron badge (bottom-right), in the Studio accent orange.
cx, cy, r = px(770), px(770), px(150)
d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(224, 134, 47, 255), outline=(14, 19, 27, 255), width=px(18))
d.line([(cx - px(42), cy - px(70)), (cx + px(42), cy), (cx - px(42), cy + px(70))], fill=(26, 18, 6, 255), width=px(40), joint="curve")


def resize(image, size):
    return image.convert("RGBa").resize(size, Image.LANCZOS).convert("RGBA")


base = resize(icon, (1024, 1024))
resize(base, (512, 512)).save(here / "bengine-studio.png")
base.save(here / "bengine-studio.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
base.save(here / "bengine-studio.icns")
print("ok")
