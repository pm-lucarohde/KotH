#!/usr/bin/env python3
"""The block mountain of map "Neu", after a Minecraft map (Luca, 21.10.2026).

Writes only what belongs to this script, never what Game Studio edits:
  KotH.Game/Resources/Models/Mountain.glb           what is drawn (vertex colours with AO)
  KotH.Game/Resources/Models/Sea.glb                the sea round the island
  KotH.Shared/Resources/Maps/NeuTerrain.bin        the same triangles for the physics, embedded
  .work/maps/mountain.png                           a map from above, for a look

Markers, zone and bot settings stay in NeuCollision (Game Studio); the
script prints where the summit and the meadow are. Blocks are 1 m: one
block up is a jump, two a double jump, three a wall. Pure Python, seeded.

  python3 Tools/maps/mountain.py [--seed N]
  dotnet run --project Tools/maps/MapUV -c Release   then: the lightmap UVs (TEXCOORD_1)
"""
import bisect
import math
import os
import heapq
import random
import struct
import sys
import zlib
from collections import deque

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))

SEED = 7
HALF = 133                 # cells from the centre to the edge of the grid
ISLAND = 119.5             # coast radius, varied by COAST_WAVES
COAST_WAVES = ((16.0, 3), (11.0, 6), (6.0, 13), (2.5, 29), (1.0, 67))   # (metres, cells round): bays to ripples
COAST_MAX = 126.0          # the GI volume ends at 128 m
BEACH = 7.0                # sand this far in from the coast, varied by BEACH_WAVES (Luca: less round;
                           # 1.5 m more after the wider bank)
BEACH_WAVES = ((4.5, 4), (3.0, 9), (1.5, 23))
BEACH_MIN = 3.5
MEADOW_MIN = 12.0          # the meadow reaches at least this far out past the foot (spawns, trees)
SOFT = 3.0                 # metres over which those limits ease in
OLD_ISLAND, OLD_COAST, OLD_BEACH = 117.0, 4.0, 5.0   # the round island the trees were planted on
MOUNTAIN = 95.0            # foot of the mountain, varied by FOOT
FOOT = 4.0
GRASS_Y = 2                # top of the meadow
SAND_Y = 1
SUMMIT_Y = 123             # top of the summit plateau (Luca: 190 m, then less steep and lower,
                           # halfway between 136 m on a 90 m foot and 110 m on 100 m)
PLATEAU = 4                # plateau: 2 * PLATEAU + 2 cells across, centred on the origin
TOP_BLEND = 2.0            # the slope eases into the plateau over this many metres down
TOP_RUN = 4.0              # and this many metres across (Luca: 2 m and 4 m)
TOP_FADE = 12.0            # ridges and columns grow in over this many metres below the plateau
PROFILE = 1.1              # height = (1 - r / foot) ^ PROFILE: about 1.35 blocks per metre,
                           # a little steeper on top
RIDGES = 9.0               # depth of the ridges running down the slope
RIDGE_SPACING = 3.0        # metres between ridges around the mountain
RIDGE_LENGTH = 13.0        # metres a ridge runs down before it changes
COLUMN = 2                 # the slope's columns are this many blocks wide round the mountain
COLUMN_DOWN = 1            # and this many deep down it (Luca: tops to stand on; 2 x 2 is too steep
                           # to climb unless the mountain is as flat as at 110 m)
COLUMNS = 2.0              # fine columns between the ridges, as in the video
COLUMN_SPACING = 1.3
COLUMN_LENGTH = 5.0
BUMPS = 4.0                # larger lumps
JITTER = 0.15              # share of columns a block higher or lower at random (more: spikier)
SNOWLINE = 0.55            # share of the summit height, varied by SNOW_NOISE
SNOW_NOISE = 9.0
CAPS = 8.0                 # snow caps on tops this far below the snow line
SOUTH = math.pi / 2        # +Z, where the meadow with the spawns is
SECTORS = 12               # directions round the foot the climb is checked from
MIN_SECTORS = 9            # the summit must be reached from this many of them
TREES = 36
SPAWN_GAP = 7.0            # first spawn row this far out from the foot in the south
TREE_GAP = 7.0
CHUNK = 4                  # collision chunks, metres across
UNDER = 0                  # the coast is drawn down to here, just under the sea
SIMPLIFY = 0.02            # the simplified mesh stays this close (metres) to every original face
SIMPLIFY_COLOUR = 0.03     # and merges only vertices this close in paint weights and shade
SIMPLIFY_NORMAL = 0.95     # and with vertex normals at least this parallel
WATER_Y = 0.4
SEA_UNDER = 1.0            # the sea reaches at least this far in under the coast line
EDGE_IN = 0.35             # the beach's edges follow their lines, not the blocks (Luca: too blocky):
EDGE_OUT = 0.3             # an edge's top this far inside the line, its foot this far outside
BANK_IN = 0.7              # the same for the bank from the meadow down to the sand, wider
BANK_OUT = 0.7             # (Luca: the drop was too steep)
EDGE_ROUND = 0.85          # share of the way from the blocks to the line (Luca: 1 a touch too round,
                           # 0.5 still too angular seen from above)
SEA_REACH = 30000.0        # the sea's half width, beyond the camera's far plane

# Colours in sRGB; the glb carries them linear.
STONE = (0.55, 0.51, 0.45)
SNOW = (0.93, 0.95, 0.97)
GRASS = (0.37, 0.55, 0.23)
DIRT = (0.47, 0.35, 0.23)
SAND = (0.86, 0.81, 0.60)
WOOD = (0.42, 0.31, 0.18)
LEAVES = (0.24, 0.42, 0.16)
AO_STEP = 0.16             # darker per solid block over half of a corner's eight (Minecraft's steps)
SMOOTH = 0.7               # Surface Nets strength: 0 cubes, 1 as round as NoCubes (more eats the tops)
SLOPE_LIMIT = 50.0         # the player motor's, to report how much ground one stands on
TRUNK_RADIUS = 0.5         # trunks are round (Luca), as thick as the block was
TRUNK_FOOT = 0.55          # and this thick at the ground, widening over TRUNK_FLARE like roots
TRUNK_FLARE = 0.5
TRUNK_SIDES = 16
CROWN_STEP = 0.35          # crowns are soft lobes run together (Luca: rounder, more natural), meshed this fine
CROWN_MELT = 0.6           # metres over which the lobes run together
CROWN_NOISE = 0.12         # lumps on the lobes, metres
CROWN_WIDTH = 1.1          # crowns this much wider than the lobes say (Luca: 10 % more)
CROWN_BLOCKY = 0.5         # share of the old leaf blocks in the crown's shape (Luca: between round and blocky)
CARDS = 200                # twigs standing out of every crown, evenly all round (Luca, after "bushy leaves" packs)
CARD_LENGTH = (0.72, 1.035)  # metres, a third of it inside the crown
# The leaves are cut out of their texture (Luca); copies of the crown this far
# in, this much darker, fill it behind the gaps. Only drawn, not in the collision.
LEAF_SHELLS = ((0.3, 0.8), (0.6, 0.62))
SHELL_SEARCH = 12.0       # metres a ray from a leaf corner inward looks for the crown's far side



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


