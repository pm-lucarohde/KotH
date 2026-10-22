#!/usr/bin/env python3
"""Painted textures for map "Neu", in the style of the mushroom figures:
saturated colours, clear shapes with a darker rim, a light plane and broad
brush strokes inside every shape, like Mushroom.png (Luca, 22.10.2026: "not
washed out", then "a touch softer and more like a comic").

Writes tileable PNGs to KotH.Game/Resources/Textures/Terrain (grass, dirt,
sand, snow, rock for TerrainTriplanar; bark for PaintedTriplanar, single
leaves for LeafSplat;
the sea is one plain colour, Luca's wish). Needs Pillow; seeded, so every run
paints the same.
Sizes below are in pixels of a 1024 texture and scaled by K.

  python3 Tools/maps/textures.py [--preview]
"""
import math
import os
import random
import sys

from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
OUT = os.path.join(ROOT, 'KotH.Game/Resources/Textures/Terrain')
SIZE = 2048                 # ground: 4 m per repeat (TerrainTriplanar), 512 px/m
SMALL = SIZE // 2           # leaves and bark: 2 m per repeat (PaintedTriplanar)
K = SIZE / 1024
EDGE = 1.0 * K              # blur of an edge: soft, but the shape stays clear
SEED = 22
LEAF_FILL = 0.94           # a leaf's length in its quarter of the atlas (LeafSplat.sdsl: Fill)
LEAF_WIDTH = 0.43          # its width over its length (LeafSplat.sdsl: Width)


