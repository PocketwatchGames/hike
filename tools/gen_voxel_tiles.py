"""Generates placeholder 16x16 voxel tile PNGs and a vertical strip for Texture2DArray import.
Run once; the output PNGs are committed assets.
"""
import os
import random
from PIL import Image

TILE = 16
OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "assets", "textures", "voxels")
os.makedirs(OUT_DIR, exist_ok=True)


def tinted_noise(base, variance=20, seed=0):
    rng = random.Random(seed)
    img = Image.new("RGBA", (TILE, TILE))
    px = img.load()
    for y in range(TILE):
        for x in range(TILE):
            r = max(0, min(255, base[0] + rng.randint(-variance, variance)))
            g = max(0, min(255, base[1] + rng.randint(-variance, variance)))
            b = max(0, min(255, base[2] + rng.randint(-variance, variance)))
            px[x, y] = (r, g, b, 255)
    return img


def grass_top():
    img = tinted_noise((95, 170, 70), variance=25, seed=1)
    px = img.load()
    rng = random.Random(11)
    for _ in range(12):
        x = rng.randint(0, TILE - 1)
        y = rng.randint(0, TILE - 1)
        px[x, y] = (60, 130, 45, 255)
    return img


def grass_side():
    dirt = tinted_noise((140, 90, 45), variance=18, seed=2)
    px = dirt.load()
    rng = random.Random(3)
    for x in range(TILE):
        top = rng.randint(3, 5)
        for y in range(top):
            r = 80 + rng.randint(-15, 15)
            g = 160 + rng.randint(-20, 20)
            b = 55 + rng.randint(-15, 15)
            px[x, y] = (max(0, min(255, r)), max(0, min(255, g)), max(0, min(255, b)), 255)
        px[x, top] = (70, 140, 50, 255)
    return dirt


def wood_end():
    img = tinted_noise((150, 105, 55), variance=15, seed=4)
    px = img.load()
    cx, cy = TILE / 2 - 0.5, TILE / 2 - 0.5
    for y in range(TILE):
        for x in range(TILE):
            d = ((x - cx) ** 2 + (y - cy) ** 2) ** 0.5
            ring = (int(d) % 3 == 0)
            if ring:
                r, g, b, _ = px[x, y]
                px[x, y] = (max(0, r - 30), max(0, g - 25), max(0, b - 15), 255)
    return img


def wood_side():
    img = tinted_noise((155, 110, 60), variance=12, seed=5)
    px = img.load()
    rng = random.Random(7)
    for x in range(TILE):
        if rng.random() < 0.35:
            for y in range(TILE):
                r, g, b, _ = px[x, y]
                px[x, y] = (max(0, r - 25), max(0, g - 22), max(0, b - 12), 255)
    return img


def water():
    img = tinted_noise((40, 125, 235), variance=15, seed=8)
    px = img.load()
    rng = random.Random(9)
    for _ in range(20):
        x = rng.randint(0, TILE - 1)
        y = rng.randint(0, TILE - 1)
        px[x, y] = (180, 220, 255, 255)
    return img


tiles = [
    ("stone", tinted_noise((128, 128, 128), variance=20, seed=10)),
    ("dirt", tinted_noise((140, 90, 40), variance=18, seed=11)),
    ("grass_top", grass_top()),
    ("grass_side", grass_side()),
    ("sand", tinted_noise((215, 200, 140), variance=12, seed=12)),
    ("wood_end", wood_end()),
    ("wood_side", wood_side()),
    ("water", water()),
]

for name, img in tiles:
    img.save(os.path.join(OUT_DIR, f"{name}.png"))

strip = Image.new("RGBA", (TILE, TILE * len(tiles)))
for i, (_, img) in enumerate(tiles):
    strip.paste(img, (0, i * TILE))
strip.save(os.path.join(OUT_DIR, "voxel_tiles.png"))

print(f"Generated {len(tiles)} tiles + strip in {OUT_DIR}")