def soft_max(a, b):
    """max(a, b), eased over SOFT metres: a limit leaves no straight stretch."""
    h = max(SOFT - abs(a - b), 0.0) / SOFT
    return max(a, b) + h * h * SOFT / 4


class Terrain:
    def __init__(self, seed):
        self.seed = seed
        self.rng = random.Random(seed)
        self.height = {}      # (x, z) -> top y of the column
        self.material = {}    # (x, z) -> (top colour, side colour)
        self.tint = {}        # (x, z) -> brightness of that column
        self.trees = set()    # (x, y, z) of wood and leaf blocks
        self.crowns = []      # (x, z, top of the trunk, blocks, tree number) per tree; drawn round (round_crown)
        self.wood = set()
        self.dropped = 0      # trees the new coast left too close to the sand
        self.build()

    # Cell (x, z) spans x..x+1 and z..z+1; its centre is +0.5.
    def polar(self, x, z):
        cx, cz = x + 0.5, z + 0.5
        return math.hypot(cx, cz), math.atan2(cz, cx) % (2 * math.pi)

    def foot(self, angle):
        return MOUNTAIN + FOOT * ring(self.seed + 1, angle, 7)

    def coast(self, angle):
        r = ISLAND + sum(m * ring(self.seed + 20 + k, angle, cells) for k, (m, cells) in enumerate(COAST_WAVES))
        return -soft_max(-soft_max(r, self.foot(angle) + MEADOW_MIN + self.beach(angle)), -COAST_MAX)

    def beach(self, angle):
        w = BEACH + sum(m * ring(self.seed + 30 + k, angle, cells) for k, (m, cells) in enumerate(BEACH_WAVES))
        return soft_max(w, BEACH_MIN)

    def old_meadow_edge(self, angle):
        return OLD_ISLAND + OLD_COAST * ring(self.seed + 2, angle, 9) - OLD_BEACH

    def rounded(self, corner, p):
        """A ground corner on the beach's inner edge (grass to sand) or on the
        coast (sand to sea) moved from the centre EDGE_ROUND of the way onto that
        edge's line, top a little inside, foot a little outside: the edge runs
        smoother than the block steps. Everything else stays."""
        X, Y, Z = corner
        if Y > GRASS_Y:
            return p
        columns = [self.height.get((X - 1 + i, Z - 1 + k)) for i in (0, 1) for k in (0, 1)]
        if SAND_Y not in columns:
            return p
        if None in columns:
            top = Y >= SAND_Y
        elif GRASS_Y in columns and max(columns) == GRASS_Y:
            top = Y >= GRASS_Y
        else:
            return p
        r = math.hypot(p[0], p[2])
        a = math.atan2(p[2], p[0]) % (2 * math.pi)
        bank = None not in columns
        line = self.coast(a) - (self.beach(a) if bank else 0.0)
        target = line - (BANK_IN if bank else EDGE_IN) if top else line + (BANK_OUT if bank else EDGE_OUT)
        k = (r + EDGE_ROUND * (target - r)) / r
        return (p[0] * k, p[1], p[2] * k)

    def build(self):
        s = self.seed
        for x in range(-HALF, HALF):
            for z in range(-HALF, HALF):
                r, a = self.polar(x, z)
                coast = self.coast(a)
                if r > coast:
                    continue
                foot = self.foot(a)
                if r > coast - self.beach(a):
                    self.height[x, z] = SAND_Y
                elif r >= foot:
                    self.height[x, z] = GRASS_Y
                elif max(abs(x + 0.5), abs(z + 0.5)) <= PLATEAU + 0.5:
                    self.height[x, z] = SUMMIT_Y
                else:
                    # The slope is shaped per group of cells, COLUMN wide round the mountain
                    # and COLUMN_DOWN deep down it: wide tops to stand on, short steps up.
                    if abs(z + 0.5) >= abs(x + 0.5):
                        w, d = COLUMN, COLUMN_DOWN
                    else:
                        w, d = COLUMN_DOWN, COLUMN
                    gx, gz = x // w * w, z // d * d
                    r, a = self.polar(gx + (w - 1) / 2, gz + (d - 1) / 2)
                    # Measured from the square plateau's edge, so the slope meets it all round.
                    edge = PLATEAU + 1
                    cx, cz = gx + w / 2, gz + d / 2
                    d = math.hypot(max(0.0, abs(cx) - edge), max(0.0, abs(cz) - edge))
                    # The first TOP_RUN metres drop TOP_BLEND metres on a cubic that starts
                    # flat and meets the main profile with its slope; ridges fade in below.
                    rest, run = SUMMIT_Y - GRASS_Y - TOP_BLEND, foot - edge - TOP_RUN
                    if d < TOP_RUN:
                        t, u = 0.0, d / TOP_RUN
                        b = rest * PROFILE * TOP_RUN / run - 2 * TOP_BLEND
                        below = max(0.0, (TOP_BLEND - b) * u * u + b * u * u * u)
                    else:
                        t = min(1.0, (d - TOP_RUN) / run)
                        below = TOP_BLEND + rest * (1 - (1 - t) ** PROFILE)
                    fade = min(1.0, below / TOP_FADE)
                    fade = fade * fade * (3 - 2 * fade)
                    h = SUMMIT_Y - below
                    # Ridges: around the mountain short, down the slope long.
                    around = a * foot / RIDGE_SPACING
                    cells = max(1, round(2 * math.pi * foot / RIDGE_SPACING))
                    rough = 0.6 + 0.4 * (1 - t)
                    lumps = RIDGES * rough * noise(s + 3, around, r / RIDGE_LENGTH, wrap=cells)
                    fine = max(1, round(2 * math.pi * foot / COLUMN_SPACING))
                    lumps += COLUMNS * noise(s + 10, a * foot / COLUMN_SPACING, r / COLUMN_LENGTH, wrap=fine)
                    lumps += BUMPS * noise(s + 4, gx / 11.0, gz / 11.0)
                    jitter = lattice(s + 11, gx, gz)
                    lumps += -1 if jitter < JITTER / 2 else 1 if jitter > 1 - JITTER / 2 else 0
                    # Bumps never climb back up past half the depth: no towers by the plateau.
                    self.height[x, z] = max(GRASS_Y, min(SUMMIT_Y, round(h + min(below / 2, fade * lumps))))
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
        # Chosen from the old round island's meadow, so the trees stand where they did.
        meadow = sorted(c for c in self.height if self.foot(self.polar(*c)[1]) <= self.polar(*c)[0] <= self.old_meadow_edge(self.polar(*c)[1]))
        tries = 0
        spawn = self.foot(SOUTH) + SPAWN_GAP
        while len(spots) < TREES and tries < 20000:
            tries += 1
            x, z = rng.choice(meadow)
            r, a = self.polar(x, z)
            if r < self.foot(a) + 4 or r > self.old_meadow_edge(a) - 3:
                continue
            if -10 <= x <= 10 and spawn - 8 <= z <= spawn + 14:     # around the spawns
                continue
            if any(math.hypot(x - sx, z - sz) < TREE_GAP for sx, sz in spots):
                continue
            spots.append((x, z))
        for n, (x, z) in enumerate(spots):
            trunk = rng.randint(4, 6)
            base = self.height[x, z]
            top = base + trunk
            wood = {(x, y, z) for y in range(base, top)}
            blocks = []
            for y, reach in ((top - 2, 2), (top - 1, 2), (top, 1), (top + 1, 1)):
                for dx in range(-reach, reach + 1):
                    for dz in range(-reach, reach + 1):
                        corner = abs(dx) == reach and abs(dz) == reach
                        if corner and (y >= top or rng.random() < 0.5):
                            continue
                        if y == top + 1 and abs(dx) + abs(dz) > 1:
                            continue
                        if (x + dx, y, z + dz) not in wood:
                            blocks.append((x + dx, y, z + dz))
            # Built in full either way, so the trees after it stay the same.
            r, a = self.polar(x, z)
            if r > self.coast(a) - self.beach(a) - 3:
                self.dropped += 1
                continue
            self.crowns.append((x, z, top, blocks, n))
            self.wood |= wood
            self.trees.update(blocks)


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

