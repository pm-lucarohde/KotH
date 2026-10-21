#!/usr/bin/env python3
"""The block mountain of map "Neu", after a Minecraft map (Luca, 21.10.2026).

Writes only what belongs to this script, never what Game Studio edits:
  KotH.Game/Resources/Models/Mountain.glb           what is drawn (vertex colours with AO)
  KotH.Shared/Assets/Maps/Neu/NeuTerrain.sdprefab   the collision, boxes in 8 x 8 m chunks
  .work/maps/mountain.png                           a map from above, for a look

Markers, zone and bot settings stay in NeuCollision (Game Studio); the
script prints where the summit and the meadow are. Blocks are 1 m: one
block up is a jump, two a double jump, three a wall. Pure Python, seeded.

  python3 Tools/maps/mountain.py [--seed N]
"""
import math
import os
import heapq
import random
import struct
import sys
import uuid
import zlib
from collections import deque

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))

SEED = 7
HALF = 106                 # cells from the centre to the edge of the grid
ISLAND = 90.0              # coast radius, varied by COAST
COAST = 4.0
BEACH = 5.0                # sand this far in from the coast
MOUNTAIN = 68.0            # foot of the mountain, varied by FOOT
FOOT = 4.0
GRASS_Y = 2                # top of the meadow
SAND_Y = 1
SUMMIT_Y = 136             # top of the summit plateau (Luca: less steep than 190 m so a way up
                           # can be found anywhere, then 15 % lower; foot shrunk with it)
PLATEAU = 4                # plateau: 2 * PLATEAU + 2 cells across, centred on the origin
PROFILE = 1.1              # height = (1 - r / foot) ^ PROFILE: about two blocks per metre,
                           # a little steeper towards the top; steeper, and no way leads up
RIDGES = 9.0               # depth of the ridges running down the slope
RIDGE_SPACING = 3.0        # metres between ridges around the mountain
RIDGE_LENGTH = 13.0        # metres a ridge runs down before it changes
COLUMN = 1                 # the slope's columns are this many blocks across (2: hardly a way up)
COLUMNS = 3.0              # fine columns between the ridges, as in the video
COLUMN_SPACING = 1.3
COLUMN_LENGTH = 5.0
BUMPS = 4.0                # larger lumps
SNOWLINE = 0.55            # share of the summit height, varied by SNOW_NOISE
SNOW_NOISE = 9.0
CAPS = 8.0                 # snow caps on tops this far below the snow line
SOUTH = math.pi / 2        # +Z, where the meadow with the spawns is
SECTORS = 12               # directions round the foot the climb is checked from
MIN_SECTORS = 9            # the summit must be reached from this many of them
TREES = 36
SPAWN_GAP = 7.0            # first spawn row this far out from the foot in the south
TREE_GAP = 7.0
CHUNK = 8
FLOOR = -2                 # columns reach down to here
WATER_Y = 0.4

# Colours in sRGB; the glb carries them linear.
STONE = (0.55, 0.51, 0.45)
SNOW = (0.93, 0.95, 0.97)
GRASS = (0.37, 0.55, 0.23)
DIRT = (0.47, 0.35, 0.23)
SAND = (0.86, 0.81, 0.60)
WOOD = (0.42, 0.31, 0.18)
LEAVES = (0.24, 0.42, 0.16)
AO = (0.52, 0.68, 0.84, 1.0)   # brightness by unoccluded neighbours, as in Minecraft

PREFAB_ID = '6E1D3F0A-8B52-4C1D-9A47-2F3E5D6C7B81'
NAMESPACE = uuid.UUID('9b0f4f63-3b3a-4e47-8a8e-6b3f7c2d1e5a')


def lattice(seed, *k):
    """A random value in [0, 1) for a lattice point, the same every run."""
    h = seed & 0xFFFFFFFF
    for v in k:
        h = (h ^ (v & 0xFFFFFFFF)) * 0x9E3779B1 & 0xFFFFFFFF
        h ^= h >> 15
        h = h * 0x85EBCA77 & 0xFFFFFFFF
        h ^= h >> 13
    return h / 4294967296.0


