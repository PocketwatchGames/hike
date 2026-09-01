# Painted water

## Water is painted, and the map answers back


Every column the brush touches is filled from its ground up to the selected
surface level. A BRUSH, not a fill.

**There is no waterline.** The water layer alone says where water is, and the
world starts PREFILLED with it at `seaLevel` — which costs nothing, because a
blank layer is zeros and 0 already encodes `seaLevel`. Land hides the water it
stands above; carving that land away reveals water that was there the whole
time. So a sea is not a rule about low ground, it is the water nobody erased,
and three things fall out that the old `max(seaLevel, painted)` could not
express:

- **Erasing water is a real edit** (RMB), so a basin dug below sea level can be
  DRY. Under the old rule the sea went wherever the ground was low, whatever the
  author wanted.
- **Water can be painted below the sea** — a signed surface, like elevation.
- **Water survives the land above it.** Paint a lake across a hillside and
  nothing shows; carve the hillside down and the lake is already filled. This is
  the river/lake workflow: shape the water first, then cut the channel into it.

Latent water is deliberately INVISIBLE on the map — drawing it would show a
shoreline the bake does not produce — so the hover readout reports it instead
(`WATER Y=… (depth n)` where it stands, `water Y=… (buried)` where the ground
covers it, `no water` where it has been erased).

Two consequences elsewhere, both from the same removal. `CanSpawnAt` tests DRY
and nothing else, so a drained basin scatters like any other ground; and the
shore band is measured from **the water beside a column** rather than from
`seaLevel`, which is what stops a dry canyon floor coming out as beach sand —
and incidentally gives a mountain lake the shore it never had.

**The flood fill was built twice and removed.** It answered a question the author
had already answered by clicking, and it could not tell a lake from a river
without being told which it was looking at — a seed high in the mountains has no
way to know whether it should pond to its own height or run downhill. Splitting
it into two tools, then unifying them behind a floor bound, both made the model
smaller without making it right. A brush has nothing to decide: water goes where
you put it, at the level you chose, and a lake and a river become the same act
with a different shape of stroke.

What the fill was really providing was FEEDBACK — "here is what your click
implies" — and the map now provides that directly, which is why the simpler model
is not a step backwards:

- **Depth shading.** Water lerps `shallowWaterColor` → `deepWaterColor` across
  `waterDeepAtVoxels` (4), so a shoreline reads pale and a bed dark. Two authored
  stops rather than a long ramp: the shore is the edge an author aims at, and
  more stops make that edge harder to find.
- **Waterfall ink.** Any edge where the water on one side STANDS ABOVE what is
  visible on the other — bare ground below it, or a lower pool — is inked in
  `waterfallInk`,
  a bright teal at full alpha, on the POOL side of the lip (the ink goes into the
  higher cell, and for a spill the higher visible surface is always the water).
  It is checked BEFORE the height buckets, because a one-metre lip is still a
  waterfall and the minor bucket it would fall into is not drawn on the elevation
  map at all. Spill is the one thing about painted water that depth cannot show,
  and the thing most likely to be an accident.

  **The low side is its VISIBLE surface, not its ground**, and that distinction
  is the whole rule. A river dropping into a lower pool is the commonest cascade
  there is; testing "the other side is dry" instead — which this did — excluded
  every one of them, and the lip drew as an ordinary height step. The tell is a
  line DARKER than the water: `waterfallInk` is brighter than any water shade, so
  a dark line at a lip means the edge was never classified as a spill at all.
  (Two columns of one pool share a surface, so the strict comparison already
  does the work a "different bodies" test would.) Measured on the default
  document: 0 cascades under the old rule, 8 under this one.

  It is drawn at its OWN width (`waterfallEdgeWidthFraction`, 0.67 of a metre
  cell, never below 2px where the zoom allows one) rather than at
  `edgeWidthFraction`. A waterfall edge is a warning, not a height cue, and at
  the contour width it comes out as a single pixel — a bright teal that reads as
  a faint fringe on the very shoreline it is flagging.

  It shows on **every map that shows water**, not just the water tool's — a
  spill is a fact about the terrain you need while painting the things beside it,
  the same argument climbing routes are drawn everywhere. The gate is
  `IWorldMapView.DrawsWater` (and **W**), never the active tool, so the only maps
  without it are the ones where water is not on screen to be poured: zone.
  The region map counts as showing water — it does not composite blue
  over the region wash, but it darkens every submerged column, which is why it
  declares `DrawsWater` and takes both the water-surface outlines and the teal.

