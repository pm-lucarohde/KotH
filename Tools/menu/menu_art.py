#!/usr/bin/env python3
"""Art for the main menu in the figures' colours (Mushroom.png), clean and
chunky: smooth faces with a soft gradient, a thin darker rim, a lighter top
edge, and a solid lip underneath that gives every board and button depth,
as if moulded from plastic; the big boards sit in a frame of round
bamboo poles tied at the corners. No brush strokes, no black outlines. The title is
lettering with a bevelled face, a brown extrusion and a cast shadow.

Writes PNGs at twice the menu's design size (1080 px high) to
KotH.Game/Menu/Art, each with room for its soft shadow (PAD design pixels on
every side; MainMenuView.axaml places them by that). Needs Pillow only.

  python3 Tools/menu/menu_art.py [--preview]
"""
import math
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
OUT = os.path.join(ROOT, 'KotH.Game/Menu/Art')
FONT = os.path.join(ROOT, 'KotH.Game/Menu/Fonts/Accuratist.otf')
BOLD = 5                    # Accuratist has one thin weight: a stroke in pixels fattens the title
S = 2                       # pixels per design pixel
PAD = 24                    # design pixels round every picture for its shadow

# The figure's colours (Mushroom.png).
RED = (196, 35, 31)
CREAM = (246, 236, 222)
GREEN = (63, 143, 53)
BROWN = (91, 58, 36)
BAMBOO = (217, 164, 76)
YELLOW = (248, 204, 64)     # a button under the pointer (Luca: lights up yellow)
WHITE = (255, 255, 255)

SEED = 7

# Lip heights in design pixels; MainMenuView.axaml and MenuTheme.axaml shift
# the text up by half of them (and down again on a press).
BUTTON_LIP = 7
SMALL_LIP = 6
NOTE_LIP = 8
PRESSED_LIP = 2
# Button corners as a share of the height (Luca: less round than a pill).
CORNER = 0.24


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