def noise(seed, u, v, wrap=0):
    """Smooth value noise in [-1, 1]; wraps every 'wrap' cells in u if given."""
    iu, iv = math.floor(u), math.floor(v)
    fu, fv = u - iu, v - iv
    su, sv = fu * fu * (3 - 2 * fu), fv * fv * (3 - 2 * fv)

    def at(a, b):
        return lattice(seed, a % wrap if wrap else a, b) * 2 - 1
    top = at(iu, iv) + (at(iu + 1, iv) - at(iu, iv)) * su
    bottom = at(iu, iv + 1) + (at(iu + 1, iv + 1) - at(iu, iv + 1)) * su
    return top + (bottom - top) * sv


def ring(seed, angle, cells):
    """Noise around a circle, seamless."""
    return noise(seed, angle / (2 * math.pi) * cells, 0.5, wrap=cells)


class Terrain:
    def __init__(self, seed):
        self.seed = seed
        self.rng = random.Random(seed)
        self.height = {}      # (x, z) -> top y of the column
        self.material = {}    # (x, z) -> (top colour, side colour)
        self.tint = {}        # (x, z) -> brightness of that column
        self.trees = set()    # (x, y, z) of wood and leaf blocks
        self.wood = set()
        self.build()

    # Cell (x, z) spans x..x+1 and z..z+1; its centre is +0.5.
    def polar(self, x, z):
        cx, cz = x + 0.5, z + 0.5
        return math.hypot(cx, cz), math.atan2(cz, cx) % (2 * math.pi)

    def foot(self, angle):
        return MOUNTAIN + FOOT * ring(self.seed + 1, angle, 7)

    def coast(self, angle):
        return ISLAND + COAST * ring(self.seed + 2, angle, 9)

    def build(self):
        s = self.seed
        for x in range(-HALF, HALF):
            for z in range(-HALF, HALF):
                r, a = self.polar(x, z)
                coast = self.coast(a)
                if r > coast:
                    continue
                foot = self.foot(a)
                if r > coast - BEACH:
                    self.height[x, z] = SAND_Y
                elif r >= foot:
                    self.height[x, z] = GRASS_Y
                elif max(abs(x + 0.5), abs(z + 0.5)) <= PLATEAU + 0.5:
                    self.height[x, z] = SUMMIT_Y
                else:
                    # The slope is shaped per COLUMN x COLUMN group: every top a body can stand on.
                    gx, gz = x // COLUMN * COLUMN, z // COLUMN * COLUMN
                    r, a = self.polar(gx + (COLUMN - 1) / 2, gz + (COLUMN - 1) / 2)
                    t = min(1.0, max(0.0, r - PLATEAU - 1) / (foot - PLATEAU - 1))
                    h = GRASS_Y + (SUMMIT_Y - GRASS_Y) * (1 - t) ** PROFILE
                    # Ridges: around the mountain short, down the slope long.
                    around = a * foot / RIDGE_SPACING
                    cells = max(1, round(2 * math.pi * foot / RIDGE_SPACING))
                    rough = 0.6 + 0.4 * (1 - t)
                    h += RIDGES * rough * noise(s + 3, around, r / RIDGE_LENGTH, wrap=cells)
                    fine = max(1, round(2 * math.pi * foot / COLUMN_SPACING))
                    h += COLUMNS * noise(s + 10, a * foot / COLUMN_SPACING, r / COLUMN_LENGTH, wrap=fine)
                    h += BUMPS * noise(s + 4, gx / 11.0, gz / 11.0)
                    h += (-1, 0, 0, 0, 1)[int(lattice(s + 11, gx, gz) * 5)]
                    self.height[x, z] = max(GRASS_Y, min(SUMMIT_Y - 1, round(h)))
        self.fill_slots()
        self.drain_traps()
        self.paint()
        self.plant()

    def fill_slots(self):
        """Slots one or two blocks wide between walls two blocks higher are
        raised to the lower wall: too narrow for a body, they would trap it."""
        for _ in range(6):
            changed = False
            for (x, z) in sorted(self.height):
                for dx, dz in ((1, 0), (0, 1)):
                    for w in (1, 2):
                        run = [(x + i * dx, z + i * dz) for i in range(w)]
                        before, after = (x - dx, z - dz), (x + w * dx, z + w * dz)
                        if any(c not in self.height for c in run):
                            continue
                        if before not in self.height or after not in self.height:
                            continue
                        top = max(self.height[c] for c in run)
                        wall = min(self.height[before], self.height[after])
                        if wall - top >= 2:
                            for c in run:
                                self.height[c] = wall
                            changed = True
            if not changed:
                break

    def escapable(self):
        """Every column from which the beach can be reached: up at most two
        blocks at a time (a double jump), down any."""
        out = {c for c, h in self.height.items() if h <= SAND_Y}
        todo = deque(sorted(out))
        while todo:
            x, z = todo.popleft()
            h = self.height[x, z]
            for b in ((x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1)):
                if b in self.height and b not in out and h - self.height[b] <= 2:
                    out.add(b)
                    todo.append(b)
        return out

    def drain_traps(self):
        """Raises every pit nobody gets out of to one block under the lowest
        rim that leads out, until there is none."""
        self.raised = 0
        for _ in range(200):
            out = self.escapable()
            edge = {}
            for (x, z), h in self.height.items():
                if (x, z) in out:
                    continue
                rims = [self.height[b] for b in ((x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1)) if b in out]
                if rims:
                    edge[x, z] = min(rims) - 1
            if not edge:
                return
            for c, y in edge.items():
                if y > self.height[c]:
                    self.height[c] = y
                    self.raised += 1
        sys.exit('Abbruch: Fallen lassen sich nicht auffüllen')

    def paint(self):
        s = self.seed
        snowline = SNOWLINE * SUMMIT_Y
        for (x, z), h in self.height.items():
            r, a = self.polar(x, z)
            if h <= SAND_Y:
                self.material[x, z] = (SAND, SAND)
                self.tint[x, z] = 1.0
            elif h == GRASS_Y and r >= self.foot(a):
                self.material[x, z] = (GRASS, DIRT)
                self.tint[x, z] = 1.0
            else:
                line = snowline + SNOW_NOISE * noise(s + 5, x / 9.0, z / 9.0)
                if h >= line:
                    self.material[x, z] = (SNOW, SNOW)
                elif h >= line - CAPS and lattice(s + 6, x, z) < 0.35:
                    self.material[x, z] = (SNOW, STONE)
                else:
                    self.material[x, z] = (STONE, STONE)
                self.tint[x, z] = 0.93 + 0.07 * lattice(s + 7, x, z) + 0.04 * noise(s + 8, x / 5.0, z / 5.0)

    def plant(self):
        """Trees on the meadow, never by the spawns."""
        rng = random.Random(self.seed + 9)
        spots = []
        meadow = [c for c, h in self.height.items() if h == GRASS_Y and self.material[c][0] == GRASS]
        meadow.sort()
        tries = 0
        spawn = self.foot(SOUTH) + SPAWN_GAP
        while len(spots) < TREES and tries < 20000:
            tries += 1
            x, z = rng.choice(meadow)
            r, a = self.polar(x, z)
            if r < self.foot(a) + 4 or r > self.coast(a) - BEACH - 3:
                continue
            if -10 <= x <= 10 and spawn - 8 <= z <= spawn + 14:     # around the spawns
                continue
            if any(math.hypot(x - sx, z - sz) < TREE_GAP for sx, sz in spots):
                continue
            spots.append((x, z))
        for x, z in spots:
            trunk = rng.randint(4, 6)
            base = self.height[x, z]
            top = base + trunk
            for y in range(base, top):
                self.wood.add((x, y, z))
            for y, reach in ((top - 2, 2), (top - 1, 2), (top, 1), (top + 1, 1)):
                for dx in range(-reach, reach + 1):
                    for dz in range(-reach, reach + 1):
                        corner = abs(dx) == reach and abs(dz) == reach
                        if corner and (y >= top or rng.random() < 0.5):
                            continue
                        if y == top + 1 and abs(dx) + abs(dz) > 1:
                            continue
                        if (x + dx, y, z + dz) not in self.wood:
                            self.trees.add((x + dx, y, z + dz))

    def solid(self, x, y, z):
        h = self.height.get((x, z))
        if h is not None and FLOOR <= y < h:
            return True
        return (x, y, z) in self.wood or (x, y, z) in self.trees


