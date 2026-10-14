"""Generates the placeholder blood stain shapes (assets/textures/effects/blood_stain_*.png).

White pixel-art splats on transparency — BloodData.color tints them at spawn, so
one set serves every substance. Seeded, so a rerun reproduces the same files.
Godot does not watch asset_src/; rerun by hand:

    python asset_src/textures/effects/build_blood_stains.py
"""
import math
import random
from pathlib import Path

from PIL import Image

SIZE = 24
VARIANTS = 3
SEED = 7319
OUT = Path(__file__).resolve().parents[3] / "assets" / "textures" / "effects"


def splat(rng: random.Random) -> Image.Image:
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    px = img.load()
    c = (SIZE - 1) / 2.0
    # Lumpy main pool: radius modulated by a few low harmonics around the circle.
    base = rng.uniform(5.0, 6.5)
    harmonics = [(k, rng.uniform(0.3, 1.2), rng.uniform(0, math.tau)) for k in (2, 3, 5)]
    # A few droplets flung outward, some smeared into short streaks.
    drops = []
    for _ in range(rng.randint(4, 7)):
        a = rng.uniform(0, math.tau)
        d = rng.uniform(base + 1.5, c - 1.0)
        drops.append((c + math.cos(a) * d, c + math.sin(a) * d, rng.uniform(0.6, 1.5), a, rng.uniform(1.0, 2.5)))

    for y in range(SIZE):
        for x in range(SIZE):
            dx, dy = x - c, y - c
            r = math.hypot(dx, dy)
            ang = math.atan2(dy, dx)
            edge = base + sum(amp * math.sin(k * ang + ph) for k, amp, ph in harmonics)
            inside = r <= edge
            for (px0, py0, rad, a, stretch) in drops:
                # Ellipse elongated along the throw angle.
                ux, uy = x - px0, y - py0
                along = ux * math.cos(a) + uy * math.sin(a)
                across = -ux * math.sin(a) + uy * math.cos(a)
                if (along / (rad * stretch)) ** 2 + (across / rad) ** 2 <= 1.0:
                    inside = True
            if inside:
                # Slightly darker toward the middle where it pools thickest.
                shade = int(255 - 40 * max(0.0, 1.0 - r / max(edge, 1.0)))
                px[x, y] = (shade, shade, shade, 255)
    return img


def main() -> None:
    rng = random.Random(SEED)
    OUT.mkdir(parents=True, exist_ok=True)
    for i in range(1, VARIANTS + 1):
        path = OUT / f"blood_stain_{i:02d}.png"
        splat(rng).save(path)
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