**The ink is a PREVIEW, not the cascade list.** `WorldMapState.SpillsOver` is a
flat two-layer test — painted water on one side standing above the visible
surface on the other — and it is all the map needs to warn you about a lip while
you paint. What the bake actually files runs over the FINISHED voxels instead
(`WaterfallFinder`, through worldgen's own `WorldFinish.PlaceWaterfalls`), so a fall
off a stamped scene, into a carved tunnel or over a hand-edited voxel is found
here exactly as it is in a generated world, and the surface-Y convention has one
home. The painter used to carry a second site-builder off its painted layers; it
could only ever see the falls the layers themselves described. The drop stays
AIR either way. How SMALL a drop is worth drawing is not decided here —
`SimData.waterfalls` tiers that, and a fall below the first tier (1 m) draws nothing.

The brush is **hard-edged**, ignoring the falloff, for the reason Flatten is: a
water surface is level, and easing it in by weight would tilt every stroke's rim
into a ring of half-steps. It writes every column it covers, including ones whose
ground stands above the surface — that is the latent water above. **alt+click**
samples a height to aim at, the same eyedropper the elevation tool has.

The surface is cached per column beside the weathered heights and refreshed by
the same rect rebuild — no longer global, since a painted surface has no
neighbourhood to spread through. Weathering still measures against it
(`StandHeight`) while it measures against RAW heights, which is what keeps the
two from depending on each other.

**Water TYPE is painted beside the surface, and REPLACE is why it is its own
mode.** The option row is the document's `waterTypes` palette with "Zone's" as
entry 0 — the unpainted state — so there is always a way back to it and it is a
special case nowhere. The ordinary brush writes the surface AND the type
together, which is right when you are putting water somewhere; **X** switches to
REPLACE, which only retypes columns that already hold water and never touches a
surface. Without it, recolouring a lake would mean filling it to whatever level
the HUD happened to hold, and getting that exactly right across a hand-painted
shoreline is not something anyone should have to do to change its colour.

Erasing a column's water erases its type with it: a column with no water has no
type, and one left behind would silently retype whatever gets painted there next.

**A painted type TINTS the water on the map**, on every view that draws water —
resolved inside `WorldMapState.WithWaterOver`, the shared "there is water here"
path, exactly as paving resolves inside `GroundColorAt`. You cannot paint scum
along a shoreline you cannot see, and a type invisible on the map is one you
cannot tell you have already laid down. It TINTS rather than replaces
(`WorldMapInkData.waterTypeTintStrength`, 0.62) so the depth shading underneath
survives: the map still has to say how deep the water is.

What the map draws IS what the bake stamps: nothing derives a water type, so
there is no second source to disagree with. (A per-zone rule briefly existed and
was removed for exactly that reason — it dressed swamp water in scum the painter
never drew.) The colour is the block's own `minimapColor`, resolved into a table
at load: this is reached for every wet texel of every rebuild, so a resource
dereference per texel would land in the map's hottest loop.

**The painted layer is the only source** — it reaches the shared pass as
`WorldFinish.Options.PaintedWaterBlockAt`, the same seam shape `MossCoverageAt`
uses, and worldgen passes null and keeps standard water. Two things the bake will
NOT stamp, both of which
`worldmap_check` reports separately because their absence is otherwise silent:
a type on **latent** water (painted, but buried under ground not yet carved — no
free surface to dress until the land goes) and a type on a **dry** column.