# What a block is; None is air. Trunks are drawn round (round_trunk); the
# ground and leaves close over their blocks only to the eye, so the
# collision has no floor inside a trunk for the bots' grid.
GROUND, FOLIAGE, TRUNK, CARD = 0, 1, 2, 3

# The dual cell round a block corner: its 8 blocks (i, j, k in 0, 1 from the
# corner's lower side) and the 12 edges between blocks next to each other.
CELL = [(i, j, k) for i in (0, 1) for j in (0, 1) for k in (0, 1)]
EDGES = [(a, b) for a in CELL for b in CELL if a < b and sum(abs(a[n] - b[n]) for n in range(3)) == 1]

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


# The ground's surfaces in the order of TerrainTriplanar's weights; rock is the rest.
PAINTS = [(GRASS, DIRT), (SAND, SAND), (SNOW, SNOW), (SNOW, STONE), (STONE, STONE)]


def block(t, x, y, z):
    h = t.height.get((x, z))
    if h is not None and y < h:
        return GROUND
    if (x, y, z) in t.wood:
        return TRUNK
    if (x, y, z) in t.trees:
        return FOLIAGE
    return None


class Mesh:
    """Surface Nets as in NoCubes: every block face between solid and air
    is a quad, but its corners move off the block corners towards the mean
    of the surface crossings round them, by SMOOTH (0: cubes, 1: NoCubes).
    Corners are shared per kind of block, so ground and leaves never blend."""

    def __init__(self, t):
        self.t = t
        self.pos, self.nrm, self.col, self.uv, self.idx = [], [], [], [], []
        self.keys = []         # per vertex (corner, kind)
        self.index = {}
        self.offsets = {}
        self.tris = []         # vertex triples wound into the solid, for the collision
        self.unsolid = set()   # triangles (by number) only to be seen, not in the collision

    def kinds(self, X, Y, Z):
        return {c: block(self.t, X - 1 + c[0], Y - 1 + c[1], Z - 1 + c[2]) for c in CELL}

    def offset(self, X, Y, Z):
        """The mean of the crossings on the dual cell's edges (their middles,
        the density is solid or not) from corner (X, Y, Z); none by a trunk."""
        key = (X, Y, Z)
        if key not in self.offsets:
            kinds = self.kinds(X, Y, Z)
            sx = sy = sz = n = 0
            if TRUNK not in kinds.values():
                for a, b in EDGES:
                    if (kinds[a] is None) != (kinds[b] is None):
                        sx += (a[0] + b[0]) / 2 - 0.5
                        sy += (a[1] + b[1]) / 2 - 0.5
                        sz += (a[2] + b[2]) / 2 - 0.5
                        n += 1
            self.offsets[key] = (sx / n, sy / n, sz / n) if n else (0.0, 0.0, 0.0)
        return self.offsets[key]

    def vertex(self, corner, kind):
        key = (corner, kind)
        if key not in self.index:
            o = self.offset(*corner)
            self.index[key] = len(self.pos)
            p = tuple(corner[n] + SMOOTH * o[n] for n in range(3))
            self.pos.append(self.t.rounded(corner, p) if kind == GROUND else p)
            self.nrm.append([0.0, 0.0, 0.0])
            self.keys.append(key)
        return self.index[key]

    def face(self, x, y, z, n, kind, solid=True):
        """The face of block (x, y, z) towards n, split along the shorter diagonal."""
        u, v = FACES[n]
        centre = (x + 0.5 + 0.5 * n[0], y + 0.5 + 0.5 * n[1], z + 0.5 + 0.5 * n[2])
        q = []
        for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            corner = tuple(round(centre[i] + 0.5 * su * u[i] + 0.5 * sv * v[i]) for i in range(3))
            q.append(self.vertex(corner, kind))
        p = [self.pos[i] for i in q]
        if dist(p[0], p[2]) <= dist(p[1], p[3]):
            tris = ((q[0], q[1], q[2]), (q[0], q[2], q[3]))
        else:
            tris = ((q[0], q[1], q[3]), (q[1], q[2], q[3]))
        for a, b, c in tris:
            # Area-weighted normals, shared by every face round a vertex.
            nx, ny, nz = cross(sub(self.pos[b], self.pos[a]), sub(self.pos[c], self.pos[a]))
            for vi in (a, b, c):
                self.nrm[vi][0] += nx
                self.nrm[vi][1] += ny
                self.nrm[vi][2] += nz
            if not solid:
                self.unsolid.add(len(self.idx) // 3)
            self.idx += [a, b, c]

    def finish(self):
        """Normals to unit length, then what the shader paints at every vertex."""
        for i, (corner, kind) in enumerate(self.keys):
            n = self.nrm[i]
            length = math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2) or 1.0
            self.nrm[i] = (n[0] / length, n[1] / length, n[2] / length)
            weights, shade = self.paint(corner, kind)
            self.col.append(weights)
            self.uv.append((shade, 0.0))

    def paint(self, corner, kind):
        """The ground's weights for TerrainTriplanar (COLOR_0: grass, sand,
        snow, snow cap; the rest is rock) and the shade (TEXCOORD_0.x)."""
        t = self.t
        X, Y, Z = corner
        tint = 1.0
        weights = (0.0, 0.0, 0.0, 0.0)
        if kind == GROUND:
            # The column whose top is at this corner, else the highest wall.
            columns = [(t.height.get((X - 1 + i, Z - 1 + k), -99), (X - 1 + i, Z - 1 + k)) for i in (0, 1) for k in (0, 1)]
            tops = [c for h, c in columns if h == Y]
            column = tops[0] if tops else max(columns)[1]
            surface = PAINTS.index(t.material[column])
            weights = tuple(1.0 if n == surface else 0.0 for n in range(4))
            if t.material[column][0] in (STONE, SNOW):
                tint = 0.95 + 0.05 * lattice(t.seed + 12, X, Y, Z) + 0.03 * noise(t.seed + 8, X / 5.0, Z / 5.0)
        # Soft AO as with smooth lighting: darker the more of the cell is solid.
        solid = sum(1 for k in self.kinds(X, Y, Z).values() if k is not None)
        return weights, tint * (1.0 - AO_STEP * max(0, solid - 4))


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def dist(a, b):
    return math.sqrt(sum((a[i] - b[i]) ** 2 for i in range(3)))