def climbable(t, starts, limit, near=None):
    """Breadth-first over the columns: up 'limit' blocks at most (one: a
    jump, two: a double jump), down any. With 'near', the meadow counts
    only that close to the first start, so a sector cannot walk round."""
    seen = set(starts)
    todo = deque(starts)
    while todo:
        x, z = todo.popleft()
        h = t.height[x, z]
        for n in ((x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1)):
            if n in seen or n not in t.height or t.height[n] - h > limit:
                continue
            if near and t.height[n] <= GRASS_Y and math.hypot(n[0] - starts[0][0], n[1] - starts[0][1]) > near:
                continue
            seen.add(n)
            todo.append(n)
    return seen


def fastest_climb(t, start):
    """Seconds from 'start' to the summit along the quickest columns: a metre
    on foot 0.2 s, a jump up 0.65 s, a double jump 1 s; none if no way."""
    dist = {start: 0.0}
    todo = [(0.0, start)]
    while todo:
        d, c = heapq.heappop(todo)
        if d > dist[c]:
            continue
        if t.height[c] == SUMMIT_Y:
            return d
        x, z = c
        for n in ((x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1)):
            if n not in t.height or t.height[n] - t.height[c] > 2:
                continue
            up = t.height[n] - t.height[c]
            nd = d + (0.2 if up <= 0 else 0.65 if up == 1 else 1.0)
            if nd < dist.get(n, 1e9):
                dist[n] = nd
                heapq.heappush(todo, (nd, n))
    return None