def mix(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def shade(c, k):
    return tuple(max(0, min(255, int(round(v * k)))) for v in c)


def wrap(draw_fn, size, x, y, reach):
    """Draws a shape at (x, y) and again across every edge it reaches over,
    so the texture tiles without seams."""
    for dx in (-size, 0, size):
        for dy in (-size, 0, size):
            if -reach <= x + dx <= size + reach and -reach <= y + dy <= size + reach:
                draw_fn(x + dx, y + dy)


def blur(img, radius):
    """A blur that wraps round the edges like the tiling; layers blur
    premultiplied, else every edge gets a dark fringe."""
    mode = img.mode
    if mode == 'RGBA':
        img = img.convert('RGBa')
    w, h = img.size
    big = Image.new(img.mode, (w * 3, h * 3))
    for i in range(3):
        for j in range(3):
            big.paste(img, (i * w, j * h))
    return big.filter(ImageFilter.GaussianBlur(radius)).crop((w, h, 2 * w, 2 * h)).convert(mode)


def layer(size):
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    return img, ImageDraw.Draw(img)


def clouds(rng, size, cells, low, high):
    """Soft tileable value noise from low to high (an L image)."""
    small = Image.new('L', (cells, cells))
    small.putdata([int(255 * rng.random()) for _ in range(cells * cells)])
    big = Image.new('L', (cells * 3, cells * 3))
    for i in range(3):
        for j in range(3):
            big.paste(small, (i * cells, j * cells))
    big = big.resize((size * 3, size * 3), Image.BICUBIC).crop((size, size, 2 * size, 2 * size))
    big = blur(big, size / cells / 2)
    return big.point(lambda v: int(low + (high - low) * v / 255))


def tinted(base, light, dark, noise):
    """A ground of base colour, lighter or darker by the noise."""
    img = Image.new('RGB', noise.size, base)
    lit = Image.new('RGB', noise.size, light)
    deep = Image.new('RGB', noise.size, dark)
    img = Image.composite(lit, img, noise.point(lambda v: max(0, v - 128) * 2))
    return Image.composite(deep, img, noise.point(lambda v: max(0, 128 - v) * 2)).convert('RGBA')


def blob(cx, cy, r, rng, corners=9, rough=0.25, squash=1.0, turn=0.0):
    pts = []
    for k in range(corners):
        a = 2 * math.pi * k / corners + rng.uniform(-0.2, 0.2)
        d = r * (1 + rng.uniform(-rough, rough))
        x, y = math.cos(a) * d, math.sin(a) * d * squash
        pts.append((cx + x * math.cos(turn) - y * math.sin(turn), cy + x * math.sin(turn) + y * math.cos(turn)))
    return pts


def polys(d, size, x, y, reach, shapes):
    """Draws (points relative to x, y, fill) shapes in order, wrapped."""
    def draw(sx, sy):
        for pts, fill in shapes:
            d.polygon([(sx + px, sy + py) for px, py in pts], fill=fill)
    wrap(draw, size, x, y, reach)


def stroke(length, width, turn, bend=0.0):
    """A tapered brush stroke along +x, turned; bend curves it sideways."""
    pts = []
    for k in range(9):
        t = k / 8
        pts.append((t * length, width * 0.5 * math.sin(math.pi * t) ** 0.6 + bend * length * math.sin(math.pi * t)))
    for k in range(8, -1, -1):
        t = k / 8
        pts.append((t * length, -width * 0.5 * math.sin(math.pi * t) ** 0.6 + bend * length * math.sin(math.pi * t)))
    c, s = math.cos(turn), math.sin(turn)
    return [(px * c - py * s, px * s + py * c) for px, py in pts]


def brush(img, rng, count, colours, length, width, turn, jitter, bend=0.1):
    """Visible brush strokes in close tones, like the painted figures."""
    size = img.size[0]
    lay, d = layer(size)
    for _ in range(count):
        x, y = rng.uniform(0, size), rng.uniform(0, size)
        ln = rng.uniform(*length) * K
        pts = stroke(ln, rng.uniform(*width) * K, turn + rng.uniform(-jitter, jitter), rng.uniform(-bend, bend))
        polys(d, size, x - ln / 2, y, ln * 1.2, [(pts, rng.choice(colours))])
    img.alpha_composite(blur(lay, EDGE))


def stones(img, rng, specs, rim_dark=0.6, light_alpha=200):
    """Painted stones (x, y, radius, colour, squash, corners): dark rim, body,
    a hard-edged light plane top left; drawn in order."""
    size = img.size[0]
    lay, d = layer(size)
    for cx, cy, r, colour, squash, corners in specs:
        turn = rng.uniform(-0.3, 0.3) if squash < 0.75 else rng.uniform(0, math.pi)
        outline = blob(0, 0, r, rng, corners, 0.22, squash, turn)
        inner = [(x * 0.88 - r * 0.03, y * 0.88 - r * 0.03) for x, y in outline]
        plane = [(x * 0.55 - r * 0.18, y * 0.5 - r * 0.2 * squash) for x, y in outline]
        polys(d, size, cx, cy, r * 1.5, [(outline, shade(colour, rim_dark) + (255,)),
                                         (inner, colour + (255,)),
                                         (plane, shade(colour, 1.18) + (light_alpha,))])
    img.alpha_composite(blur(lay, EDGE))


def specks(img, rng, count, colours, radius):
    size = img.size[0]
    lay, d = layer(size)
    for _ in range(count):
        x, y = rng.uniform(0, size), rng.uniform(0, size)
        r = rng.uniform(*radius) * K
        c = rng.choice(colours)
        wrap(lambda sx, sy, r=r, c=c: d.ellipse((sx - r, sy - r, sx + r, sy + r), fill=c), size, x, y, r + 2)
    img.alpha_composite(blur(lay, EDGE))


def blade(length, width, lean, bend, root):
    """A slim curved blade of grass from (root, 0) upwards, tapering to a tip."""
    left, right = [], []
    for k in range(9):
        t = k / 8
        x = root + lean * length * t + bend * length * t * t
        y = -length * t
        w = width * 0.5 * (1 - t) ** 0.8
        left.append((x - w, y))
        right.append((x + w, y))
    return left + right[::-1]


def grass(rng):
    img = tinted((96, 168, 62), (124, 194, 76), (66, 132, 48), clouds(rng, SIZE, 6, 70, 190))
    # Soft clumps, darker and lighter, so the meadow reads from afar.
    lay, d = layer(SIZE)
    for _ in range(70):
        r = rng.uniform(40, 90) * K
        c = rng.choice([(70, 138, 48, 120), (128, 192, 78, 110)])
        polys(d, SIZE, rng.uniform(0, SIZE), rng.uniform(0, SIZE), r * 1.4,
              [(blob(0, 0, r, rng, 12, 0.3, rng.uniform(0.6, 1.0), rng.uniform(0, math.pi)), c)])
    img.alpha_composite(blur(lay, 8 * K))
    # Tufts like in a comic: a few slim, curved blades from one root, close
    # to the ground's tone (a shade darker, or lighter towards the tips).
    for count, colours in ((130, [(80, 148, 52), (86, 154, 54)]),
                           (130, [(132, 198, 80), (146, 208, 92)])):
        lay, d = layer(SIZE)
        for _ in range(count):
            c = rng.choice(colours) + (255,)
            shapes = []
            for b in range(rng.randint(3, 5)):
                length = rng.uniform(26, 46) * K
                lean = rng.uniform(-0.55, 0.55) + (b - 2) * 0.18
                shapes.append((blade(length, rng.uniform(3.0, 4.5) * K, lean, rng.uniform(-0.25, 0.25), (b - 2) * 3 * K), c))
            polys(d, SIZE, rng.uniform(0, SIZE), rng.uniform(0, SIZE), 50 * K, shapes)
        img.alpha_composite(blur(lay, EDGE))
    # A few small flowers, white and yellow like the spots on the caps.
    lay, d = layer(SIZE)
    for _ in range(10):
        petal = rng.choice([(250, 248, 240), (255, 226, 92)])
        r = rng.uniform(3, 5) * K

        def draw(fx, fy, petal=petal, r=r):
            for k in range(5):
                a = 2 * math.pi * k / 5
                px, py = fx + math.cos(a) * r, fy + math.sin(a) * r
                d.ellipse((px - r * 0.75, py - r * 0.75, px + r * 0.75, py + r * 0.75), fill=shade(petal, 0.8) + (255,))
                d.ellipse((px - r * 0.55, py - r * 0.6, px + r * 0.5, py + r * 0.5), fill=petal + (255,))
            d.ellipse((fx - r * 0.5, fy - r * 0.5, fx + r * 0.5, fy + r * 0.5), fill=(236, 160, 40, 255))
        wrap(draw, SIZE, rng.uniform(0, SIZE), rng.uniform(0, SIZE), 12 * K)
    img.alpha_composite(blur(lay, EDGE))
    return img.convert('RGB')


def dirt(rng):
    img = tinted((132, 88, 52), (152, 104, 60), (104, 68, 40), clouds(rng, SIZE, 5, 60, 200))
    # Clods with a dark lower rim and a light top, crisp like painted earth.
    lay, d = layer(SIZE)
    for _ in range(160):
        r = rng.uniform(12, 40) * K
        c = rng.choice([(158, 110, 64), (112, 72, 42), (144, 96, 56), (126, 84, 48)])
        pts = blob(0, 0, r, rng, 10, 0.3, rng.uniform(0.55, 0.9), rng.uniform(-0.4, 0.4))
        polys(d, SIZE, rng.uniform(0, SIZE), rng.uniform(0, SIZE), r * 1.4,
              [([(x + r * 0.06, y + r * 0.1) for x, y in pts], shade(c, 0.7) + (230,)),
               (pts, c + (240,)),
               ([(x * 0.6 - r * 0.15, y * 0.45 - r * 0.25) for x, y in pts], shade(c, 1.15) + (200,))])
    img.alpha_composite(blur(lay, EDGE))
    brush(img, rng, 90, [(164, 116, 70, 100), (100, 64, 36, 100)], (30, 70), (8, 16), 0.0, 0.5)
    lay, d = layer(SIZE)
    for _ in range(7):
        x, y = rng.uniform(0, SIZE), rng.uniform(0, SIZE)
        a, pts = rng.uniform(0, 2 * math.pi), []
        for k in range(12):
            pts.append((x, y))
            a += rng.uniform(-0.5, 0.5)
            x, y = x + math.cos(a) * 9 * K, y + math.sin(a) * 9 * K
        ox, oy = pts[0]

        def draw(sx, sy, pts=pts, ox=ox, oy=oy):
            d.line([(sx - ox + px, sy - oy + py + 2 * K) for px, py in pts], fill=(70, 42, 24, 240), width=int(5 * K))
            d.line([(sx - ox + px, sy - oy + py) for px, py in pts], fill=(124, 82, 50, 255), width=int(3 * K))
        wrap(draw, SIZE, ox, oy, 120 * K)
    img.alpha_composite(blur(lay, EDGE))
    specs = []
    for _ in range(40):
        c = rng.choice([(150, 132, 112), (170, 150, 124), (128, 100, 76)])
        specs.append((rng.uniform(0, SIZE), rng.uniform(0, SIZE), rng.uniform(9, 20) * K, c, rng.uniform(0.6, 0.9), 8))
    stones(img, rng, specs, 0.55)
    return img.convert('RGB')


def sand(rng):
    img = tinted((238, 214, 148), (248, 230, 172), (218, 190, 124), clouds(rng, SIZE, 6, 80, 180))
    # Ripples as a lit crest over a shaded trough, then grains and a few shells.
    lay, d = layer(SIZE)
    for _ in range(60):
        x, y = rng.uniform(0, SIZE), rng.uniform(0, SIZE)
        w = rng.uniform(40, 110) * K

        def draw(sx, sy, w=w):
            d.arc((sx - w, sy - w * 0.25 + 3 * K, sx + w, sy + w * 0.25 + 3 * K), 200, 340, fill=(212, 182, 118, 150), width=int(4 * K))
            d.arc((sx - w, sy - w * 0.25, sx + w, sy + w * 0.25), 205, 335, fill=(255, 244, 200, 170), width=int(3 * K))
        wrap(draw, SIZE, x, y, 120 * K)
    img.alpha_composite(blur(lay, EDGE))
    brush(img, rng, 70, [(250, 236, 186, 80), (222, 194, 130, 80)], (50, 110), (10, 22), 0.0, 0.3)
    specks(img, rng, 1250, [(196, 160, 98, 200), (255, 248, 220, 200), (224, 192, 128, 200), (176, 140, 90, 180)], (1.4, 2.8))
    specs = []
    for _ in range(6):
        c = rng.choice([(246, 214, 206), (236, 236, 228)])
        specs.append((rng.uniform(0, SIZE), rng.uniform(0, SIZE), rng.uniform(5, 8) * K, c, 0.7, 6))
    stones(img, rng, specs, 0.7)
    return img.convert('RGB')


def snow(rng):
    # Calm, but painted: big drifts as crisp planes in close tones (pale blue
    # shadow below, white crest above), long soft strokes, a few sparkles.
    img = tinted((222, 232, 246), (238, 244, 252), (204, 218, 240), clouds(rng, SIZE, 4, 90, 170))
    lay, d = layer(SIZE)
    for _ in range(16):
        x, y = rng.uniform(0, SIZE), rng.uniform(0, SIZE)
        w, h = rng.uniform(140, 260) * K, rng.uniform(50, 100) * K
        under = blob(0, h * 0.25, w, rng, 14, 0.12, h / w, rng.uniform(-0.15, 0.15))
        crest = [(px * 0.9, py * 0.75 - h * 0.25) for px, py in under]
        polys(d, SIZE, x, y, w * 1.3, [(under, (206, 222, 244, 150)), (crest, (246, 249, 255, 190))])
    img.alpha_composite(blur(lay, 1.5 * K))
    brush(img, rng, 45, [(216, 230, 248, 90), (255, 255, 255, 110)], (120, 260), (16, 34), 0.0, 0.2, 0.05)
    specks(img, rng, 150, [(255, 255, 255, 255), (198, 216, 244, 200)], (1.4, 2.6))
    return img.convert('RGB')


def rock(rng):
    # The columns already make the blocks: the rock shows big painted planes
    # (about 2 m) in close tones, layers, a few long cracks with a lit lip,
    # strokes, chips and lichen spots.
    img = tinted((160, 148, 134), (180, 168, 152), (134, 122, 110), clouds(rng, SIZE, 3, 60, 200))
    specs = []
    for j in range(2):
        for i in range(2):
            x = (i + 0.5 + (0.5 if j % 2 else 0)) * SIZE / 2 + rng.uniform(-80, 80) * K
            y = (j + 0.5) * SIZE / 2 + rng.uniform(-80, 80) * K
            c = shade(mix((158, 146, 132), (176, 164, 148), rng.random()), rng.uniform(0.92, 1.06))
            specs.append((x, y, rng.uniform(300, 380) * K, c, rng.uniform(0.7, 0.9), 7))
    for _ in range(5):
        c = shade(mix((158, 146, 132), (176, 164, 148), rng.random()), rng.uniform(0.92, 1.06))
        specs.append((rng.uniform(0, SIZE), rng.uniform(0, SIZE), rng.uniform(100, 180) * K, c, rng.uniform(0.6, 0.85), 6))
    stones(img, rng, specs, 0.86, 120)
    lay, d = layer(SIZE)
    y = 0.0
    while y < SIZE:
        h = rng.uniform(120, 280) * K
        tone = rng.choice([(186, 174, 158, 60), (128, 116, 104, 60), (170, 158, 142, 50)])
        step = int(128 * K)
        pts = [(x, y + rng.uniform(-16, 16) * K) for x in range(0, SIZE + step, step)]
        pts += [(x, y + h + rng.uniform(-16, 16) * K) for x in range(SIZE, -step, -step)]
        d.polygon(pts, fill=tone)
        d.line([(x, py + 3 * K) for x, py in pts[:SIZE // step + 1]], fill=(112, 100, 90, 110), width=int(3 * K))
        d.line([(x, py) for x, py in pts[:SIZE // step + 1]], fill=(214, 204, 188, 140), width=int(3 * K))
        y += h
    img.alpha_composite(blur(lay, EDGE))
    brush(img, rng, 110, [(184, 172, 156, 100), (138, 126, 114, 100), (170, 158, 142, 100)], (60, 150), (14, 28), -1.45, 0.35)
    lay, d = layer(SIZE)
    for _ in range(5):
        x, y = rng.uniform(0, SIZE), rng.uniform(0, SIZE)
        a, pts = rng.uniform(-1.9, -1.2), []
        for k in range(rng.randint(5, 10)):
            pts.append((x, y))
            a += rng.uniform(-0.6, 0.6)
            x, y = x + math.cos(a) * 30 * K, y + math.sin(a) * 30 * K
        ox, oy = pts[0]

        def draw(sx, sy, pts=pts, ox=ox, oy=oy):
            d.line([(sx - ox + px + 4 * K, sy - oy + py + 3 * K) for px, py in pts], fill=(214, 204, 188, 190), width=int(6 * K))
            d.line([(sx - ox + px, sy - oy + py) for px, py in pts], fill=(84, 74, 66, 240), width=int(7 * K))
        wrap(draw, SIZE, ox, oy, 320 * K)
    img.alpha_composite(blur(lay, EDGE))
    specs = []
    for _ in range(12):
        c = rng.choice([(132, 122, 110), (196, 186, 170), (170, 160, 146)])
        specs.append((rng.uniform(0, SIZE), rng.uniform(0, SIZE), rng.uniform(8, 20) * K, c, 0.75, 7))
    stones(img, rng, specs, 0.62)
    # Lichen: a third of the first patches (Luca: too many, twice), one
    # yellow, one green. The draw that once picked the tone stays, so the
    # patches keep their places.
    lay, d = layer(SIZE)
    for c in ((196, 186, 92), (150, 172, 84)):
        cx, cy = rng.uniform(0, SIZE), rng.uniform(0, SIZE)
        rng.choice(range(3))
        for _ in range(rng.randint(5, 10)):
            x, y, r = cx + rng.uniform(-36, 36) * K, cy + rng.uniform(-24, 24) * K, rng.uniform(5, 12) * K

            def draw(sx, sy, r=r, c=c):
                d.ellipse((sx - r, sy - r, sx + r, sy + r), fill=shade(c, 0.75) + (230,))
                d.ellipse((sx - r * 0.8, sy - r * 0.85, sx + r * 0.7, sy + r * 0.6), fill=c + (240,))
            wrap(draw, SIZE, x, y, r + 2)
    img.alpha_composite(blur(lay, EDGE))
    return img.convert('RGB')


def leaves(rng):
    """Four single leaves in a 2 x 2 atlas, one per quarter, lying along x.
    LeafSplat places every leaf on the crown and cuts its outline itself
    (LEAF_WIDTH, the same curve), so it only takes the colours from here."""
    size = SMALL
    quarter = size // 2
    # The ground between is clear, its colour the dark rim's, so filtering
    # leaves no bright fringe.
    img = Image.new('RGBA', (size, size), (40, 96, 36, 0))
    lay, d = layer(size)
    length = quarter * LEAF_FILL
    width = length * LEAF_WIDTH
    half = []
    for k in range(49):
        t = k / 48
        half.append((t * length, width * 0.5 * math.sin(math.pi * t) ** 0.8))
    outline = half + [(px, -py) for px, py in reversed(half)]
    # Like the leaves round the mushroom's collar: dark rim, body with a lit
    # half, a dark middle vein and side veins.
    for n in range(4):
        ox = (n % 2) * quarter + (quarter - length) / 2
        oy = (n // 2) * quarter + quarter / 2
        body = mix((58, 128, 46), (110, 178, 66), n / 3 * 0.8 + rng.random() * 0.2)
        vein = shade(body, 0.62)

        def at(px, py):
            return (ox + px, oy + py)
        d.polygon([at(px, py) for px, py in outline], fill=shade(body, 0.55) + (255,))
        d.polygon([at(px * 0.94 + length * 0.03, py * 0.82) for px, py in outline], fill=body + (255,))
        d.polygon([at(px * 0.94 + length * 0.03, py * 0.82) for px, py in half] + [at(length * 0.97, 0)],
                  fill=shade(body, 1.18) + (255,))
        d.line([at(length * 0.06, 0), at(length * 0.9, 0)], fill=vein + (255,), width=max(1, int(6 * K)))
        for k in range(1, 4):
            s = length * (0.12 + 0.2 * k)
            for side in (1, -1):
                d.line([at(s, 0), at(s + length * 0.1, side * width * 0.24)], fill=vein + (220,), width=max(1, int(4 * K)))
    img.alpha_composite(blur(lay, EDGE * 3))
    return img


def bark(rng):
    size = SMALL
    img = tinted((122, 82, 48), (144, 98, 58), (96, 62, 38), clouds(rng, size, 4, 70, 190))
    # Long vertical strokes and dark grooves with a lit lip, like the painted wood.
    lay, d = layer(size)
    for _ in range(210):
        x, y = rng.uniform(0, size), rng.uniform(0, size)
        h, w = rng.uniform(40, 140) * K, rng.uniform(2, 6) * K
        c = rng.choice([(160, 112, 68, 170), (100, 64, 38, 180), (176, 128, 80, 140)])
        polys(d, size, x, y, h * 1.2, [(stroke(h * 2, w * 2, math.pi / 2 + rng.uniform(-0.05, 0.05), rng.uniform(-0.03, 0.03)), c)])
    img.alpha_composite(blur(lay, EDGE))
    lay, d = layer(size)
    for _ in range(30):
        x, y = rng.uniform(0, size), rng.uniform(0, size)
        h, w = rng.uniform(60, 180) * K, rng.uniform(2.5, 4.5) * K
        groove = stroke(h, w, math.pi / 2 + rng.uniform(-0.04, 0.04), rng.uniform(-0.03, 0.03))
        polys(d, size, x, y, h * 1.2, [([(px + w * 0.8, py) for px, py in groove], (170, 122, 76, 200)),
                                        (groove, (62, 38, 22, 240))])
    img.alpha_composite(blur(lay, EDGE))
    lay, d = layer(size)
    for _ in range(2):
        x, y = rng.uniform(0, size), rng.uniform(0, size)

        def draw(sx, sy):
            d.ellipse((sx - 13 * K, sy - 19 * K, sx + 13 * K, sy + 19 * K), fill=(62, 38, 22, 255))
            d.ellipse((sx - 8 * K, sy - 13 * K, sx + 8 * K, sy + 13 * K), fill=(142, 98, 58, 255))
            d.ellipse((sx - 4 * K, sy - 7 * K, sx + 4 * K, sy + 7 * K), fill=(96, 62, 36, 255))
        wrap(draw, size, x, y, 30 * K)
    img.alpha_composite(blur(lay, EDGE))
    return img.convert('RGB')


PAINTERS = {'Grass': grass, 'Dirt': dirt, 'Sand': sand, 'Snow': snow, 'Rock': rock,
            'Leaves': leaves, 'Bark': bark}


def main():
    os.makedirs(OUT, exist_ok=True)
    for n, (name, paint) in enumerate(PAINTERS.items()):
        img = paint(random.Random(SEED * 100 + n))
        img.save(os.path.join(OUT, name + '.png'), optimize=True)
        print('%s.png: %d x %d' % (name, img.size[0], img.size[1]))
    if '--preview' in sys.argv:
        # Every texture tiled 2 x 2, to look for seams.
        tiles = [Image.open(os.path.join(OUT, n + '.png')).resize((256, 256), Image.LANCZOS) for n in PAINTERS]
        sheet = Image.new('RGB', (512 * 4, 512 * 2))
        for k, t in enumerate(tiles):
            for i in range(2):
                for j in range(2):
                    sheet.paste(t, ((k % 4) * 512 + i * 256, (k // 4) * 512 + j * 256))
        sheet.save(os.path.join(ROOT, '.work/maps/textures.png'))


if __name__ == '__main__':
    main()
