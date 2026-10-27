#!/usr/bin/env python3
"""The cloud map for map "Neu": a sheet of small, soft cloudlets in rows and
waves with blue lanes between them, like Luca's photo (altocumulus, 24.10.2026).
Painted, few details, soft but clear edges.

Writes KotH.Game/Resources/Textures/CloudMap.png, tileable, linear:
  red   the cloudlets: one dome per cloudlet (1 at its top, 0 at its rim);
        the shader takes it as the height of the cloudlet over the layer's base
  green the large streaks and lanes, read again at a larger scale (Atmosphere)
  blue  the coastal fog's wisps over the sea, at their own scale (Atmosphere)
Texture x runs with the wind. Seeded, so every run paints the same.
Needs numpy and Pillow (.work/venv).

  .work/venv/bin/python Tools/maps/clouds.py [--preview]
"""
import os
import sys

import numpy as np
from PIL import Image

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
OUT = os.path.join(ROOT, 'KotH.Game/Resources/Textures/CloudMap.png')
SIZE = 1024
SEED = 24
CELLS = (28, 22)            # cloudlets along and across the wind per repeat
RADIUS = (0.55, 0.95)       # a cloudlet's radius in cells, at random
ACROSS = 1.25               # cloudlets are this much wider across the wind
WAVE = 0.35                 # rows bend along the wind by this many cells
WAVES = 3                   # times per repeat
LANES = (2, 7)              # streaks along and across the wind per repeat
OCTAVES = 3
WISPS = (3, 5)              # coastal fog banks' size: repeats per tile along and across the wind


def cloudlets(rng):
    """Domes round jittered points of a grid, on a torus, the highest wins."""
    gx, gy = CELLS
    cx, cy = SIZE / gx, SIZE / gy
    ys, xs = np.mgrid[0:SIZE, 0:SIZE].astype(np.float64) + 0.5
    jitter = rng.random((gy, gx, 2))
    radius = RADIUS[0] + (RADIUS[1] - RADIUS[0]) * rng.random((gy, gx))
    out = np.zeros((SIZE, SIZE))
    for j in range(gy):
        for i in range(gx):
            py = (j + jitter[j, i, 1]) * cy
            # Rows of cloudlets across the wind bend into waves.
            px = (i + jitter[j, i, 0] + WAVE * np.sin(2 * np.pi * WAVES * py / SIZE)) * cx
            dx = (xs - px + SIZE / 2) % SIZE - SIZE / 2
            dy = ((ys - py + SIZE / 2) % SIZE - SIZE / 2) / ACROSS
            r = radius[j, i] * cx
            d2 = (dx * dx + dy * dy) / (r * r)
            out = np.maximum(out, np.sqrt(np.clip(1.0 - d2, 0.0, 1.0)))
    return out


def lanes(rng, cells=LANES):
    """Tileable streaks along the wind: smooth noise on whole-number lattices."""
    out = np.zeros((SIZE, SIZE))
    total = 0.0
    for o in range(OCTAVES):
        fx, fy = cells[0] << o, cells[1] << o
        grid = rng.random((fy, fx))
        u = np.arange(SIZE) / SIZE * fx
        v = np.arange(SIZE) / SIZE * fy
        i0, j0 = np.floor(u).astype(int), np.floor(v).astype(int)
        su, sv = u - i0, v - j0
        su, sv = su * su * (3 - 2 * su), sv * sv * (3 - 2 * sv)
        i1, j1 = (i0 + 1) % fx, (j0 + 1) % fy
        a = grid[j0][:, i0] * (1 - su) + grid[j0][:, i1] * su
        b = grid[j1][:, i0] * (1 - su) + grid[j1][:, i1] * su
        weight = 0.5 ** o
        out += weight * (a * (1 - sv[:, None]) + b * sv[:, None])
        total += weight
    out /= total
    lo, hi = np.percentile(out, 5), np.percentile(out, 95)
    return np.clip((out - lo) / (hi - lo), 0.0, 1.0)


def wisps(rng):
    """Soft fog banks: white noise filtered in the frequency domain, so it tiles
    and has no lattice; longer along the wind than across."""
    k = np.fft.fftfreq(SIZE) * SIZE
    kx, ky = np.meshgrid(k / WISPS[0], k / WISPS[1])
    amplitude = 1.0 / (1.0 + kx * kx + ky * ky) ** 1.4
    amplitude[0, 0] = 0.0
    out = np.real(np.fft.ifft2(np.fft.fft2(rng.standard_normal((SIZE, SIZE))) * amplitude))
    lo, hi = np.percentile(out, 3), np.percentile(out, 97)
    return np.clip((out - lo) / (hi - lo), 0.0, 1.0)


def main():
    rng = np.random.default_rng(SEED)
    red = cloudlets(rng)
    green = lanes(rng)
    # Their own seed: red and green stay as they were.
    blue = wisps(np.random.default_rng(SEED + 1))
    rgb = np.stack([red, green, blue], axis=-1)
    Image.fromarray(np.round(rgb * 255).astype(np.uint8), 'RGB').save(OUT, optimize=True)
    print('geschrieben:', OUT)
    if '--preview' in sys.argv:
        tile = np.tile(red * np.clip(green * 1.6 - 0.3, 0, 1), (2, 2))
        path = os.path.join(ROOT, '.work/maps/clouds.png')
        Image.fromarray(np.round(tile * 255).astype(np.uint8), 'L').save(path)
        print('Vorschau:', path)


if __name__ == '__main__':
    main()