def check_climb(t):
    """No paths are laid: the slope itself has to let one up. Reports from
    how many of SECTORS directions round the foot the summit is reached,
    how much of the mountain one gets onto, and the quickest way up."""
    summit = {c for c, h in t.height.items() if h == SUMMIT_Y}
    mountain = [c for c in t.height if t.polar(*c)[0] < t.foot(t.polar(*c)[1])]
    meadow = [c for c, h in t.height.items() if h <= GRASS_Y and c not in mountain]
    shares = {}
    for limit in (1, 2):
        seen = climbable(t, meadow, limit)
        shares[limit] = (bool(summit & seen), sum(1 for c in mountain if c in seen) / len(mountain))
    sectors = 0
    for k in range(SECTORS):
        a = SOUTH + k * 2 * math.pi / SECTORS
        r = t.foot(a) + 2
        start = (math.floor(r * math.cos(a)), math.floor(r * math.sin(a)))
        sectors += bool(summit & climbable(t, [start], 2, near=8))
    spawn = (0, math.floor(t.foot(SOUTH) + SPAWN_GAP))
    return shares, sectors, fastest_climb(t, spawn)


# ---------- the mesh ----------

def linear(c):
    return tuple(v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in c)


class Mesh:
    def __init__(self):
        self.pos, self.nrm, self.col, self.idx = [], [], [], []

    def quad(self, corners, normal, colours):
        """Four corners counter-clockwise seen from the normal's side."""
        base = len(self.pos)
        for p, c in zip(corners, colours):
            self.pos.append(p)
            self.nrm.append(normal)
            self.col.append(c)
        # Split along the brighter diagonal, as Minecraft does against AO seams.
        if colours[0][0] + colours[2][0] >= colours[1][0] + colours[3][0]:
            self.idx += [base, base + 1, base + 2, base, base + 2, base + 3]
        else:
            self.idx += [base, base + 1, base + 3, base + 1, base + 2, base + 3]


def corner_ao(t, face, ux, uy, uz, vx, vy, vz, su, sv):
    """Minecraft's vertex AO: the two side blocks and the corner block in
    front of the face; both sides set is fully dark."""
    fx, fy, fz = face
    s1 = t.solid(fx + su * ux, fy + su * uy, fz + su * uz)
    s2 = t.solid(fx + sv * vx, fy + sv * vy, fz + sv * vz)
    c = t.solid(fx + su * ux + sv * vx, fy + su * uy + sv * vy, fz + su * uz + sv * vz)
    return 0 if s1 and s2 else 3 - (s1 + s2 + c)


