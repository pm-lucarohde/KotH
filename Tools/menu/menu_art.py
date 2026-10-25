#!/usr/bin/env python3
"""Painted art for the main menu, in the style of the mushroom figures and
Tools/maps/textures.py: saturated colours from Mushroom.png, clear shapes with
a darker rim, a light plane and broad brush strokes inside, soft edges and no
black outlines.

Writes PNGs at twice the menu's design size (1080 px high) to
KotH.Game/Menu/Art, each with room for its soft shadow (PAD design pixels on
every side; MainMenuView.axaml places them by that). Needs Pillow; seeded, so
every run paints the same.

  python3 Tools/menu/menu_art.py [--preview]
"""
import math
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
OUT = os.path.join(ROOT, 'KotH.Game/Menu/Art')
FONT = os.path.join(ROOT, 'KotH.Game/Menu/Fonts/Fredoka-Bold.ttf')
S = 2                       # pixels per design pixel
PAD = 24                    # design pixels round every picture for its shadow
SEED = 7

# The figure's colours (Mushroom.png).
RED = (196, 35, 31)
CREAM = (246, 236, 222)
GREEN = (63, 143, 53)
BROWN = (91, 58, 36)
BAMBOO = (217, 164, 76)
YELLOW = (248, 204, 64)     # a button under the pointer (Luca: lights up yellow)


