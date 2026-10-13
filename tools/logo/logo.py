"""Draws the Rambutan logo and writes the app icon.

The logo in use is "tile": a red rounded tile holding a white rambutan, a
round body with curled, tapered spikes. At 64 px and up, three red sound
bars sit in the middle; below that they turn to mush, so the small variant
drops them and uses fewer, thicker spikes.

Two other concepts from the same mockups are kept here, undecided rather
than rejected: "silhouette" (a solid red fruit) and "peeled" (white flesh in
a spiky red shell). Every concept has a full and a small variant.

    python tools/logo/logo.py                   # needs Pillow (pip install pillow)
    python tools/logo/logo.py --concept peeled  # switch the app icon

Writes src/Rambutan.App/Assets/rambutan.ico (16-256 px, PNG entries) from the
chosen concept, and tools/logo/<concept>.svg / <concept>-small.svg for all of
them, for use outside the app and for comparing.
"""
import argparse
import io
import math
import struct
from pathlib import Path

from PIL import Image, ImageDraw

RED = '#FF4E45'    # Token.Color.Accent
FLESH = '#FFF4EC'
ROOT = Path(__file__).resolve().parents[2]
SIZES = [16, 24, 32, 48, 64, 128, 256]


def polar(cx, cy, r, a):
    return (cx + r * math.cos(a), cy + r * math.sin(a))


def quad(p0, c, p1, n=12):
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * c[0] + t * t * p1[0],
             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * c[1] + t * t * p1[1])
            for t in (i / n for i in range(n + 1))]


def spikes(r0, r1, n, width, curl, cx=128, cy=128, arc=None):
    """Tapered hairs rooted just inside the body, tips turned clockwise.

    arc=(lo, hi) keeps only the spikes that point between those angles,
    measured clockwise from 12 o'clock at the middle of the curl, so a
    symmetric arc gives a symmetric shape."""
    step = 2 * math.pi / n
    out = []
    for i in range(n):
        if arc and not arc[0] <= i * step + curl / 2 <= arc[1]:
            continue
        a = -math.pi / 2 + i * step
        w = step * width / 2
        left, right = polar(cx, cy, r0 - 6, a - w), polar(cx, cy, r0 - 6, a + w)
        tip = polar(cx, cy, r1, a + curl)
        c1 = polar(cx, cy, r0 + (r1 - r0) * 0.55, a - w * 0.35 + curl * 0.25)
        c2 = polar(cx, cy, r0 + (r1 - r0) * 0.45, a + w * 0.9 + curl * 0.75)
        out.append(quad(left, c1, tip)[:-1] + quad(tip, c2, right))
    return out


def circle(r, cx=128, cy=128, n=96):
    return [polar(cx, cy, r, 2 * math.pi * i / n) for i in range(n)]


def rounded(x, y, w, h, r, n=10):
    pts = []
    for cx, cy, a0 in ((x + w - r, y + r, -math.pi / 2), (x + w - r, y + h - r, 0),
                       (x + r, y + h - r, math.pi / 2), (x + r, y + r, math.pi)):
        pts += [polar(cx, cy, r, a0 + math.pi / 2 * i / n) for i in range(n + 1)]
    return pts


# Layers on a 256-unit grid, back to front: (colour, [polygon, ...]).
# Each concept: (full, small); small is used below 64 px.
CONCEPTS = {
    'tile': (
        [(RED, [rounded(8, 8, 240, 240, 58)]),
         (FLESH, spikes(54, 92, n=20, width=0.85, curl=0.4) + [circle(56)]),
         (RED, [rounded(104, 110, 12, 36, 6), rounded(122, 96, 12, 64, 6), rounded(140, 114, 12, 28, 6)])],
        [(RED, [rounded(8, 8, 240, 240, 58)]),
         (FLESH, spikes(62, 98, n=11, width=0.95, curl=0.3) + [circle(66)])],
    ),
    'silhouette': (
        [(RED, spikes(70, 112, n=24, width=0.85, curl=0.42) + [circle(72)]),
         ('#FF7A72', [circle(16, 104, 104)])],
        [(RED, spikes(72, 112, n=12, width=0.95, curl=0.32) + [circle(76)])],
    ),
    'peeled': (
        [(RED, spikes(70, 114, n=22, width=0.85, curl=0.38, cy=140, arc=(math.pi * 0.4, math.pi * 1.6))
          + [circle(74, cy=140)]),
         (FLESH, [circle(58, cy=120)]),
         ('#FFFFFF', [circle(12, 110, 100)])],
        [(RED, spikes(72, 112, n=12, width=0.95, curl=0.3, cy=140, arc=(math.pi * 0.4, math.pi * 1.6))
          + [circle(76, cy=140)]),
         (FLESH, [circle(62, cy=118)])],
    ),
}


def render(concept, size, supersample=16):
    s = size * supersample
    im = Image.new('RGBA', (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(im)
    f = s / 256
    full, small = CONCEPTS[concept]
    for colour, polys in (full if size >= 64 else small):
        for p in polys:
            draw.polygon([(x * f, y * f) for x, y in p], fill=colour)
    return im.resize((size, size), Image.LANCZOS)


def svg(layers):
    paths = ''.join(
        f'<path fill="{c}" d="'
        + ' '.join('M' + ' L'.join(f'{x:.1f},{y:.1f}' for x, y in p) + 'Z' for p in polys)
        + '"/>'
        for c, polys in layers)
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">{paths}</svg>\n'


def ico(images):
    """An .ico with PNG-compressed entries (Vista+), smallest first."""
    blobs = []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, 'PNG', optimize=True)
        blobs.append(buf.getvalue())
    head = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b''
    for im, blob in zip(images, blobs):
        w = im.width if im.width < 256 else 0
        entries += struct.pack('<BBBBHHII', w, w, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    return head + entries + b''.join(blobs)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Write the app icon from one logo concept.')
    parser.add_argument('--concept', choices=CONCEPTS, default='tile')
    concept = parser.parse_args().concept
    (ROOT / 'src/Rambutan.App/Assets/rambutan.ico').write_bytes(ico([render(concept, s) for s in SIZES]))
    here = Path(__file__).parent
    for name, (full, small) in CONCEPTS.items():
        (here / f'{name}.svg').write_text(svg(full), encoding='utf-8', newline='\n')
        (here / f'{name}-small.svg').write_text(svg(small), encoding='utf-8', newline='\n')
    print(f'wrote rambutan.ico from "{concept}"', SIZES)