def shade(colour, tint, ao):
    lin = linear(colour)
    k = tint * AO[ao]
    return (lin[0] * k, lin[1] * k, lin[2] * k, 1.0)


# The six directions with their face plane: normal, then the two edge axes
# u and v so that u x v = normal (corners counter-clockwise from outside).
FACES = {
    (0, 1, 0): ((0, 0, 1), (1, 0, 0)),
    (0, -1, 0): ((1, 0, 0), (0, 0, 1)),
    (1, 0, 0): ((0, 1, 0), (0, 0, 1)),
    (-1, 0, 0): ((0, 0, 1), (0, 1, 0)),
    (0, 0, 1): ((1, 0, 0), (0, 1, 0)),
    (0, 0, -1): ((0, 1, 0), (1, 0, 0)),
}


def block_face(t, mesh, x, y, z, n, colour, tint):
    """One face of block (x, y, z) towards n, with AO at every corner."""
    u, v = FACES[n]
    face = (x + n[0], y + n[1], z + n[2])
    # The face's corners in the block's unit cube, counter-clockwise.
    centre = (x + 0.5 + 0.5 * n[0], y + 0.5 + 0.5 * n[1], z + 0.5 + 0.5 * n[2])
    corners, colours = [], []
    for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
        p = tuple(centre[i] + 0.5 * su * u[i] + 0.5 * sv * v[i] for i in range(3))
        ao = corner_ao(t, face, *u, *v, su, sv)
        corners.append(p)
        colours.append(shade(colour, tint, ao))
    mesh.quad(corners, n, colours)
    return [c[0] for c in colours]


def block_tint(t, x, y, z):
    """Each rock or snow block a little lighter or darker, so the blocks read."""
    tint = t.tint[x, z]
    return tint if tint == 1.0 else tint * (0.92 + 0.08 * lattice(t.seed + 12, x, y, z))


