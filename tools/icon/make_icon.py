"""Draws the SAM app icon (trophy on a gradient tile) and writes a multi-size .ico.

Usage: python -I make_icon.py <out.ico> <preview.png>
Each size is rendered at 8x and downsampled, and sizes <= 24 px use a simplified glyph
(no star, thicker strokes) so the tray/title-bar icon stays crisp.
"""
import math
import sys

from PIL import Image, ImageDraw, ImageFilter

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
TOP = (59, 130, 246)       # blue
BOTTOM = (124, 58, 237)    # violet
GOLD = (251, 191, 36)
WHITE = (255, 255, 255)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def tile(n, small):
    """Rounded-square tile with a diagonal gradient and a soft top highlight."""
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    grad = Image.new("RGBA", (n, n))
    px = grad.load()
    for y in range(n):
        for x in range(n):
            t = min(1.0, max(0.0, (x * 0.35 + y * 0.65) / n))
            px[x, y] = lerp(TOP, BOTTOM, t) + (255,)
    margin = round(n * (0.04 if small else 0.06))
    radius = round(n * 0.22)
    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle((margin, margin, n - margin - 1, n - margin - 1), radius, fill=255)
    img.paste(grad, (0, 0), mask)

    if not small:
        # Soft highlight across the top third.
        hl = Image.new("L", (n, n), 0)
        ImageDraw.Draw(hl).ellipse((-n * 0.2, -n * 0.75, n * 1.2, n * 0.42), fill=46)
        hl = hl.filter(ImageFilter.GaussianBlur(n * 0.04))
        white = Image.new("RGBA", (n, n), WHITE + (255,))
        img.paste(white, (0, 0), Image.composite(hl, Image.new("L", (n, n), 0), mask))
    return img, mask


def star(draw, cx, cy, r, fill):
    pts = []
    for i in range(10):
        a = -math.pi / 2 + i * math.pi / 5
        rr = r if i % 2 == 0 else r * 0.45
        pts.append((cx + rr * math.cos(a), cy + rr * math.sin(a)))
    draw.polygon(pts, fill=fill)


def trophy(n, small):
    """White trophy glyph (and gold star at larger sizes) on a transparent layer."""
    layer = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    s = n / 100.0  # design units: 0..100

    # Handles: thick arcs either side of the cup.
    hw = (8.5 if small else 6.5) * s
    d.arc((17 * s, 25 * s, 41 * s, 55 * s), 90, 270, fill=WHITE, width=round(hw))
    d.arc((59 * s, 25 * s, 83 * s, 55 * s), 270, 90, fill=WHITE, width=round(hw))
    # Cup: a rounded top and a semicircular bottom.
    d.rounded_rectangle((29 * s, 22 * s, 71 * s, 45 * s), 4 * s, fill=WHITE, corners=(True, True, False, False))
    d.pieslice((29 * s, 22 * s, 71 * s, 66 * s), 0, 180, fill=WHITE)
    # Stem and base.
    stem_w = (12 if small else 9) * s
    d.rectangle((50 * s - stem_w / 2, 60 * s, 50 * s + stem_w / 2, 72 * s), fill=WHITE)
    d.rounded_rectangle((36 * s, 70 * s, 64 * s, 76 * s), 2 * s, fill=WHITE)
    d.rounded_rectangle((31 * s, 75 * s, 69 * s, 82 * s), 3 * s, fill=WHITE)

    if not small:
        star(d, 50 * s, 39 * s, 10 * s, GOLD)
    return layer


def render(size):
    small = size <= 24
    n = size * 8
    img, mask = tile(n, small)
    glyph = trophy(n, small)
    if not small:
        # Subtle drop shadow under the glyph for depth.
        shadow = Image.new("RGBA", (n, n), (0, 0, 0, 0))
        alpha = glyph.split()[3].point(lambda a: a * 0.28)
        shadow.putalpha(alpha)
        shadow = shadow.filter(ImageFilter.GaussianBlur(n * 0.015))
        img.alpha_composite(shadow, (0, round(n * 0.012)))
    img.alpha_composite(glyph)
    return img.resize((size, size), Image.LANCZOS)


def main(out_ico, preview_png):
    images = {s: render(s) for s in SIZES}
    images[256].save(out_ico, format="ICO", sizes=[(s, s) for s in SIZES],
                     append_images=[images[s] for s in SIZES[:-1]])

    # Preview: every size on light and dark strips, enlarged 2x.
    pad = 16
    width = sum(s * 2 + pad for s in SIZES) + pad
    sheet = Image.new("RGBA", (width, (256 * 2 + pad * 2) * 2), (0, 0, 0, 0))
    for row, bg in enumerate([(243, 243, 243, 255), (32, 32, 32, 255)]):
        y0 = row * (256 * 2 + pad * 2)
        ImageDraw.Draw(sheet).rectangle((0, y0, width, y0 + 256 * 2 + pad * 2), fill=bg)
        x = pad
        for s in SIZES:
            big = images[s].resize((s * 2, s * 2), Image.NEAREST)
            sheet.alpha_composite(big, (x, y0 + pad + (512 - s * 2) // 2))
            x += s * 2 + pad
    sheet.save(preview_png)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
