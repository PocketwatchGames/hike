# Resizing a document (`WorldMapResize`)

## Resizing a document (`WorldMapResize`)


Two operations, and the difference matters:

- **`worldmap_resize <chunksX> <chunksZ> <res://doc.tres>`** rescales the world.
  The same coastline, bigger. Every layer is resampled.
- **`worldmap_canvas <chunksX> <chunksZ> <res://doc.tres>`** changes the extent
  and nothing else. Every painted metre stays the metre it was and the map gains
  (or loses) ground around it; nothing is resampled, so nothing can be lost
  except what falls outside a shrink. Placements do not even move — they are
  authored in world coordinates and the origin does not shift. This is the one
  to reach for when a world simply needs more room.

Chunks are the unit for both because the per-column images are always
`sizeChunks * 16`, so any chunk count is reachable and the ratio need not be
whole. **Close the painter first** — it holds the old images and its next save
would put them straight back.

**Every layer is CATEGORICAL, so nothing is ever interpolated.** An elevation 6
beside an 8 is two terraces with a wall between them, and a filter that averages
invents a 7 — a terrace nobody painted. The same filter on the ground layer
blends "forest" and "desert" into whichever index lies between. So the one rule
is that an output pixel is a verbatim COPY of some input pixel, enforced by
working on raw bytes a whole pixel at a time. That also keeps a pixel's channels
together, which the spawn layers need: R is a set index and G is that set's
density, and a per-channel filter would pair one set's index with another's
density.

Copying alone gives nearest-neighbour, which staircases every diagonal by the
scale factor — the thing the resize is trying to avoid. The fix is **EPX /
Scale2x, iterated**: double with the corner rule while the target has room, then
land on the exact size. The rule fires only where the two neighbours meeting at a
corner AGREE and the opposite two DISAGREE, which identifies a corner and never a
line, so a one-metre ridge cannot be eroded.

Measured at 4x — worth not re-deriving:

| approach | worst step | flat run | invented values | 1px ridge |
|---|---|---|---|---|
| nearest | 4 px | 4.0 px | 0 | intact |
| majority filter | — | — | 0 | eroded, or no effect at all |
| one corner pass at 4x | 4 px | ~4 px | 0 | intact |
| **Scale2x iterated** | **2 px** | **1.4 px** | **0** | **intact** |

The two rejected ones are instructive. A majority filter sized to see across a
step (radius ~ the scale factor) erodes any feature that small — and after an
upscale a one-metre ridge is exactly that size — while sized smaller it changes
nothing at all (zero pixels at 3x). A single corner pass at the full factor only
nips the corner of an N-pixel step; cutting deeper made it worse, because the
cuts either side of the boundary land a cell apart and zigzag. Iterating is what
works, because the second pass chamfers the first pass's chamfer.

Shrinking takes the **mode** of the region each destination pixel covers, ties to
the centre sample: lossy but never stepped, and picking one sample instead would
drop thin features at random.

**Voxel edits are resampled in XZ like everything else** — they have to be, or a
passage would no longer meet the hillside it was bored into. Only their Y is left
alone, for the same reason heights are. They take *any* edit over the region
rather than a mode, and a carve beats an add, because a passage that silently
seals is worse than one that comes out a metre wide.

Two more things do not follow the images. **Heights are not scaled** — doubling the
map's width must not double how tall its walls are, since wall height is a
gameplay quantity the terrain rules pin independently of extent. **Stamps keep
their size** and are moved to the same relative spot, since a house does not grow
with the map.