def build_mesh(t):
    mesh = Mesh(t)
    # Ground: the top of every column and its sides down to a lower
    # neighbour, or to one block under the sea at the coast.
    for (x, z) in sorted(t.height):
        h = t.height[x, z]
        if block(t, x, h, z) in (None, TRUNK):
            mesh.face(x, h - 1, z, (0, 1, 0), GROUND, block(t, x, h, z) is None)
        for n in ((1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1)):
            nb = t.height.get((x + n[0], z + n[2]), UNDER)
            for y in range(max(nb, UNDER), h):
                if block(t, x + n[0], y, z + n[2]) is None:
                    mesh.face(x, y, z, n, GROUND)
    mesh.finish()
    for x, z, top, blocks, n in t.crowns:
        base = t.height[x, z]
        # The trunk reaches into the crown's middle.
        round_trunk(mesh, x, z, base, top - 1.3)
        round_crown(mesh, random.Random(t.seed * 1000 + n), x, z, top, blocks)
    simplify(mesh)
    return mesh


def round_trunk(mesh, x, z, base, top):
    """A round trunk of TRUNK_SIDES from under the ground at base into the
    leaves at top, widened at the foot like roots. Shaded as the blocks'
    corners were: darker where ground or leaves meet it."""
    cx, cz = x + 0.5, z + 0.5
    dark = 1.0 - AO_STEP
    heights = [(base - 0.3, TRUNK_FOOT, dark), (base, TRUNK_FOOT, dark)]
    for s in (0.25, 0.55, 1.0):
        # Spreads fastest at the ground and runs smoothly into the round.
        w = (1.0 - s) ** 2
        heights.append((base + TRUNK_FLARE * s, w * TRUNK_FOOT + (1 - w) * TRUNK_RADIUS, 1.0 - AO_STEP * w))
    heights += [(top - 0.5, TRUNK_RADIUS, 1.0), (top, TRUNK_RADIUS, dark), (top + 0.3, TRUNK_RADIUS, dark)]
    first = len(mesh.pos)
    for y, r, shade in heights:
        for k in range(TRUNK_SIDES):
            a = 2 * math.pi * k / TRUNK_SIDES
            mesh.pos.append((cx + r * math.cos(a), y, cz + r * math.sin(a)))
            mesh.nrm.append([0.0, 0.0, 0.0])
            mesh.col.append((0.0, 0.0, 0.0, 0.0))
            mesh.uv.append((shade, 0.0))
            mesh.keys.append((None, TRUNK))
    for ring in range(len(heights) - 1):
        for k in range(TRUNK_SIDES):
            a = first + ring * TRUNK_SIDES + k
            b = first + ring * TRUNK_SIDES + (k + 1) % TRUNK_SIDES
            # Wound as the blocks' faces: counter-clockwise from outside.
            for tri in ((a, b + TRUNK_SIDES, b), (a, a + TRUNK_SIDES, b + TRUNK_SIDES)):
                p = [mesh.pos[i] for i in tri]
                nx, ny, nz = cross(sub(p[1], p[0]), sub(p[2], p[0]))
                for i in tri:
                    mesh.nrm[i][0] += nx
                    mesh.nrm[i][1] += ny
                    mesh.nrm[i][2] += nz
                mesh.idx += tri
    for i in range(first, len(mesh.pos)):
        n = mesh.nrm[i]
        length = math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2) or 1.0
        mesh.nrm[i] = (n[0] / length, n[1] / length, n[2] / length)


def crown_lobes(rng, x, z, top):
    """A body round the trunk's top and 6 to 8 smaller lobes round it, one on
    top, each as (centre, radii) of an ellipsoid as it shows on its own."""
    cx, cz = x + 0.5 + rng.uniform(-0.25, 0.25), z + 0.5 + rng.uniform(-0.25, 0.25)
    cy = top - 1.0
    lobes = [((cx, cy, cz), (2.1, 1.5, 2.1))]
    count = rng.randint(6, 8)
    turn = rng.uniform(0.0, 2 * math.pi)
    for i in range(count):
        # Some hang lower at the rim, some sit up on the shoulders.
        a = turn + 2 * math.pi * i / count + rng.uniform(-0.35, 0.35)
        up = rng.uniform(-0.35, 0.8)
        d = rng.uniform(1.5, 2.0)
        r = rng.uniform(0.9, 1.3)
        centre = (cx + d * math.cos(up) * math.cos(a), cy + 0.8 * d * math.sin(up), cz + d * math.cos(up) * math.sin(a))
        lobes.append((centre, (r, 0.85 * r, r)))
    lobes.append(((cx + rng.uniform(-0.5, 0.5), cy + 1.3, cz + rng.uniform(-0.5, 0.5)), (1.2, 0.95, 1.2)))
    w = CROWN_WIDTH
    return [((cx + (c[0] - cx) * w, c[1], cz + (c[2] - cz) * w), (r[0] * w, r[1], r[2] * w)) for c, r in lobes]


