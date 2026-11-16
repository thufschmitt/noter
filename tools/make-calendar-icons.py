#!/usr/bin/env python3
"""Draws Longhand Calendar's tiles: the main app's rounded tile in green instead of blue, with a calendar page (rings, header band)
holding the main logo's pen wave. Run from the repository root: python3 tools/make-calendar-icons.py (needs Pillow)."""
import math
from PIL import Image, ImageDraw

GREEN = (30, 133, 96, 255)
WHITE = (255, 255, 255, 255)
SS = 8  # drawn this many times larger, then scaled down, for smooth edges


def tile(size):
    """The square tile, size x size pixels."""
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=s * 0.16, fill=GREEN)
    u = s / 150  # the design is laid out on a 150 grid
    w = 7 * u
    # The page, its header band and the two rings.
    left, top, right, bottom = 34 * u, 40 * u, 116 * u, 116 * u
    d.rounded_rectangle([left, top, right, bottom], radius=10 * u, outline=WHITE, width=round(w))
    d.rectangle([left, top + 4 * u, right, top + 20 * u], fill=WHITE)
    d.rounded_rectangle([left, top, right, top + 20 * u], radius=10 * u, fill=WHITE)
    for x in (56 * u, 94 * u):
        d.rounded_rectangle([x - 4 * u, 30 * u, x + 4 * u, 50 * u], radius=4 * u, fill=WHITE, outline=GREEN, width=round(2.5 * u))
    # The pen wave of the main logo, written on the page, over a ruled line.
    pts = []
    for i in range(61):
        t = i / 60
        x = 48 * u + t * 54 * u
        y = 86 * u - math.sin(t * 2 * math.pi * 1.25 + 0.3) * 11 * u
        pts.append((x, y))
    d.line(pts, fill=WHITE, width=round(6 * u), joint="curve")
    for p in (pts[0], pts[-1]):
        r = 3 * u
        d.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=WHITE)
    d.line([46 * u, 104 * u, 104 * u, 104 * u], fill=WHITE, width=round(3.5 * u))
    return img.resize((size, size), Image.LANCZOS)


def on_canvas(width, height, size, x):
    img = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    img.paste(tile(size), (x, (height - size) // 2))
    return img


A = "Longhand/Assets/"
tile(150).save(A + "Calendar150x150Logo.png")
tile(44).save(A + "Calendar44x44Logo.png")
on_canvas(310, 150, 150, 80).save(A + "CalendarWide310x150Logo.png")
on_canvas(620, 300, 300, 160).save(A + "CalendarSplashScreen.png")
