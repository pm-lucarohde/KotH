#!/usr/bin/env python3
"""The game's sounds out of Luca's picks in Sounds/ (kept as they are, not in Git).

Writes KotH.Game/Resources/Sounds/NAME.wav, 48 kHz 16 bit: silence cut off both
ends, a short fade at the end, mono for the sounds placed in the world, every
one at the same loudness (SFX_LOUD, the loudest 400 ms; never over PEAK), so
the volumes in SoundEffects alone set the mix. The ambient stays stereo, at
the music's loudness, and its end is faded over its start: it loops seamlessly.

  python3 Tools/audio/sfx.py        needs ffmpeg
"""
import array
import math
import os
import re
import subprocess
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
SOURCE = os.path.join(ROOT, 'Sounds')
TARGET = os.path.join(ROOT, 'KotH.Game/Resources/Sounds')
RATE = 48000
SFX_LOUD = -20.0           # LUFS over the loudest 400 ms
AMBIENT_LOUD = -15.5       # LUFS integrated, as the music
PEAK = -1.0                # dBFS
SILENCE = -60.0            # dBFS: quieter than this at either end is cut
FADE = 0.03                # seconds faded out at the end
LOOP_FADE = 1.0            # seconds of the ambient's end laid over its start
SOUNDS = ['fall', 'hit1', 'hit2', 'hit3', 'jump', 'slide', 'sporejump', 'steps', 'wind']
FILTERS = {'steps': 'lowpass=f=1000'}   # ffmpeg filters on decoding (Luca: steps too harsh)
DRY = {'steps': (0.04, 0.12)}           # the room's reverb faded out between these seconds (Luca: sounded like a hall)


def decode(path, channels, filters=None):
    shape = ['-af', filters] if filters else []
    raw = subprocess.run(['ffmpeg', '-v', 'error', '-i', path, *shape, '-f', 'f32le', '-ac', str(channels), '-ar', str(RATE), '-'],
                         check=True, capture_output=True).stdout
    return array.array('f', raw)


def loudness(samples, channels, integrated):
    """LUFS of the samples, integrated or the loudest momentary (400 ms) value."""
    padded = samples + array.array('f', [0.0] * (RATE * channels // 2))
    out = subprocess.run(['ffmpeg', '-v', 'info', '-nostats', '-f', 'f32le', '-ac', str(channels), '-ar', str(RATE), '-i', '-',
                          '-af', 'ebur128', '-f', 'null', '-'], input=padded.tobytes(), capture_output=True).stderr.decode()
    if integrated:
        return float(re.findall(r'I:\s+(-?[\d.]+) LUFS', out)[-1])
    return max(float(m) for m in re.findall(r'M:\s*(-?[\d.]+)', out))


def trim(samples, channels):
    floor = 10 ** (SILENCE / 20)
    frames = len(samples) // channels
    loud = [i for i in range(frames) if max(abs(samples[i * channels + c]) for c in range(channels)) > floor]
    first, last = loud[0], loud[-1] + 1
    out = samples[first * channels:last * channels]
    fade = min(int(FADE * RATE), last - first)
    n = last - first
    for i in range(fade):
        k = 1.0 - (i + 1) / fade
        for c in range(channels):
            out[(n - fade + i) * channels + c] *= k
    return out


def dry(samples, start, end):
    """Faded out from start to end seconds (a cosine) and cut there: no room tail."""
    a, b = int(start * RATE), int(end * RATE)
    out = samples[:b]
    for i in range(a, len(out)):
        out[i] *= 0.5 + 0.5 * math.cos(math.pi * (i - a) / (b - a))
    return out


def seamless(samples, channels):
    """The last LOOP_FADE seconds faded out over the first ones fading in, then cut off."""
    fade = int(LOOP_FADE * RATE)
    n = len(samples) // channels
    out = samples[:(n - fade) * channels]
    for i in range(fade):
        # Equal power: the noise keeps its loudness through the seam.
        a, b = math.sin(0.5 * math.pi * i / fade), math.cos(0.5 * math.pi * i / fade)
        for c in range(channels):
            out[i * channels + c] = a * samples[i * channels + c] + b * samples[(n - fade + i) * channels + c]
    return out


def write(name, samples, channels, gain_db):
    peak = max(abs(v) for v in samples)
    gain_db = min(gain_db, PEAK - 20 * math.log10(peak))
    k = 10 ** (gain_db / 20)
    pcm = array.array('h', (max(-32767, min(32767, round(v * k * 32767))) for v in samples))
    subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 's16le', '-ac', str(channels), '-ar', str(RATE), '-i', '-',
                    os.path.join(TARGET, name + '.wav')], input=pcm.tobytes(), check=True)
    return gain_db


def main():
    if not os.path.isdir(SOURCE):
        sys.exit('Abbruch: Sounds/ fehlt')
    os.makedirs(TARGET, exist_ok=True)
    for name in SOUNDS:
        samples = trim(decode(os.path.join(SOURCE, name + '.mp3'), 1, FILTERS.get(name)), 1)
        if name in DRY:
            samples = dry(samples, *DRY[name])
        before = loudness(samples, 1, False)
        gain = write(name, samples, 1, SFX_LOUD - before)
        print('%-10s %.2f s, %6.1f LUFS (400 ms) -> %6.1f' % (name, len(samples) / RATE, before, before + gain))
    samples = seamless(decode(os.path.join(SOURCE, 'ambient.mp3'), 2), 2)
    before = loudness(samples, 2, True)
    gain = write('ambient', samples, 2, AMBIENT_LOUD - before)
    print('%-10s %.2f s, %6.1f LUFS -> %6.1f, Schleife' % ('ambient', len(samples) / RATE / 2, before, before + gain))


if __name__ == '__main__':
    main()