def round_crown(mesh, rng, x, z, top, blocks):
    """A crown as one smooth surface (Surface Nets, CROWN_STEP), CROWN_BLOCKY
    of the way from the lobes (run together over CROWN_MELT, a little noise
    for lumps) to the old leaf blocks. Both as distances in metres, so the
    blend stays clean far from either. Normals from the field, lighter on
    top; solid like the leaf blocks were."""
    lobes = crown_lobes(rng, x, z, top)
    seed = rng.randrange(1 << 30)

    def field(p):
        # Into the lobes: an ellipsoid's distance roughly, smooth minimum.
        near = None
        for c, r in lobes:
            q = math.sqrt(sum(((p[k] - c[k]) / r[k]) ** 2 for k in range(3)))
            d = (q - 1.0) * min(r)
            if near is None:
                near = d
            else:
                h = max(CROWN_MELT - abs(near - d), 0.0) / CROWN_MELT
                near = min(near, d) - h * h * CROWN_MELT / 4
        rounded = -near + CROWN_NOISE * noise3(seed, p[0] / 0.9, p[1] / 0.9, p[2] / 0.9)
        # Into the blocks: the nearest block's box distance.
        box = None
        for b in blocks:
            o = [abs(p[k] - b[k] - 0.5) - 0.5 for k in range(3)]
            d = math.sqrt(sum(max(v, 0.0) ** 2 for v in o)) + min(max(o), 0.0)
            box = d if box is None else min(box, d)
        return (1 - CROWN_BLOCKY) * rounded - CROWN_BLOCKY * box

    lo = [min(min(c[k] - r[k] for c, r in lobes), min(b[k] for b in blocks)) - CROWN_STEP for k in range(3)]
    hi = [max(max(c[k] + r[k] for c, r in lobes), max(b[k] + 1 for b in blocks)) + CROWN_STEP for k in range(3)]
    size = [int(math.ceil((hi[k] - lo[k]) / CROWN_STEP)) + 1 for k in range(3)]

    def point(i, j, k):
        return (lo[0] + i * CROWN_STEP, lo[1] + j * CROWN_STEP, lo[2] + k * CROWN_STEP)
    values = {(i, j, k): field(point(i, j, k)) for i in range(size[0]) for j in range(size[1]) for k in range(size[2])}
    first = len(mesh.pos)
    corners = [(a, b, c) for a in (0, 1) for b in (0, 1) for c in (0, 1)]
    edges = [(u, v) for u in corners for v in corners if u < v and sum(abs(u[n] - v[n]) for n in range(3)) == 1]
    cell = {}
    for i in range(size[0] - 1):
        for j in range(size[1] - 1):
            for k in range(size[2] - 1):
                # The mean of the crossings on the cell's edges.
                sx = sy = sz = n = 0
                for u, v in edges:
                    fu = values[i + u[0], j + u[1], k + u[2]]
                    fv = values[i + v[0], j + v[1], k + v[2]]
                    if (fu > 0) != (fv > 0):
                        s = fu / (fu - fv)
                        pu, pv = point(i + u[0], j + u[1], k + u[2]), point(i + v[0], j + v[1], k + v[2])
                        sx += pu[0] + s * (pv[0] - pu[0])
                        sy += pu[1] + s * (pv[1] - pu[1])
                        sz += pu[2] + s * (pv[2] - pu[2])
                        n += 1
                if not n:
                    continue
                p = (sx / n, sy / n, sz / n)
                h = 0.05
                g = [field(tuple(p[m] + (h if m == a else 0.0) for m in range(3)))
                     - field(tuple(p[m] - (h if m == a else 0.0) for m in range(3))) for a in range(3)]
                length = math.sqrt(sum(v * v for v in g)) or 1.0
                normal = tuple(-v / length for v in g)
                cell[i, j, k] = len(mesh.pos)
                mesh.pos.append(p)
                mesh.nrm.append(normal)
                mesh.col.append((0.0, 0.0, 0.0, 0.0))
                mesh.uv.append((0.72 + 0.28 * (0.5 + 0.5 * normal[1]), 0.0))
                mesh.keys.append((None, FOLIAGE))
    # A quad round every grid edge the surface crosses, of the four cells sharing it.
    faces = len(mesh.idx)
    for (i, j, k), f in values.items():
        for axis in range(3):
            o = [i, j, k]
            o[axis] += 1
            if tuple(o) not in values or (f > 0) == (values[tuple(o)] > 0):
                continue
            b, c = [m for m in range(3) if m != axis]
            ring = []
            for db, dc in ((0, 0), (1, 0), (1, 1), (0, 1)):
                q = [i, j, k]
                q[b] -= db
                q[c] -= dc
                ring.append(cell.get(tuple(q)))
            if None in ring:
                continue
            out = [0.0, 0.0, 0.0]
            out[axis] = 1.0 if f > 0 else -1.0
            for tri in ((ring[0], ring[1], ring[2]), (ring[0], ring[2], ring[3])):
                p = [mesh.pos[v] for v in tri]
                if sum(cross(sub(p[1], p[0]), sub(p[2], p[0]))[m] * out[m] for m in range(3)) < 0:
                    tri = (tri[0], tri[2], tri[1])
                mesh.idx += tri
    leaf_cards(mesh, rng, faces)