def pill(size, box, radius):
    """An antialiased rounded rectangle as an L mask."""
    m = Image.new('L', size, 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m.filter(ImageFilter.GaussianBlur(0.6 * S))


def gradient(size, y0, y1, top, bottom):
    """Opaque vertical gradient from top (at y0) to bottom (at y1)."""
    g = Image.new('RGBA', size)
    d = ImageDraw.Draw(g)
    for y in range(size[1]):
        t = min(1.0, max(0.0, (y - y0) / max(1, y1 - y0)))
        d.line([(0, y), (size[0], y)], fill=mix(top, bottom, t) + (255,))
    return g


def put(img, layer, mask):
    """Composites layer onto img through mask."""
    layer = layer.copy()
    layer.putalpha(ImageChops.multiply(layer.getchannel('A'), mask))
    img.alpha_composite(layer)


def flat(size, colour, alpha=255):
    return Image.new('RGBA', size, colour + (alpha,))


def shadow(img, mask, dy=6, radius=6, strength=95):
    """A wide soft shadow plus a tight contact shadow under the picture."""
    out = Image.new('RGBA', mask.size, (0, 0, 0, 0))
    for off, blur, k in ((dy, radius, strength), (dy // 3, radius / 4, strength * 0.6)):
        sh = Image.new('L', mask.size, 0)
        sh.paste(mask, (0, int(off * S)))
        sh = sh.filter(ImageFilter.GaussianBlur(blur * S)).point(lambda v, k=k: int(v * k / 255))
        lay = Image.new('RGBA', mask.size, (40, 26, 16, 0))
        lay.putalpha(sh)
        out.alpha_composite(lay)
    out.alpha_composite(img)
    return out


def slab(w, h, face, lip, radius, sunk=0, gloss=0):
    """A board or button of w x h design pixels: a face (top and bottom
    colour of its gradient) on a lip of `lip` pixels. sunk presses the face
    down into the lip. gloss lays a soft sheen over the face's upper half."""
    img = canvas(w, h)
    p = PAD * S
    size = img.size
    x0, x1 = p, p + w * S
    fy0 = p + sunk * S
    fy1 = fy0 + (h - lip) * S
    r = radius * S

    under = pill(size, (x0, p + lip * S, x1, p + h * S), r)
    near, far = mix(face[1], BROWN, 0.22), mix(face[1], BROWN, 0.38)
    put(img, gradient(size, fy1 - r, p + h * S, near, far), under)
    top = pill(size, (x0, fy0, x1, fy1), r)
    # the lip's upper part stays visible beside the face's round corners
    side = pill(size, (x0, fy0 + r, x1, p + h * S - r // 2), r)
    put(img, gradient(size, fy0, p + h * S, near, mix(near, far, 0.6)),
        ImageChops.subtract(side, top))

    put(img, gradient(size, fy0, fy1, face[0], face[1]), top)
    # a thin darker rim and a lighter top edge
    inner = top.filter(ImageFilter.MinFilter(2 * S + 1)).filter(ImageFilter.GaussianBlur(0.6 * S))
    put(img, flat(size, shade(face[1], 0.86)), ImageChops.subtract(top, inner))
    edge = ImageChops.subtract(inner, ImageChops.offset(inner, 0, int(2.5 * S)))
    put(img, flat(size, WHITE, 170), edge.filter(ImageFilter.GaussianBlur(0.5 * S)))
    if gloss:
        sheen = Image.new('L', size, 0)
        ImageDraw.Draw(sheen).rounded_rectangle(
            (x0 + 6 * S, fy0 + 4 * S, x1 - 6 * S, fy0 + (fy1 - fy0) * 0.48), radius=r * 0.8, fill=gloss)
        put(img, flat(size, WHITE), ImageChops.multiply(sheen.filter(ImageFilter.GaussianBlur(2 * S)), inner))
    mask = ImageChops.lighter(top, under)
    return img, mask


def board(w, h, face, lip, radius, gloss=0):
    pic, mask = slab(w, h, face, lip, radius, gloss=gloss)
    return shadow(pic, mask, dy=7, radius=6, strength=100)


def button(state, w=380, h=66, lip=BUTTON_LIP):
    """Idle cream, under the pointer yellow, pressed yellow and sunk in."""
    if state == 'idle':
        face = (mix(CREAM, WHITE, 0.8), mix(CREAM, WHITE, 0.15))
        pic, mask = slab(w, h, face, lip, round(h * CORNER), gloss=70)
    else:
        face = (mix(YELLOW, WHITE, 0.35), shade(YELLOW, 0.97))
        pic, mask = slab(w, h, face, lip, round(h * CORNER), sunk=lip - PRESSED_LIP if state == 'down' else 0, gloss=80)
    return shadow(pic, mask, dy=4 if state != 'down' else 2, radius=4, strength=80)


# ---------------------------------------------------------------- bamboo

def pole(length, thick, rng, vertical):
    """A bamboo pole like the figure's cane, lit as a cylinder from above:
    nodes as darker rings with a light edge, now and then a leaf. Returns an
    RGBA strip of length x thick design pixels plus room for the leaves."""
    L, T = int(length * S), int(thick * S)
    room = int(thick * 1.6 * S)
    size = (L + 2 * room, T + 2 * room)
    img = Image.new('RGBA', size, (0, 0, 0, 0))
    y0, x0 = room, room

    def profile(k):
        col = Image.new('RGBA', size)
        d = ImageDraw.Draw(col)
        for y in range(T):
            t = (y + 0.5) / T
            light = 0.66 + 0.4 * math.sin(math.pi * t) ** 0.8 - 0.14 * t
            c = mix(shade(BAMBOO, light * k), (255, 246, 214), 0.75 * math.exp(-((t - 0.3) / 0.08) ** 2))
            d.line([(0, y0 + y), (size[0], y0 + y)], fill=c + (255,))
        return col

    put(img, profile(1.0), pill(size, (x0, y0, x0 + L, y0 + T), T // 2))
    dark, bright = profile(0.74), profile(1.12)
    leaves = Image.new('RGBA', size, (0, 0, 0, 0))
    x = x0 + rng.uniform(40, 70) * S
    while x < x0 + L - 25 * S:
        put(img, dark, pill(size, (x - 3.5 * S, y0 - S, x + 3.5 * S, y0 + T + S), 2 * S))
        put(img, bright, pill(size, (x + 3.5 * S, y0 + S, x + 5.5 * S, y0 + T - S), S))
        if rng.random() < 0.4:
            turn = -rng.uniform(0.35, 0.6) if rng.random() < 0.5 else math.pi + rng.uniform(0.35, 0.6)
            leaf(leaves, x, y0 + T * 0.3, turn, thick * 1.5 * S, thick * 0.55 * S)
        x += rng.uniform(70, 110) * S
    img.alpha_composite(leaves)
    if vertical:
        img = img.rotate(90, expand=True)
    return img, room


def leaf(img, x, y, turn, length, width):
    """A clean leaf: darker edge, lighter towards its tip, a light midrib."""
    size = img.size
    shape = Image.new('L', size, 0)
    ImageDraw.Draw(shape).polygon([(x + a, y + b) for a, b in leaf_shape(length, width, turn)], fill=255)
    shape = shape.filter(ImageFilter.GaussianBlur(0.6 * S))
    put(img, flat(size, shade(GREEN, 0.72)), shape)
    inner = shape.filter(ImageFilter.MinFilter(2 * S + 1)).filter(ImageFilter.GaussianBlur(0.5 * S))
    put(img, gradient(size, y - width, y + width, mix(GREEN, WHITE, 0.2), GREEN), inner)
    d = ImageDraw.Draw(img)
    vx, vy = math.cos(turn), math.sin(turn)
    d.line([(x, y), (x + vx * length * 0.8, y + vy * length * 0.8)], fill=mix(GREEN, (220, 240, 160), 0.45), width=S)


def twine(img, x, y):
    """Brown cord wound round a corner where two poles cross."""
    d = ImageDraw.Draw(img)
    cord, light = mix(BROWN, BAMBOO, 0.25), mix(BROWN, (255, 230, 190), 0.4)
    for k in range(3):
        off = (k - 1) * 4.5 * S
        a = (x - 11 * S + off, y + 11 * S + off)
        b = (x + 11 * S + off, y - 11 * S + off)
        d.line([a, b], fill=shade(cord, 0.75), width=int(4.5 * S))
        d.line([(a[0], a[1] - S), (b[0], b[1] - S)], fill=cord, width=int(3 * S))
        d.line([(a[0] + 2 * S, a[1] - 3 * S), (b[0] - 2 * S, b[1] + S)], fill=light, width=S)


def framed(w, h, rng):
    """A clean cream board in a frame of bamboo poles crossing and tied at
    the corners."""
    pic, mask = slab(w, h, (mix(CREAM, WHITE, 0.45), shade(CREAM, 0.97)), 0, 26)
    thick, over = 22, 14
    p = PAD * S
    poles = Image.new('RGBA', pic.size, (0, 0, 0, 0))
    for top in (True, False):
        strip, room = pole(w + 2 * over, thick, rng, False)
        y = p - thick * S // 2 if top else p + h * S - thick * S // 2
        poles.alpha_composite(strip, (p - over * S - room, y - room))
    for left in (True, False):
        strip, room = pole(h + 2 * over, thick, rng, True)
        x = p - thick * S // 2 if left else p + w * S - thick * S // 2
        poles.alpha_composite(strip, (x - room, p - over * S - room))
    for cx, cy in ((p, p), (p + w * S, p), (p, p + h * S), (p + w * S, p + h * S)):
        twine(poles, cx, cy)
    pic.alpha_composite(poles)
    return shadow(pic, ImageChops.lighter(mask, poles.getchannel('A')), dy=7, radius=6, strength=110)


# ---------------------------------------------------------------- title

def bevel(mask, width, strength):
    """Light and dark masks of a rounded bevel width pixels wide on the
    mask's edge, lit from the top left: the blurred mask is the height, its
    slope towards the light the shading."""
    height = mask.filter(ImageFilter.GaussianBlur(width)).point(lambda v: max(0, min(255, (v - 128) * 2)))
    slope = height.filter(ImageFilter.Kernel((3, 3), [2, 1, 0, 1, 0, -1, 0, -1, -2], 1, 128))
    lit = slope.point(lambda v: max(0, min(255, int((v - 128) * strength))))
    dark = slope.point(lambda v: max(0, min(255, int((128 - v) * strength))))
    return ImageChops.multiply(lit, mask), ImageChops.multiply(dark, mask)


def extrude(img, mask, depth, near, far, step=(0.35, 1.0)):
    """Stacks the mask down and to the right, depth design pixels deep, far
    colour at the back, near at the front: the letters' solid sides."""
    n = depth * S
    for k in range(n, 0, -1):
        lay = flat(img.size, mix(near, far, k / n))
        put(img, lay, ImageChops.offset(mask, int(k * step[0]), int(k * step[1])))


def cap(d, x, y, r):
    """A tiny fly agaric cap (the i's dot): cream gills under a red dome with dots."""
    d.ellipse((x - r * 1.2, y - r * 0.62, x + r * 1.2, y - r * 0.12), fill=mix(CREAM, BROWN, 0.3))
    d.chord((x - r * 1.3, y - r * 1.25, x + r * 1.3, y + r * 0.45), 180, 360, fill=shade(RED, 0.72))
    d.chord((x - r * 1.18, y - r * 1.12, x + r * 1.18, y + r * 0.3), 180, 360, fill=RED)
    d.ellipse((x - r * 0.8, y - r * 0.95, x - r * 0.1, y - r * 0.5), fill=mix(RED, (255, 200, 180), 0.35))
    for dx, dy, rr in ((-0.6, -0.3, 0.17), (0.05, -0.8, 0.16), (0.62, -0.38, 0.2), (0.0, -0.3, 0.12)):
        R = r * rr
        d.ellipse((x + dx * r - R, y + dy * r - R, x + dx * r + R, y + dy * r + R), fill=CREAM)


def leaf_shape(length, width, turn):
    """A pointed leaf along +x, turned by turn radians."""
    pts = []
    for k in range(17):
        t = k / 16
        pts.append((t * length, width * 0.5 * math.sin(math.pi * t) ** 0.6))
    for k in range(16, -1, -1):
        t = k / 16
        pts.append((t * length, -width * 0.5 * math.sin(math.pi * t) ** 0.6))
    c, s = math.cos(turn), math.sin(turn)
    return [(px * c - py * s, px * s + py * c) for px, py in pts]


def logo():
    """'King of the Hill': fat cream letters with a bevelled face, a brown
    rim and a deep brown extrusion, the dots of both i as little caps,
    'of the' small on a green leaf of its own depth."""
    w, h = 680, 300
    img = canvas(w, h)
    size = img.size
    p = PAD * S
    big = ImageFont.truetype(FONT, 135 * S)
    small = ImageFont.truetype(FONT, 50 * S)
    words = [('Kıng', p + 50 * S, p + 4 * S), ('Hıll', p + 260 * S, p + 144 * S)]

    text = Image.new('L', size, 0)
    td = ImageDraw.Draw(text)
    for word, x, y in words:
        td.text((x, y), word, font=big, fill=255, stroke_width=BOLD, stroke_fill=255)
    text = text.filter(ImageFilter.GaussianBlur(0.5 * S))
    rim = text.filter(ImageFilter.MaxFilter(2 * 6 * S + 1)).filter(ImageFilter.GaussianBlur(0.7 * S))

    # the leaf behind 'Hıll', with 'of the' on it
    lx, ly = p + 46 * S, p + 204 * S
    leaf = Image.new('L', size, 0)
    ImageDraw.Draw(leaf).polygon([(lx + a, ly + b) for a, b in leaf_shape(196 * S, 64 * S, -0.08)], fill=255)
    leaf = leaf.filter(ImageFilter.GaussianBlur(0.7 * S))
    extrude(img, leaf, 7, shade(GREEN, 0.62), shade(GREEN, 0.42))
    put(img, gradient(size, ly - 30 * S, ly + 30 * S, mix(GREEN, WHITE, 0.18), shade(GREEN, 0.92)), leaf)
    lit, dark = bevel(leaf, 3 * S, 2.0)
    put(img, flat(size, WHITE, 150), lit)
    put(img, flat(size, shade(GREEN, 0.5), 150), dark)
    d = ImageDraw.Draw(img)
    tx, ty = lx + 42 * S, ly - 31 * S
    d.text((tx + S, ty + 3 * S), 'of the', font=small, fill=shade(GREEN, 0.5), stroke_width=S, stroke_fill=shade(GREEN, 0.5))
    d.text((tx, ty), 'of the', font=small, fill=CREAM, stroke_width=S, stroke_fill=CREAM)

    # the letters' body: extrusion, then rim, then face
    extrude(img, rim, 16, shade(BROWN, 0.85), shade(BROWN, 0.48))
    put(img, flat(size, BROWN), rim)
    lit, dark = bevel(rim, 2.5 * S, 1.6)
    put(img, flat(size, mix(BROWN, WHITE, 0.45), 160), ImageChops.subtract(lit, text))

    face = Image.new('RGBA', size)
    fd = ImageDraw.Draw(face)
    for word, x, y in words:
        x0, y0, x1, _ = td.textbbox((x, y), word.replace('g', 'n'), font=big)
        y1 = td.textbbox((x, y), word, font=big)[3]
        for yy in range(y0 - 4 * S, y1 + 8 * S):
            t = min(1, max(0, (yy - y0) / (y1 - y0)))
            fd.line([(x0 - 8 * S, yy), (x1 + 8 * S, yy)], fill=mix((255, 252, 244), (240, 206, 146), t ** 1.3) + (255,))
    put(img, face, text)
    lit, dark = bevel(text, 4 * S, 2.6)
    put(img, flat(size, WHITE, 230), lit)
    put(img, flat(size, (150, 96, 52), 170), dark)

    for word, x, y in words:
        # the dotless i's top: put the cap a little above its stem
        pre = word[:word.index('ı')]
        x0 = x + d.textlength(pre, font=big)
        x1 = x0 + d.textlength('ı', font=big)
        stem_top = y + big.getbbox('ı', stroke_width=BOLD)[1]
        cx, cy, r = (x0 + x1) / 2, stem_top + 2 * S, 24 * S
        stalk = Image.new('L', size, 0)
        ImageDraw.Draw(stalk).chord((cx - r * 1.3, cy - r * 1.25, cx + r * 1.3, cy + r * 0.45), 180, 360, fill=255)
        extrude(img, stalk.filter(ImageFilter.GaussianBlur(0.6 * S)), 5, shade(RED, 0.55), shade(RED, 0.38))
        cap(d, cx, cy, r)

    return shadow(img, img.getchannel('A'), dy=7, radius=6, strength=105)


def main():
    rng = random.Random(SEED)
    os.makedirs(OUT, exist_ok=True)
    pieces = {
        'Panel': lambda: framed(440, 450, rng),
        'Wide': lambda: framed(820, 560, rng),
        'Button': lambda: button('idle'),
        'ButtonHot': lambda: button('hot'),
        'ButtonDown': lambda: button('down'),
        'SmallButton': lambda: button('idle', 230, 54, SMALL_LIP),
        'SmallButtonHot': lambda: button('hot', 230, 54, SMALL_LIP),
        'SmallButtonDown': lambda: button('down', 230, 54, SMALL_LIP),
        'Note': lambda: board(460, 112, (mix(GREEN, WHITE, 0.12), shade(GREEN, 0.95)), NOTE_LIP, 22, gloss=28),
        'Ribbon': lambda: board(340, 58, (mix(GREEN, WHITE, 0.12), shade(GREEN, 0.95)), BUTTON_LIP, round(58 * CORNER), gloss=34),
        'Logo': logo,
    }
    for name, paint in pieces.items():
        img = paint()
        img.save(os.path.join(OUT, name + '.png'), optimize=True)
        print(f'{name}.png {img.size[0]}x{img.size[1]}')
    if '--preview' in sys.argv:
        sheet = Image.new('RGBA', (3600, 2600), (120, 170, 220, 255))
        x = y = row = 0
        for name in pieces:
            img = Image.open(os.path.join(OUT, name + '.png'))
            if x + img.size[0] > sheet.size[0]:
                x, y, row = 0, y + row, 0
            sheet.alpha_composite(img, (x, y))
            x += img.size[0]
            row = max(row, img.size[1])
        sheet.save(os.path.join(ROOT, '.work/menu/art_preview.png'))


if __name__ == '__main__':
    main()
