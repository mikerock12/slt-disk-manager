"""Gera os ícones do SLT Disk Manager (deploy/branding/*.ico e prévias PNG).

Desenho próprio: base escura arredondada (cores do painel), prato de disco com
trilhas e braço de leitura, e um selo que identifica cada programa. Rodar de novo
só quando o desenho mudar:  python scripts/generate-icons.py
"""
from pathlib import Path
import math
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "deploy" / "branding"
S = 1024  # supersampling; reduced with LANCZOS to every ICO size
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

BASE_TOP = (38, 45, 56)
BASE_BOTTOM = (16, 19, 23)
PLATTER = (214, 220, 228)
PLATTER_DARK = (150, 160, 173)


def rounded_base(draw_target: Image.Image) -> None:
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((40, 40, S - 40, S - 40), radius=200, fill=255)
    gradient = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(gradient)
    for y in range(S):
        t = y / (S - 1)
        color = tuple(int(BASE_TOP[i] * (1 - t) + BASE_BOTTOM[i] * t) for i in range(3)) + (255,)
        gd.line([(0, y), (S, y)], fill=color)
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((52, 70, S - 28, S - 16), radius=200, fill=(0, 0, 0, 120))
    draw_target.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(18)))
    draw_target.paste(gradient, (0, 0), mask)
    border = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(border).rounded_rectangle((40, 40, S - 40, S - 40), radius=200, outline=(90, 104, 122, 255), width=14)
    draw_target.alpha_composite(border)


def platter(img: Image.Image, accent) -> None:
    d = ImageDraw.Draw(img)
    cx, cy, r = 470, 470, 330
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=PLATTER, outline=PLATTER_DARK, width=10)
    for k, w in ((0.82, 6), (0.64, 6), (0.46, 6)):
        rr = int(r * k)
        d.ellipse((cx - rr, cy - rr, cx + rr, cy + rr), outline=PLATTER_DARK, width=w)
    # Accent sector: the "data" being read.
    d.pieslice((cx - r + 14, cy - r + 14, cx + r - 14, cy + r - 14), start=200, end=262, fill=accent)
    hub = 70
    d.ellipse((cx - hub, cy - hub, cx + hub, cy + hub), fill=(60, 68, 80), outline=PLATTER_DARK, width=8)
    d.ellipse((cx - 22, cy - 22, cx + 22, cy + 22), fill=PLATTER)
    # Read arm from the lower-right pivot.
    px, py = 820, 800
    d.ellipse((px - 62, py - 62, px + 62, py + 62), fill=(60, 68, 80), outline=accent, width=14)
    angle = math.radians(222)
    hx, hy = px + 400 * math.cos(angle), py + 400 * math.sin(angle)
    d.line((px, py, hx, hy), fill=accent, width=46)
    d.ellipse((hx - 34, hy - 34, hx + 34, hy + 34), fill=accent)


def badge(img: Image.Image, color, glyph: str) -> None:
    d = ImageDraw.Draw(img)
    x0, y0, x1, y1 = 600, 600, 960, 960
    d.ellipse((x0 + 8, y0 + 14, x1 + 8, y1 + 14), fill=(0, 0, 0, 110))
    d.ellipse((x0, y0, x1, y1), fill=color, outline=(16, 19, 23), width=18)
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
    white = (255, 255, 255)
    if glyph == "server":
        for i in range(3):
            top = cy - 105 + i * 75
            d.rounded_rectangle((cx - 100, top, cx + 100, top + 55), radius=12, fill=white)
            d.ellipse((cx + 50, top + 17, cx + 72, top + 39), fill=color)
    elif glyph == "client":
        d.rounded_rectangle((cx - 110, cy - 95, cx + 110, cy + 55), radius=16, fill=white)
        d.rectangle((cx - 86, cy - 72, cx + 86, cy + 32), fill=color)
        d.rectangle((cx - 22, cy + 55, cx + 22, cy + 95), fill=white)
        d.rounded_rectangle((cx - 75, cy + 88, cx + 75, cy + 110), radius=8, fill=white)
    elif glyph == "reboot":
        r = 92
        d.arc((cx - r, cy - r, cx + r, cy + r), start=-60, end=250, fill=white, width=36)
        ax, ay = cx + r * math.cos(math.radians(-60)), cy + r * math.sin(math.radians(-60))
        d.polygon([(ax - 52, ay - 30), (ax + 46, ay - 44), (ax + 18, ay + 52)], fill=white)
    elif glyph == "bolt":
        d.polygon([(cx + 30, cy - 125), (cx - 85, cy + 18), (cx - 5, cy + 18), (cx - 35, cy + 128), (cx + 88, cy - 22), (cx + 8, cy - 22)], fill=white)
    elif glyph == "setup":
        d.rectangle((cx - 30, cy - 120, cx + 30, cy + 10), fill=white)
        d.polygon([(cx - 95, cy - 5), (cx + 95, cy - 5), (cx, cy + 95)], fill=white)
        d.rounded_rectangle((cx - 110, cy + 100, cx + 110, cy + 126), radius=10, fill=white)
    elif glyph == "vm":
        d.rounded_rectangle((cx - 115, cy - 90, cx + 115, cy + 70), radius=16, outline=white, width=26)
        d.rounded_rectangle((cx - 60, cy - 45, cx + 60, cy + 25), radius=8, fill=white)
        d.rectangle((cx - 20, cy + 70, cx + 20, cy + 105), fill=white)
        d.rounded_rectangle((cx - 80, cy + 100, cx + 80, cy + 124), radius=8, fill=white)


ICONS = {
    # file: (accent of the platter, badge color, glyph)
    "slt-disk-manager": ((242, 140, 40), (242, 140, 40), "server"),
    "slt-client": ((76, 154, 255), (59, 130, 246), "client"),
    "slt-mode": ((245, 158, 11), (217, 119, 6), "reboot"),
    "slt-kamikaze": ((229, 72, 77), (220, 38, 38), "bolt"),
    "slt-setup": ((242, 140, 40), (34, 160, 107), "setup"),
    "slt-vm": ((242, 140, 40), (124, 92, 246), "vm"),
}


def render(accent, badge_color, glyph) -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    rounded_base(img)
    platter(img, accent)
    badge(img, badge_color, glyph)
    return img


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for name, spec in ICONS.items():
        big = render(*spec)
        frames = [big.resize((n, n), Image.LANCZOS) for n in SIZES]
        frames[-1].save(OUT / f"{name}.ico", format="ICO", sizes=[(n, n) for n in SIZES], append_images=frames[:-1])
        frames[-1].save(OUT / f"{name}.png")
        print(f"{name}.ico ({len(SIZES)} tamanhos)")


if __name__ == "__main__":
    main()