def leaf_cards(mesh, rng, faces):
    """CARDS twigs on the crown whose triangles start at index faces, as many
    per square metre on top, at the sides and below: each a card leaning out
    of the surface, a third inside, lit with the crown's normal there; only to
    be seen. u runs from a whole number k (LeafCard's wind pace) to k + 1,
    mirrored at random; v from the tip (0) to the base (1)."""
    tris = [mesh.idx[i:i + 3] for i in range(faces, len(mesh.idx), 3)]
    total, upto = 0.0, []
    for a, b, c in tris:
        total += 0.5 * math.sqrt(sum(k * k for k in cross(sub(mesh.pos[b], mesh.pos[a]), sub(mesh.pos[c], mesh.pos[a]))))
        upto.append(total)

    def unit(v):
        length = math.sqrt(sum(k * k for k in v))
        return tuple(k / length for k in v) if length > 1e-6 else None
    placed = 0
    while placed < CARDS:
        # A point on the surface by area, its normal blended from the corners.
        tri = tris[min(bisect.bisect(upto, rng.random() * total), len(tris) - 1)]
        r1, r2 = math.sqrt(rng.random()), rng.random()
        wts = (1 - r1, r1 * (1 - r2), r1 * r2)
        p = tuple(sum(wts[m] * mesh.pos[tri[m]][k] for m in range(3)) for k in range(3))
        n = unit(tuple(sum(wts[m] * mesh.nrm[tri[m]][k] for m in range(3)) for k in range(3)))
        if n is None:
            continue
        lean = unit((rng.gauss(0, 1), rng.gauss(0, 1), rng.gauss(0, 1)))
        side = unit((rng.gauss(0, 1), rng.gauss(0, 1), rng.gauss(0, 1)))
        if lean is None or side is None:
            continue
        d = unit(tuple(n[k] + 0.9 * lean[k] for k in range(3)))
        w = d and unit(cross(d, side))
        if w is None:
            continue
        length = rng.uniform(*CARD_LENGTH)
        width = 0.8 * length
        base = tuple(p[k] - d[k] * length / 3 for k in range(3))
        quad = [tuple(base[k] + s * w[k] * width / 2 + t * d[k] * length for k in range(3))
                for s, t in ((-1, 0), (1, 0), (1, 1), (-1, 1))]
        card = rng.randrange(8)
        u0, u1 = (card + 1, card) if rng.random() < 0.5 else (card, card + 1)
        at = len(mesh.pos)
        for q, uv in zip(quad, ((u0, 1.0), (u1, 1.0), (u1, 0.0), (u0, 0.0))):
            mesh.pos.append(q)
            mesh.nrm.append(n)
            mesh.col.append((0.0, 0.0, 0.0, 0.0))
            mesh.uv.append(uv)
            mesh.keys.append((None, CARD))
        # Both sides wound as fronts (LeafCards culls backs): Stride turns a
        # back face's normal round, and a twig seen from behind went dark.
        for tri in ((at, at + 1, at + 2), (at, at + 2, at + 3), (at, at + 2, at + 1), (at, at + 3, at + 2)):
            mesh.unsolid.add(len(mesh.idx) // 3)
            mesh.idx += tri
        placed += 1


def noise3(seed, u, v, w):
    """Smooth value noise in [-1, 1] in three dimensions."""
    iu, iv, iw = math.floor(u), math.floor(v), math.floor(w)
    f = [u - iu, v - iv, w - iw]
    s = [t * t * (3 - 2 * t) for t in f]
    total = 0.0
    for a in (0, 1):
        for b in (0, 1):
            for c in (0, 1):
                weight = (s[0] if a else 1 - s[0]) * (s[1] if b else 1 - s[1]) * (s[2] if c else 1 - s[2])
                total += weight * (lattice(seed, iu + a, iv + b, iw + c) * 2 - 1)
    return total


def quadric(a, b, c):
    """The plane through a, b, c as the ten terms of its error quadric."""
    n = cross(sub(b, a), sub(c, a))
    length = math.sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2])
    if length == 0:
        return [0.0] * 10
    x, y, z = n[0] / length, n[1] / length, n[2] / length
    d = -(x * a[0] + y * a[1] + z * a[2])
    return [x * x, x * y, x * z, x * d, y * y, y * z, y * d, z * z, z * d, d * d]


def error(q, p):
    x, y, z = p
    return (q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x + q[4] * y * y
            + 2 * q[5] * y * z + 2 * q[6] * y + q[7] * z * z + 2 * q[8] * z + q[9])


def simplify(mesh):
    """Fewer triangles where they change nothing: half-edge collapses (a
    vertex moves onto a neighbour, so every kept vertex keeps its place,
    colour and normal) by the planes they bend, while every original face
    stays within SIMPLIFY. Borders stay; the collision gets the same faces."""
    pos, col, nrm, uv = mesh.pos, mesh.col, mesh.nrm, mesh.uv
    faces = [mesh.idx[i:i + 3] for i in range(0, len(mesh.idx), 3)]
    around = [set() for _ in pos]
    quad = [[0.0] * 10 for _ in pos]
    edges = {}
    for f, (a, b, c) in enumerate(faces):
        q = quadric(pos[a], pos[b], pos[c])
        for v in (a, b, c):
            around[v].add(f)
            quad[v] = [quad[v][k] + q[k] for k in range(10)]
        for e in ((a, b), (b, c), (c, a)):
            key = (min(e), max(e))
            edges[key] = edges.get(key, 0) + 1
    border = set()
    for (a, b), n in edges.items():
        if n != 2:
            border.update((a, b))
    # Faces only to be seen keep their corners: no collision face shrinks under them.
    for f in mesh.unsolid:
        border.update(faces[f])
    alive = [True] * len(faces)
    gone = [False] * len(pos)
    version = [0] * len(pos)
    limit = SIMPLIFY * SIMPLIFY
    heap = []

    def neighbours(v):
        return {w for f in around[v] for w in faces[f]} - {v}

    def similar(u, v):
        return (max(abs(col[u][k] - col[v][k]) for k in range(4)) <= SIMPLIFY_COLOUR
                and abs(uv[u][0] - uv[v][0]) <= SIMPLIFY_COLOUR
                and sum(nrm[u][k] * nrm[v][k] for k in range(3)) >= SIMPLIFY_NORMAL)

    def offer(u, v):
        # u goes only if it matches every neighbour: then no stretched face
        # carries a far colour or normal over where it was.
        if u in border or not all(similar(u, w) for w in neighbours(u)):
            return
        cost = error([quad[u][k] + quad[v][k] for k in range(10)], pos[v])
        if cost <= limit:
            heapq.heappush(heap, (cost, u, v, version[u], version[v]))

    for a, b in sorted(edges):
        offer(a, b)
        offer(b, a)
    while heap:
        cost, u, v, vu, vv = heapq.heappop(heap)
        if gone[u] or gone[v] or version[u] != vu or version[v] != vv:
            continue
        shared = [f for f in around[u] if v in faces[f]]
        if len(shared) != 2 or len(neighbours(u) & neighbours(v)) != 2:
            continue
        # No face round u may fold over or shrink to nothing when u moves to v.
        ok = True
        for f in around[u]:
            if v in faces[f]:
                continue
            old = [pos[w] for w in faces[f]]
            new = [pos[v] if w == u else pos[w] for w in faces[f]]
            n0 = cross(sub(old[1], old[0]), sub(old[2], old[0]))
            n1 = cross(sub(new[1], new[0]), sub(new[2], new[0]))
            l0 = math.sqrt(sum(k * k for k in n0))
            l1 = math.sqrt(sum(k * k for k in n1))
            if l1 < 1e-6 or sum(n0[k] * n1[k] for k in range(3)) < 0.5 * l0 * l1:
                ok = False
                break
        if not ok:
            continue
        for f in shared:
            alive[f] = False
            for w in faces[f]:
                around[w].discard(f)
        for f in around[u]:
            faces[f] = [v if w == u else w for w in faces[f]]
            around[v].add(f)
        around[u] = set()
        gone[u] = True
        quad[v] = [quad[u][k] + quad[v][k] for k in range(10)]
        version[v] += 1
        for w in sorted(neighbours(v)):
            offer(v, w)
            offer(w, v)
    # Only the vertices still in use, in their old order.
    keep = sorted({w for f, face in enumerate(faces) if alive[f] for w in face})
    new = {old: i for i, old in enumerate(keep)}
    mesh.pos = [pos[i] for i in keep]
    mesh.col = [col[i] for i in keep]
    mesh.uv = [uv[i] for i in keep]
    mesh.nrm = [nrm[i] for i in keep]
    mesh.keys = [mesh.keys[i] for i in keep]
    mesh.idx = [new[w] for f, face in enumerate(faces) if alive[f] for w in face]
    mesh.tris = [(new[a], new[c], new[b]) for f, (a, b, c) in enumerate(faces) if alive[f] and f not in mesh.unsolid]