def build_mesh(t):
    mesh = Mesh()
    # Tops: flat open ground merges into rectangles, everything else per block.
    plain = {}
    for (x, z), h in t.height.items():
        top, side = t.material[x, z]
        aos = [corner_ao(t, (x, h, z), 0, 0, 1, 1, 0, 0, su, sv) for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        if t.tint[x, z] == 1.0 and aos == [3, 3, 3, 3]:
            plain[x, z] = (h, top)
        else:
            block_face(t, mesh, x, h - 1, z, (0, 1, 0), top, block_tint(t, x, h - 1, z))
    done = set()
    for x, z in sorted(plain):
        if (x, z) in done:
            continue
        key = plain[x, z]
        w = 1
        while (x + w, z) in plain and (x + w, z) not in done and plain[x + w, z] == key:
            w += 1
        d = 1
        while all((x + i, z + d) in plain and (x + i, z + d) not in done and plain[x + i, z + d] == key for i in range(w)):
            d += 1
        for i in range(w):
            for j in range(d):
                done.add((x + i, z + j))
        h, colour = key
        c = shade(colour, 1.0, 3)
        mesh.quad([(x, h, z), (x, h, z + d), (x + w, h, z + d), (x + w, h, z)], (0, 1, 0), [c] * 4)

    # Sides: per column and direction a strip from the neighbour's top up;
    # runs of blocks without any shading merge into one quad.
    for (x, z), h in t.height.items():
        top, side = t.material[x, z]
        tint = t.tint[x, z]
        for n in ((1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1)):
            nb = t.height.get((x + n[0], z + n[2]), FLOOR)
            low = max(nb, -1)
            run = None
            for y in range(low, h):
                if t.solid(x + n[0], y, z + n[2]):
                    run = flush(mesh, run, x, z, n)
                    continue
                u, v = FACES[n]
                face = (x + n[0], y, z + n[2])
                aos = [corner_ao(t, face, *u, *v, su, sv) for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
                # Grass columns show dirt with the grass colour on their top block.
                colour = top if (side == DIRT and y == h - 1) else side
                # Rock and snow show every block, as in the video; earth merges.
                if aos == [3, 3, 3, 3] and colour == side and side in (DIRT, SAND):
                    if run is None:
                        run = [y, y + 1, shade(side, tint, 3)]
                    else:
                        run[1] = y + 1
                    continue
                run = flush(mesh, run, x, z, n)
                block_face(t, mesh, x, y, z, n, colour, block_tint(t, x, y, z))
            flush(mesh, run, x, z, n)

    # Trees: every open face of every block.
    for (x, y, z) in sorted(t.wood | t.trees):
        colour = WOOD if (x, y, z) in t.wood else LEAVES
        for n in FACES:
            if not t.solid(x + n[0], y + n[1], z + n[2]):
                block_face(t, mesh, x, y, z, n, colour, 1.0)
    return mesh


def flush(mesh, run, x, z, n):
    """Closes a merged run of plain side blocks as one quad."""
    if run is None:
        return None
    y0, y1, c = run
    u, v = FACES[n]
    cx, cz = x + 0.5 + 0.5 * n[0], z + 0.5 + 0.5 * n[2]
    corners = []
    for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
        px = cx + 0.5 * su * u[0] + 0.5 * sv * v[0]
        pz = cz + 0.5 * su * u[2] + 0.5 * sv * v[2]
        ty = su * u[1] + sv * v[1]
        corners.append((px, y1 if ty > 0 else y0, pz))
    mesh.quad(corners, n, [c] * 4)
    return None


def write_glb(mesh, path):
    """A glTF binary: one mesh, one primitive, material "Terrain"."""
    def pack(fmt, rows):
        return b''.join(struct.pack(fmt, *r) for r in rows)
    pos = pack('<3f', mesh.pos)
    nrm = pack('<3f', mesh.nrm)
    col = pack('<4f', mesh.col)
    idx = struct.pack('<%dI' % len(mesh.idx), *mesh.idx)
    blobs = [pos, nrm, col, idx]
    views, offset = [], 0
    for i, b in enumerate(blobs):
        views.append({'buffer': 0, 'byteOffset': offset, 'byteLength': len(b),
                      'target': 34963 if i == 3 else 34962})
        offset += len(b)
    lo = [min(p[i] for p in mesh.pos) for i in range(3)]
    hi = [max(p[i] for p in mesh.pos) for i in range(3)]
    n = len(mesh.pos)
    gltf = {
        'asset': {'version': '2.0', 'generator': 'KotH Tools/maps/mountain.py'},
        'scene': 0, 'scenes': [{'nodes': [0]}],
        'nodes': [{'name': 'Mountain', 'mesh': 0}],
        'meshes': [{'name': 'Mountain', 'primitives': [{
            'attributes': {'POSITION': 0, 'NORMAL': 1, 'COLOR_0': 2}, 'indices': 3, 'material': 0}]}],
        'materials': [{'name': 'Terrain', 'pbrMetallicRoughness': {'metallicFactor': 0.0, 'roughnessFactor': 0.9}}],
        'buffers': [{'byteLength': offset}],
        'bufferViews': views,
        'accessors': [
            {'bufferView': 0, 'componentType': 5126, 'count': n, 'type': 'VEC3', 'min': lo, 'max': hi},
            {'bufferView': 1, 'componentType': 5126, 'count': n, 'type': 'VEC3'},
            {'bufferView': 2, 'componentType': 5126, 'count': n, 'type': 'VEC4'},
            {'bufferView': 3, 'componentType': 5125, 'count': len(mesh.idx), 'type': 'SCALAR'},
        ],
    }
    import json
    js = json.dumps(gltf, separators=(',', ':')).encode()
    js += b' ' * (-len(js) % 4)
    body = b''.join(blobs)
    body += b'\0' * (-len(body) % 4)
    total = 12 + 8 + len(js) + 8 + len(body)
    with open(path, 'wb') as f:
        f.write(struct.pack('<4sII', b'glTF', 2, total))
        f.write(struct.pack('<I4s', len(js), b'JSON') + js)
        f.write(struct.pack('<I4s', len(body), b'BIN\0') + body)


# ---------- the collision ----------

def boxes(t):
    """Per chunk: columns of equal height merged into rectangles, from FLOOR
    to the top, then the trees layer by layer."""
    chunks = {}
    for (x, z), h in t.height.items():
        chunks.setdefault((x // CHUNK, z // CHUNK), {})[x, z] = h
    trees = {}
    for (x, y, z) in t.wood | t.trees:
        trees.setdefault((x // CHUNK, z // CHUNK), set()).add((x, y, z))
    out = {}
    for key in sorted(set(chunks) | set(trees)):
        cells = chunks.get(key, {})
        rects = []
        done = set()
        for x, z in sorted(cells):
            if (x, z) in done:
                continue
            h = cells[x, z]
            w = 1
            while (x + w, z) in cells and (x + w, z) not in done and cells[x + w, z] == h:
                w += 1
            d = 1
            while all((x + i, z + d) in cells and (x + i, z + d) not in done and cells[x + i, z + d] == h for i in range(w)):
                d += 1
            for i in range(w):
                for j in range(d):
                    done.add((x + i, z + j))
            rects.append((x, FLOOR, z, w, h - FLOOR, d))
        blocks = trees.get(key, set())
        layer_done = set()
        for (x, y, z) in sorted(blocks, key=lambda b: (b[1], b[0], b[2])):
            if (x, y, z) in layer_done:
                continue
            w = 1
            while (x + w, y, z) in blocks and (x + w, y, z) not in layer_done:
                w += 1
            d = 1
            while all((x + i, y, z + d) in blocks and (x + i, y, z + d) not in layer_done for i in range(w)):
                d += 1
            for i in range(w):
                for j in range(d):
                    layer_done.add((x + i, y, z + j))
            rects.append((x, y, z, w, 1, d))
        out[key] = rects
    return out


def guid(name):
    return str(uuid.uuid5(NAMESPACE, name)).upper()


def hexid(name):
    return uuid.uuid5(NAMESPACE, name).hex


def num(v):
    return repr(float(v))


def write_prefab(chunks, path):
    parts, roots = [], []
    for (cx, cz), rects in chunks.items():
        name = 'Terrain_%d_%d' % (cx, cz)
        eid = guid(name)
        roots.append(eid)
        ox, oz = cx * CHUNK, cz * CHUNK
        lines = [
            '        -   Entity:',
            '                Id: %s' % eid,
            '                Name: %s' % name,
            '                Components:',
            '                    %s: !TransformComponent' % hexid(name + '/t'),
            '                        Id: %s' % guid(name + '/transform'),
            '                        Position: {X: %s, Y: 0.0, Z: %s}' % (num(ox), num(oz)),
            '                        Rotation: {X: 0.0, Y: 0.0, Z: 0.0, W: 1.0}',
            '                        Scale: {X: 1.0, Y: 1.0, Z: 1.0}',
            '                        Children: {}',
            '                    %s: !Stride.BepuPhysics.StaticComponent,Stride.BepuPhysics' % hexid(name + '/s'),
            '                        Id: %s' % guid(name + '/static'),
            '                        Collider: !Stride.BepuPhysics.Definitions.Colliders.CompoundCollider,Stride.BepuPhysics',
            '                            Colliders:',
        ]
        for i, (x, y, z, w, h, d) in enumerate(rects):
            lines += [
                '                                %s: !Stride.BepuPhysics.Definitions.Colliders.BoxCollider,Stride.BepuPhysics' % hexid('%s/%d' % (name, i)),
                '                                    Mass: 1.0',
                '                                    PositionLocal: {X: %s, Y: %s, Z: %s}' % (num(x - ox + w / 2), num(y + h / 2), num(z - oz + d / 2)),
                '                                    RotationLocal: {X: 0.0, Y: 0.0, Z: 0.0, W: 1.0}',
                '                                    Size: {X: %s, Y: %s, Z: %s}' % (num(w), num(h), num(d)),
            ]
        lines += [
            '                        CollisionGroup: {}',
            '                        ContactEventHandler: null',
        ]
        parts.append('\n'.join(lines) + '\n')
    head = ['!PrefabAsset', 'Id: %s' % PREFAB_ID, 'SerializedVersion: {Stride: 3.1.0.1}', 'Tags: []',
            'Hierarchy:', '    RootParts:'] + ['        - ref!! %s' % r for r in roots] + ['    Parts:']
    with open(path, 'w') as f:
        f.write('\n'.join(head) + '\n' + ''.join(parts))


# ---------- the picture from above ----------

def write_png(t, path):
    size = 2 * HALF
    rows = []
    for z in range(-HALF, HALF):
        row = bytearray([0])
        for x in range(-HALF, HALF):
            h = t.height.get((x, z))
            if h is None:
                c = (0.25, 0.45, 0.75)
            else:
                c = t.material[x, z][0]
                # Light from the north-west, from the height steps.
                west = t.height.get((x - 1, z), h)
                north = t.height.get((x, z - 1), h)
                k = max(0.45, min(1.25, 1.0 + 0.12 * ((h - west) + (h - north))))
                c = tuple(min(1.0, v * k) for v in c)
            if any((x, y, z) in t.trees for y in range(GRASS_Y, GRASS_Y + 9)):
                c = LEAVES
            row += bytes(int(v * 255) for v in c)
        rows.append(bytes(row))
    raw = zlib.compress(b''.join(rows), 9)

    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n')
        f.write(chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 2, 0, 0, 0)))
        f.write(chunk(b'IDAT', raw))
        f.write(chunk(b'IEND', b''))


def main():
    seed = SEED
    if '--seed' in sys.argv:
        seed = int(sys.argv[sys.argv.index('--seed') + 1])
    t = Terrain(seed)

    shares, sectors, quickest = check_climb(t)
    print('Seed %d: %d Säulen, %d Baumblöcke, %d Säulen aus Fallen angehoben' % (seed, len(t.height), len(t.wood | t.trees), t.raised))
    print('Gipfel aus %d / %d Richtungen erreichbar (mit Doppelsprung); vom Berg erreichbar: %.0f %% mit Doppelsprung, '
          '%.0f %% nur mit einfachen Sprüngen (Gipfel %s)' % (sectors, SECTORS, shares[2][1] * 100, shares[1][1] * 100,
                                                            'ja' if shares[1][0] else 'nein'))
    print('Schnellster Aufstieg vom Spawn: %s' % ('%.0f s (ohne Fehler, Weg bekannt)' % quickest if quickest else 'keiner'))
    if sectors < MIN_SECTORS:
        sys.exit('Abbruch: der Gipfel ist aus zu wenigen Richtungen erreichbar')

    mesh = build_mesh(t)
    glb = os.path.join(ROOT, 'KotH.Game/Resources/Models/Mountain.glb')
    write_glb(mesh, glb)
    print('Mountain.glb: %d Dreiecke, %d Ecken, %.1f MB' % (len(mesh.idx) // 3, len(mesh.pos), os.path.getsize(glb) / 1e6))

    chunks = boxes(t)
    prefab = os.path.join(ROOT, 'KotH.Shared/Assets/Maps/Neu/NeuTerrain.sdprefab')
    write_prefab(chunks, prefab)
    counts = [len(r) for r in chunks.values()]
    print('NeuTerrain: %d Chunks, %d Boxen, höchstens %d je Chunk' % (len(chunks), sum(counts), max(counts)))

    os.makedirs(os.path.join(ROOT, '.work/maps'), exist_ok=True)
    write_png(t, os.path.join(ROOT, '.work/maps/mountain.png'))

    print('Gipfelplateau: %d x %d Blöcke, Oberkante y = %d, Mitte (0, 0)' % (2 * PLATEAU + 2, 2 * PLATEAU + 2, SUMMIT_Y))
    print('Wiese im Süden: Höhe y = %d, Bergfuß dort bei z = %.1f, Strand ab z = %.1f, Startreihen bei z = %.1f und %.1f'
          % (GRASS_Y, t.foot(SOUTH), t.coast(SOUTH) - BEACH, t.foot(SOUTH) + SPAWN_GAP, t.foot(SOUTH) + SPAWN_GAP + 2.5))


if __name__ == '__main__':
    main()
