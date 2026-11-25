#!/usr/bin/env python3
"""Draws Longhand Score's tiles: the main app's rounded tile in plum instead of blue, with a staff and a pair of beamed eighth
notes. Run from the repository root: python3 tools/make-score-icons.py (needs Pillow)."""
from PIL import Image, ImageDraw

PLUM = (124, 58, 140, 255)
WHITE = (255, 255, 255, 255)
SS = 8  # drawn this many times larger, then scaled down, for smooth edges


def tile(size):
    """The square tile, size x size pixels."""
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=s * 0.16, fill=PLUM)
    u = s / 150  # the design is laid out on a 150 grid
    # The staff: five lines, a space of 11.
    space = 11 * u
    top = 52 * u
    for i in range(5):
        y = top + i * space
        d.line([30 * u, y, 120 * u, y], fill=(255, 255, 255, 150), width=max(1, round(2.2 * u)))
    # Two eighths beamed: heads on the second and third spaces from the bottom, stems up, one beam.
    heads = [(62 * u, top + 3.5 * space), (92 * u, top + 2.5 * space)]
    rx, ry = 8.5 * u, 6.2 * u
    for (x, y) in heads:
        head = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        ImageDraw.Draw(head).ellipse([x - rx, y - ry, x + rx, y + ry], fill=WHITE)
        img.alpha_composite(head.rotate(20, center=(x, y), resample=Image.BICUBIC))
    stem_w = 3.4 * u
    tops = [top - 1.2 * space, top - 2.0 * space]
    for (x, y), t in zip(heads, tops):
        sx = x + rx - stem_w / 2 - 0.6 * u
        d.rectangle([sx - stem_w / 2, t, sx + stem_w / 2, y - 1 * u], fill=WHITE)
    x0, x1 = heads[0][0] + rx - stem_w, heads[1][0] + rx + 0.2 * u
    d.polygon([(x0, tops[0]), (x1, tops[1]), (x1, tops[1] + 7 * u), (x0, tops[0] + 7 * u)], fill=WHITE)
    return img.resize((size, size), Image.LANCZOS)


def on_canvas(width, height, size, x):
    img = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    img.paste(tile(size), (x, (height - size) // 2))
    return img


A = "Longhand/Assets/"
tile(150).save(A + "Score150x150Logo.png")
tile(44).save(A + "Score44x44Logo.png")
on_canvas(310, 150, 150, 80).save(A + "ScoreWide310x150Logo.png")
on_canvas(620, 300, 300, 160).save(A + "ScoreSplashScreen.png")