def slopes(mesh):
    """Share of the ground's area (seen from above) flatter than the motor's
    slope limit, where one stands without sliding."""
    flat = total = 0.0
    for a, c, b in mesh.tris:
        nx, ny, nz = cross(sub(mesh.pos[b], mesh.pos[a]), sub(mesh.pos[c], mesh.pos[a]))
        length = math.sqrt(nx * nx + ny * ny + nz * nz)
        if length == 0 or ny <= 0:
            continue
        total += ny / 2
        if ny / length > math.cos(math.radians(SLOPE_LIMIT)):
            flat += ny / 2
    return flat / total


def write_glb(path, parts):
    """A glTF binary: one mesh with a primitive per part (material name, positions,
    normals, indices, COLOR_0 or None, TEXCOORD_0 or None); the model asset maps
    the materials by name."""
    def pack(fmt, rows):
        return b''.join(struct.pack(fmt, *r) for r in rows)
    blobs, views, accessors, primitives, materials = [], [], [], [], []
    offset = 0

    def add(blob, target, accessor):
        nonlocal offset
        views.append({'buffer': 0, 'byteOffset': offset, 'byteLength': len(blob), 'target': target})
        blobs.append(blob)
        offset += len(blob)
        accessor['bufferView'] = len(views) - 1
        accessors.append(accessor)
        return len(accessors) - 1
    for name, pos, nrm, idx, col, uv in parts:
        n = len(pos)
        attributes = {
            'POSITION': add(pack('<3f', pos), 34962, {'componentType': 5126, 'count': n, 'type': 'VEC3',
                            'min': [min(p[i] for p in pos) for i in range(3)], 'max': [max(p[i] for p in pos) for i in range(3)]}),
            'NORMAL': add(pack('<3f', nrm), 34962, {'componentType': 5126, 'count': n, 'type': 'VEC3'}),
        }
        if col:
            attributes['COLOR_0'] = add(pack('<4f', col), 34962, {'componentType': 5126, 'count': n, 'type': 'VEC4'})
        if uv:
            attributes['TEXCOORD_0'] = add(pack('<2f', uv), 34962, {'componentType': 5126, 'count': n, 'type': 'VEC2'})
        indices = add(struct.pack('<%dI' % len(idx), *idx), 34963, {'componentType': 5125, 'count': len(idx), 'type': 'SCALAR'})
        primitives.append({'attributes': attributes, 'indices': indices, 'material': len(materials)})
        # Stride's import merges materials that differ only by name: tell them apart.
        materials.append({'name': name, 'pbrMetallicRoughness': {'metallicFactor': 0.0, 'roughnessFactor': 0.9 - 0.01 * len(materials)}})
    name = os.path.splitext(os.path.basename(path))[0]
    gltf = {
        'asset': {'version': '2.0', 'generator': 'KotH Tools/maps/mountain.py'},
        'scene': 0, 'scenes': [{'nodes': [0]}],
        'nodes': [{'name': name, 'mesh': 0}],
        'meshes': [{'name': name, 'primitives': primitives}],
        'materials': materials,
        'buffers': [{'byteLength': offset}],
        'bufferViews': views,
        'accessors': accessors,
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


def parts(mesh):
    """The mesh split by kind of block, each with only its own vertices; the
    leaves with their inner shells (LEAF_SHELLS)."""
    out = []
    for kind, name in ((GROUND, 'Terrain'), (FOLIAGE, 'Leaves'), (TRUNK, 'Bark'), (CARD, 'LeafCards')):
        faces = [mesh.idx[i:i + 3] for i in range(0, len(mesh.idx), 3) if mesh.keys[mesh.idx[i]][1] == kind]
        used = sorted({v for f in faces for v in f})
        new = {v: i for i, v in enumerate(used)}
        pos, nrm = [mesh.pos[v] for v in used], [mesh.nrm[v] for v in used]
        idx, col, uv = [new[v] for f in faces for v in f], [mesh.col[v] for v in used], [mesh.uv[v] for v in used]
        if kind == FOLIAGE:
            # TEXCOORD_0.y = 1 marks the leaves (LeafSplat needs it no more).
            uv = [(u[0], 1.0) for u in uv]
            n = len(used)
            room = shell_room(pos, nrm, idx)
            print(f'Laub: {sum(r < 1.0 for r in room)} von {n} Ecken zu dünn für die inneren Schalen, '
                  f'{sum(r == 0.0 for r in room)} ohne')
            # A shell face on a corner without room would stand on the crown itself.
            held = [i for f in range(0, len(faces) * 3, 3) if all(room[v] > 0 for v in idx[f:f + 3]) for i in idx[f:f + 3]]
            for depth, dark in LEAF_SHELLS:
                base = len(pos)
                pos += [tuple(pos[i][k] - depth * room[i] * nrm[i][k] for k in range(3)) for i in range(n)]
                nrm += nrm[:n]
                col += col[:n]
                uv += [(uv[i][0] * dark, uv[i][1]) for i in range(n)]
                idx += [base + i for i in held]
        out.append((name, pos, nrm, idx, col, uv))
    return out


def shell_room(pos, nrm, idx):
    """Per leaf vertex the share of LEAF_SHELLS' depths that fits under it:
    where a ray inward along the normal leaves the crown again within twice
    the deepest shell (a lump, a thin top), the shells keep to its middle,
    else they came out on the far side as dark patches (Luca). None where the
    ray meets no wall from inside within SHELL_SEARCH (a loose sliver, a
    normal turned out): there the shell stood above the crown."""
    deepest = max(depth for depth, _ in LEAF_SHELLS)
    reach = 2 * deepest
    grid = {}
    for f in range(0, len(idx), 3):
        tri = [pos[v] for v in idx[f:f + 3]]
        lo = [math.floor(min(p[k] for p in tri) / reach) for k in range(3)]
        hi = [math.floor(max(p[k] for p in tri) / reach) for k in range(3)]
        for cx in range(lo[0], hi[0] + 1):
            for cy in range(lo[1], hi[1] + 1):
                for cz in range(lo[2], hi[2] + 1):
                    grid.setdefault((cx, cy, cz), []).append(f)
    room = []
    for v, (p, n) in enumerate(zip(pos, nrm)):
        d = (-n[0], -n[1], -n[2])
        near, inside, seen, along = SHELL_SEARCH, False, set(), 0.0
        while along < near:
            home = [math.floor((p[k] + d[k] * along) / reach) for k in range(3)]
            for cx in (home[0] - 1, home[0], home[0] + 1):
                for cy in (home[1] - 1, home[1], home[1] + 1):
                    for cz in (home[2] - 1, home[2], home[2] + 1):
                        for f in grid.get((cx, cy, cz), ()):
                            if f in seen or v in idx[f:f + 3]:
                                continue
                            seen.add(f)
                            corners = idx[f:f + 3]
                            t = ray_triangle(p, d, *(pos[w] for w in corners))
                            if t is not None and 0.01 < t < near:
                                near = t
                                inside = sum(d[k] * nrm[w][k] for w in corners for k in range(3)) > 0
            along += reach
        room.append(min(1.0, near / reach) if inside else 0.0)
    return room


def ray_triangle(o, d, a, b, c):
    """Distance along d from o to triangle abc (Möller-Trumbore), or None."""
    e1, e2 = sub(b, a), sub(c, a)
    h = cross(d, e2)
    det = sum(e1[k] * h[k] for k in range(3))
    if abs(det) < 1e-12:
        return None
    s = sub(o, a)
    u = sum(s[k] * h[k] for k in range(3)) / det
    if u < 0 or u > 1:
        return None
    q = cross(s, e1)
    w = sum(d[k] * q[k] for k in range(3)) / det
    if w < 0 or u + w > 1:
        return None
    return sum(e2[k] * q[k] for k in range(3)) / det


def sea(t):
    """The sea round the island and out to SEA_REACH, with a hole where the
    island stands: overlapping it, the far depth buffer lost the meadow under
    the water. Flat at the entity's height (NeuVisual puts it at WATER_Y)."""
    pos, idx = [], []

    def rect(x0, z0, x1, z1):
        b = len(pos)
        pos.extend([(x0, 0.0, z0), (x0, 0.0, z1), (x1, 0.0, z1), (x1, 0.0, z0)])
        idx.extend([b, b + 1, b + 2, b, b + 2, b + 3])
    # The hole keeps SEA_UNDER inside the coast line, so the sea reaches in
    # under the rounded coast wall, else one saw past it into the void.
    def hole(x, z):
        r, a = t.polar(x, z)
        return r + 0.71 <= t.coast(a) - SEA_UNDER
    for z in range(-HALF, HALF):
        x = -HALF
        while x < HALF:
            if hole(x, z):
                x += 1
                continue
            end = x
            while end < HALF and not hole(end, z):
                end += 1
            rect(x, z, end, z + 1)
            x = end
    far = SEA_REACH
    rect(-far, -far, far, -HALF)
    rect(-far, HALF, far, far)
    rect(-far, -HALF, -HALF, HALF)
    rect(HALF, -HALF, far, HALF)
    return 'Water', pos, [(0.0, 1.0, 0.0)] * len(pos), idx, None, None


# ---------- the collision ----------

def write_collision(mesh, path):
    """The drawn triangles for the physics, compressed: the vertices, then
    per CHUNK x CHUNK chunk (by centroid, in order) its triangles as vertex
    triples wound into the solid, as Bepu's triangles take them. The game
    puts each chunk into one static compound (WorldBuilder)."""
    chunks = {}
    for tri in mesh.tris:
        cx = sum(mesh.pos[i][0] for i in tri) / 3
        cz = sum(mesh.pos[i][2] for i in tri) / 3
        chunks.setdefault((math.floor(cx / CHUNK), math.floor(cz / CHUNK)), []).append(tri)
    out = [struct.pack('<4sII', b'KTRN', 1, len(mesh.pos))]
    out += [struct.pack('<3f', *p) for p in mesh.pos]
    out.append(struct.pack('<I', len(chunks)))
    for key in sorted(chunks):
        tris = chunks[key]
        out.append(struct.pack('<iiI', key[0], key[1], len(tris)))
        out += [struct.pack('<3I', *tri) for tri in tris]
    with open(path, 'wb') as f:
        f.write(zlib.compress(b''.join(out), 9))
    return chunks


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
    print('Seed %d: %d Säulen, %d Baumblöcke, %d Säulen aus Fallen angehoben, %d Bäume am Strand weggelassen' % (seed, len(t.height), len(t.wood | t.trees), t.raised, t.dropped))
    print('Gipfel aus %d / %d Richtungen erreichbar (mit Doppelsprung); vom Berg erreichbar: %.0f %% mit Doppelsprung, '
          '%.0f %% nur mit einfachen Sprüngen (Gipfel %s)' % (sectors, SECTORS, shares[2][1] * 100, shares[1][1] * 100,
                                                            'ja' if shares[1][0] else 'nein'))
    print('Schnellster Aufstieg vom Spawn: %s' % ('%.0f s (ohne Fehler, Weg bekannt)' % quickest if quickest else 'keiner'))
    if sectors < MIN_SECTORS:
        sys.exit('Abbruch: der Gipfel ist aus zu wenigen Richtungen erreichbar')

    mesh = build_mesh(t)
    glb = os.path.join(ROOT, 'KotH.Game/Resources/Models/Mountain.glb')
    write_glb(glb, parts(mesh))
    write_glb(os.path.join(ROOT, 'KotH.Game/Resources/Models/Sea.glb'), [sea(t)])
    print('Mountain.glb: %d Dreiecke, %d Ecken, %.1f MB' % (len(mesh.idx) // 3, len(mesh.pos), os.path.getsize(glb) / 1e6))

    print('Begehbar (flacher als %.0f Grad): %.0f %% der Bodenfläche' % (SLOPE_LIMIT, slopes(mesh) * 100))

    data = os.path.join(ROOT, 'KotH.Shared/Resources/Maps/NeuTerrain.bin')
    os.makedirs(os.path.dirname(data), exist_ok=True)
    chunks = write_collision(mesh, data)
    counts = [len(c) for c in chunks.values()]
    print('NeuTerrain.bin: %d Chunks, %d Dreiecke, höchstens %d je Chunk, %.1f MB'
          % (len(chunks), sum(counts), max(counts), os.path.getsize(data) / 1e6))

    os.makedirs(os.path.join(ROOT, '.work/maps'), exist_ok=True)
    write_png(t, os.path.join(ROOT, '.work/maps/mountain.png'))

    print('Gipfelplateau: %d x %d Blöcke, Oberkante y = %d, Mitte (0, 0)' % (2 * PLATEAU + 2, 2 * PLATEAU + 2, SUMMIT_Y))
    print('Wiese im Süden: Höhe y = %d, Bergfuß dort bei z = %.1f, Strand ab z = %.1f, Startreihen bei z = %.1f und %.1f'
          % (GRASS_Y, t.foot(SOUTH), t.coast(SOUTH) - t.beach(SOUTH), t.foot(SOUTH) + SPAWN_GAP, t.foot(SOUTH) + SPAWN_GAP + 2.5))


if __name__ == '__main__':
    main()