def mix(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def shade(c, k):
    return tuple(max(0, min(255, int(round(v * k)))) for v in c)


def canvas(w, h):
    """A transparent picture of w x h design pixels plus the shadow's pad."""
    return Image.new('RGBA', ((w + 2 * PAD) * S, (h + 2 * PAD) * S), (0, 0, 0, 0))


def soft(img, radius):
    """Blurs premultiplied, so edges get no dark fringe."""
    return img.convert('RGBa').filter(ImageFilter.GaussianBlur(radius)).convert('RGBA')


def wobbly_rect(x0, y0, x1, y1, r, rng, rough):
    """A rounded rectangle as a polygon whose edge wanders a little, like a
    painted shape (rough in pixels)."""
    pts = []
    corners = [(x1 - r, y0 + r, -90), (x1 - r, y1 - r, 0), (x0 + r, y1 - r, 90), (x0 + r, y0 + r, 180)]
    phase = rng.uniform(0, 6.3)
    for i, (cx, cy, a0) in enumerate(corners):
        nx, ny = corners[(i + 1) % 4][:2]
        for k in range(7):
            a = math.radians(a0 + 90 * k / 6)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
        # the straight side to the next corner, in short wavy steps
        ex, ey = cx + r * math.cos(math.radians(a0 + 90)), cy + r * math.sin(math.radians(a0 + 90))
        sx, sy = nx + r * math.cos(math.radians(a0 + 90)), ny + r * math.sin(math.radians(a0 + 90))
        steps = max(2, int(math.hypot(sx - ex, sy - ey) / (18 * S)))
        nxv, nyv = math.cos(math.radians(a0 + 90)), math.sin(math.radians(a0 + 90))
        for k in range(1, steps):
            t = k / steps
            d = rough * (math.sin(phase + t * 7.1 + i) * 0.6 + rng.uniform(-0.4, 0.4))
            pts.append((ex + (sx - ex) * t + nxv * d, ey + (sy - ey) * t + nyv * d))
    return pts


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


def strokes(size, mask, rng, count, colours, length, width, turn, jitter, alpha=255):
    """Brush strokes in close tones inside the mask (an L image)."""
    lay = Image.new('RGBA', size, (0, 0, 0, 0))
    d = ImageDraw.Draw(lay)
    w, h = size
    for _ in range(count):
        x, y = rng.uniform(0, w), rng.uniform(0, h)
        ln = rng.uniform(*length) * S
        pts = stroke(ln, rng.uniform(*width) * S, turn + rng.uniform(-jitter, jitter), rng.uniform(-0.1, 0.1))
        c = rng.choice(colours)
        d.polygon([(x - ln / 2 + px, y + py) for px, py in pts], fill=c + (alpha,))
    lay = soft(lay, 1.0 * S)
    lay.putalpha(ImageChops.multiply(lay.getchannel('A'), mask))
    return lay


def shadow(img, mask, dy=6, radius=9, strength=110):
    """A soft shadow of the mask under the picture, a little lower."""
    sh = Image.new('L', mask.size, 0)
    sh.paste(mask, (0, dy * S))
    sh = sh.filter(ImageFilter.GaussianBlur(radius * S)).point(lambda v: v * strength // 255)
    dark = Image.new('RGBA', mask.size, (40, 26, 16, 0))
    dark.putalpha(sh)
    dark.alpha_composite(img)
    return dark


def painted_shape(size, pts, base, rim_k, light_k, rng, rim=4, light_at=(0.35, 0.25), grain=None):
    """The figures' recipe: a darker rim, the base inside, a light plane
    towards the top left and brush strokes; returns (picture, mask)."""
    mask = Image.new('L', size, 0)
    ImageDraw.Draw(mask).polygon(pts, fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(0.8 * S))
    inner = mask.filter(ImageFilter.MinFilter(2 * int(rim * S) + 1)).filter(ImageFilter.GaussianBlur(1.2 * S))

    img = Image.new('RGBA', size, shade(base, rim_k) + (0,))
    img.putalpha(mask)
    body = Image.new('RGBA', size, base + (0,))
    body.putalpha(inner)
    img.alpha_composite(body)

    # light plane: a big soft ellipse
    w, h = size
    light = Image.new('L', size, 0)
    cx, cy = light_at[0] * w, light_at[1] * h
    ImageDraw.Draw(light).ellipse((cx - 0.55 * w, cy - 0.6 * h, cx + 0.55 * w, cy + 0.6 * h), fill=150)
    light = ImageChops.multiply(light.filter(ImageFilter.GaussianBlur(0.12 * min(w, h))), inner)
    lit = Image.new('RGBA', size, shade(base, light_k) + (0,))
    lit.putalpha(light)
    img.alpha_composite(lit)

    if grain:
        img.alpha_composite(strokes(size, inner, rng, *grain))
    return img, mask


def board(w, h, base, rng, radius=30, rough=2.5, rim=4, grain=None):
    img = canvas(w, h)
    p = PAD * S
    pts = wobbly_rect(p, p, p + w * S, p + h * S, radius * S, rng, rough * S)
    pic, mask = painted_shape(img.size, pts, base, 0.78, 1.06, rng, rim=rim, grain=grain)
    return pic, mask


# ---------------------------------------------------------------- bamboo

def bamboo(length, thick, rng, vertical):
    """A bamboo pole like the figure's cane: ochre, lit along its middle,
    darker rings at the nodes, a leaf here and there. Returns an RGBA strip
    of length x thick design pixels (plus a little room for the leaves)."""
    L, T = length * S, thick * S
    room = int(thick * 1.6 * S)
    img = Image.new('RGBA', (L + 2 * room, T + 2 * room), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    y0, y1 = room, room + T
    x0, x1 = room, room + L
    d.rounded_rectangle((x0, y0, x1, y1), radius=T // 2, fill=shade(BAMBOO, 0.72))
    d.rounded_rectangle((x0 + 2 * S, y0 + 2 * S, x1 - 2 * S, y1 - 3 * S), radius=T // 2, fill=BAMBOO)
    d.rounded_rectangle((x0 + 4 * S, y0 + 0.22 * T, x1 - 4 * S, y0 + 0.45 * T), radius=T // 5,
                        fill=mix(BAMBOO, (255, 240, 200), 0.45))
    # nodes
    x = x0 + rng.uniform(40, 70) * S
    while x < x1 - 25 * S:
        d.rounded_rectangle((x - 3.5 * S, y0 - 1 * S, x + 3.5 * S, y1 + 1 * S), radius=2 * S, fill=shade(BAMBOO, 0.6))
        d.rectangle((x + 2.5 * S, y0 + 0.2 * T, x + 5 * S, y1 - 0.3 * T), fill=mix(BAMBOO, (255, 240, 200), 0.35))
        if rng.random() < 0.35:
            leaf(d, x, y0 + T * 0.2, rng.uniform(-2.6, -1.9) if rng.random() < 0.5 else rng.uniform(-1.2, -0.6),
                 thick * 2.2 * S, thick * 0.6 * S)
        x += rng.uniform(70, 110) * S
    img = soft(img, 0.7 * S)
    if vertical:
        img = img.rotate(90, expand=True)
    return img, room


def leaf(d, x, y, turn, length, width):
    pts = stroke(length, width, turn, 0.08)
    d.polygon([(x + px, y + py) for px, py in pts], fill=shade(GREEN, 0.8))
    pts = stroke(length * 0.92, width * 0.7, turn, 0.08)
    d.polygon([(x + px, y + py) for px, py in pts], fill=GREEN)
    vx, vy = math.cos(turn), math.sin(turn)
    d.line([(x, y), (x + vx * length * 0.8, y + vy * length * 0.8)], fill=mix(GREEN, (220, 240, 160), 0.4), width=max(1, S))


def twine(img, x, y, rng):
    """Brown cord wound round a corner where two poles cross."""
    d = ImageDraw.Draw(img)
    for k in range(4):
        a = math.radians(45 + rng.uniform(-8, 8))
        off = (k - 1.5) * 4 * S
        pts = stroke(30 * S, 4.5 * S, a, 0.02)
        d.polygon([(x - 10 * S + off + px, y - 10 * S - off + py) for px, py in pts], fill=shade(BROWN, 1.2))


def framed(w, h, rng, grain_count):
    """The cream board with a bamboo frame round it, poles crossing at the
    corners and tied there."""
    pic, mask = board(w, h, CREAM, rng, radius=26, rough=2.0, rim=3,
                      grain=(grain_count, [shade(CREAM, 0.95), shade(CREAM, 1.03), mix(CREAM, BAMBOO, 0.15)],
                             (40, 90), (6, 12), 0.15, 0.25, 120))
    thick = 22
    p = PAD * S
    over = 14
    poles = Image.new('RGBA', pic.size, (0, 0, 0, 0))
    for top in (True, False):
        strip, room = bamboo(w + 2 * over, thick, rng, False)
        y = p - thick * S // 2 if top else p + h * S - thick * S // 2
        poles.alpha_composite(strip, (p - over * S - room, y - room))
    for left in (True, False):
        strip, room = bamboo(h + 2 * over, thick, rng, True)
        x = p - thick * S // 2 if left else p + w * S - thick * S // 2
        poles.alpha_composite(strip, (x - room, p - over * S - room))
    for cx, cy in ((p, p), (p + w * S, p), (p, p + h * S), (p + w * S, p + h * S)):
        twine(poles, cx, cy, rng)
    pic.alpha_composite(poles)
    full = ImageChops.lighter(mask, poles.getchannel('A'))
    return shadow(pic, full, dy=8, radius=10, strength=120)


# ---------------------------------------------------------------- pieces

def button(rng, hot, w=380, h=66):
    if hot:
        pic, mask = board(w, h, YELLOW, rng, radius=33, rough=1.2, rim=3,
                          grain=(40, [shade(YELLOW, 0.94), mix(YELLOW, (255, 245, 200), 0.4)], (40, 90), (6, 10), 0.0, 0.2, 150))
    else:
        pic, mask = board(w, h, mix(CREAM, (255, 255, 255), 0.35), rng, radius=33, rough=1.2, rim=3,
                          grain=(40, [shade(CREAM, 0.96), shade(CREAM, 1.02)], (40, 90), (6, 10), 0.0, 0.2, 140))
    return shadow(pic, mask, dy=4, radius=5, strength=90)


def note(rng):
    """The green leaf-board under the buttons for the description."""
    w, h = 460, 112
    pic, mask = board(w, h, GREEN, rng, radius=22, rough=2.0, rim=3,
                      grain=(50, [shade(GREEN, 0.9), shade(GREEN, 1.1)], (50, 120), (6, 12), 0.35, 0.2, 130))
    return shadow(pic, mask, dy=6, radius=8, strength=100)


def ribbon(rng):
    w, h = 340, 58
    pic, mask = board(w, h, GREEN, rng, radius=18, rough=1.5, rim=3,
                      grain=(30, [shade(GREEN, 0.9), shade(GREEN, 1.1)], (40, 90), (5, 10), 0.0, 0.2, 130))
    return shadow(pic, mask, dy=5, radius=7, strength=100)


def cap(d, x, y, r):
    """A tiny fly agaric cap (the i's dot): cream gills under a red dome with dots."""
    d.ellipse((x - r * 1.2, y - r * 0.62, x + r * 1.2, y - r * 0.12), fill=mix(CREAM, BROWN, 0.3))
    d.chord((x - r * 1.3, y - r * 1.25, x + r * 1.3, y + r * 0.45), 180, 360, fill=shade(RED, 0.72))
    d.chord((x - r * 1.18, y - r * 1.12, x + r * 1.18, y + r * 0.3), 180, 360, fill=RED)
    d.ellipse((x - r * 0.8, y - r * 0.95, x - r * 0.1, y - r * 0.5), fill=mix(RED, (255, 200, 180), 0.35))
    for dx, dy, rr in ((-0.6, -0.3, 0.17), (0.05, -0.8, 0.16), (0.62, -0.38, 0.2), (0.0, -0.3, 0.12)):
        R = r * rr
        d.ellipse((x + dx * r - R, y + dy * r - R, x + dx * r + R, y + dy * r + R), fill=CREAM)


def logo(rng):
    """'King of the Hill': round, fat cream letters with a deep brown rim
    and a short brown extrusion, the dots of both i as little caps, 'of the'
    small on a green leaf between."""
    w, h = 680, 300
    img = canvas(w, h)
    p = PAD * S
    big = ImageFont.truetype(FONT, 132 * S)
    small = ImageFont.truetype(FONT, 46 * S)
    words = [('Kıng', p + 40 * S, p + 6 * S), ('Hıll', p + 250 * S, p + 150 * S)]

    text = Image.new('L', img.size, 0)
    td = ImageDraw.Draw(text)
    for word, x, y in words:
        td.text((x, y), word, font=big, fill=255)
    rim = text.filter(ImageFilter.MaxFilter(2 * 7 * S + 1))
    depth = Image.new('L', img.size, 0)
    for k in range(1, 9):
        depth = ImageChops.lighter(depth, ImageChops.offset(rim, k * S // 2, k * S))

    def fill(mask, colour):
        lay = Image.new('RGBA', img.size, colour + (0,))
        lay.putalpha(mask.filter(ImageFilter.GaussianBlur(0.7 * S)))
        img.alpha_composite(lay)

    fill(depth, shade(BROWN, 0.7))
    fill(rim, BROWN)
    # the letters: cream, warmer towards their feet, per word
    grad = Image.new('RGBA', img.size)
    gd = ImageDraw.Draw(grad)
    for word, x, y in words:
        x0, y0, x1, y1 = td.textbbox((x, y), word.replace('g', 'n'), font=big)
        for yy in range(y0, td.textbbox((x, y), word, font=big)[3] + 8 * S):
            t = min(1, max(0, (yy - y0) / (y1 - y0) * 1.4 - 0.35))
            gd.line([(x0 - 8 * S, yy), (x1 + 8 * S, yy)], fill=mix((255, 250, 238), (242, 214, 160), t) + (255,))
    grad.putalpha(ImageChops.multiply(grad.getchannel('A'), text.filter(ImageFilter.GaussianBlur(0.7 * S))))
    img.alpha_composite(grad)
    img.alpha_composite(strokes(img.size, text.filter(ImageFilter.MinFilter(5 * S + 1)), rng, 70,
                                [(255, 252, 244), (240, 220, 180)], (20, 50), (4, 8), -0.3, 0.3, 110))

    d = ImageDraw.Draw(img)
    for word, x, y in words:
        # the dotless i's top: put the cap a little above its stem
        pre = word[:word.index('ı')]
        x0 = x + d.textlength(pre, font=big)
        x1 = x0 + d.textlength('ı', font=big)
        stem_top = y + big.getbbox('ı')[1]
        cap(d, (x0 + x1) / 2, stem_top + 2 * S, 24 * S)

    # 'of the' on a small leaf
    leaf_l = Image.new('RGBA', img.size, (0, 0, 0, 0))
    ld = ImageDraw.Draw(leaf_l)
    lx, ly = p + 40 * S, p + 205 * S
    pts = stroke(190 * S, 62 * S, -0.08, 0.03)
    ld.polygon([(lx + px, ly + py) for px, py in pts], fill=shade(GREEN, 0.78))
    pts = stroke(178 * S, 50 * S, -0.08, 0.03)
    ld.polygon([(lx + 6 * S + px, ly + py) for px, py in pts], fill=GREEN)
    leaf_l = soft(leaf_l, 0.7 * S)
    img.alpha_composite(leaf_l)
    d.text((lx + 40 * S, ly - 30 * S), 'of the', font=small, fill=CREAM)

    alpha = img.getchannel('A')
    return shadow(img, alpha, dy=8, radius=10, strength=110)


def main():
    rng = random.Random(SEED)
    os.makedirs(OUT, exist_ok=True)
    pieces = {
        'Panel': lambda: framed(440, 450, rng, 160),
        'Wide': lambda: framed(820, 560, rng, 280),
        'Button': lambda: button(rng, False),
        'ButtonHot': lambda: button(rng, True),
        'SmallButton': lambda: button(rng, False, 230, 54),
        'SmallButtonHot': lambda: button(rng, True, 230, 54),
        'Note': lambda: note(rng),
        'Ribbon': lambda: ribbon(rng),
        'Logo': lambda: logo(rng),
    }
    for name, paint in pieces.items():
        img = paint()
        img.save(os.path.join(OUT, name + '.png'), optimize=True)
        print(f'{name}.png {img.size[0]}x{img.size[1]}')
    if '--preview' in sys.argv:
        sheet = Image.new('RGBA', (2200, 1500), (120, 170, 220, 255))
        x = y = 0
        row = 0
        for name in pieces:
            img = Image.open(os.path.join(OUT, name + '.png'))
            if x + img.size[0] > sheet.size[0]:
                x, y = 0, y + row
                row = 0
            sheet.alpha_composite(img, (x, y))
            x += img.size[0]
            row = max(row, img.size[1])
        sheet.save(os.path.join(ROOT, '.work/menu/art_preview.png'))


if __name__ == '__main__':
    main()
