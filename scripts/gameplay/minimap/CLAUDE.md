# Minimap

Covers `scripts/gameplay/minimap/` and `shaders/minimap.gdshader`.

Two parallel renderers behind one HUD widget — the **world (heightmap) map** for outdoor view, and the **slice (atlas) map** for indoor / underground view. The HUD shader composites them with a state-A/state-B crossfade so mode toggles (camera cutaway) and slice-level crossings glide smoothly instead of snapping.

**Why two paths**: the outdoor map is a top-down silhouette of terrain — one height per XZ column suffices, so it's a single global texture. The slice map answers "what does this *level* of the world look like in plan view?" — every slice is a separate texture per Y-band, sparsely allocated, because most of the world's volume is empty air or solid rock and only the inhabited bands matter.

## Resolution split

- Outdoor heightmap: **1m/voxel** (`MinimapData.OutdoorMetersPerPixel`) — one pixel per voxel column, so `GenerateSurfaceRow` writes that column's top-face Y directly. It was 2m/voxel (max of each 2×2 block), and the reason it had to change is the **elevation lines**: the map draws a line on a 1 m step, and at 2m/voxel that step did not exist in the data — a one-metre rise was absorbed into its neighbour's max. Cost is **4×** the outdoor surface + both exploration buffers; ~1.2 MB on the shipped 18×16 world, and it grows with world AREA, so a streaming-scale world wants these paged rather than global.
- Indoor slice atlas: **1m/voxel** (`MinimapData.IndoorMetersPerPixel`). One full-extent texture per slice level the player has visited, allocated lazily into `MinimapSliceAtlas._layers`. Each cell encodes the slice center as its synthesized height (so all in-slice content has the same `h`).
- Plateau height = 4m (`MinimapData.PlateauHeight`). Slice levels = `floor(Y / 4)`.

## Surface texture layout (RGBA8, both maps)

- R = height low byte, G = height high byte (combined → world Y of top face, 0 = no content)
- B = **`EMinimapCategory`** (0..4 — indexes the five-entry category palette)
- A = collidable-prop stamp (non-zero = a prop is here; the shader paints the Prop colour over the ground's)

Slice cells that are solid throughout with no air above read as **Stone** (`MinimapData.WallSlotIndex`) — solid rock IS stone, and the map has five colours. Written directly by the slice generator rather than resolved from a block, so every biome's wall pixels agree regardless of what they are made of.

## The map draws five CATEGORIES, not materials

`EMinimapCategory` — **Terrain / Stone / Water / Road / Prop** — authored per
block as `BlockData.minimapCategory`, defaulting to Terrain so a new block is
ordinary ground until someone says otherwise. `Minimap` holds one colour per
category; `MinimapData.ResolveSurfaceTileId` writes the enum into the tile
channel and the shader looks it up. That is the whole mechanism.

**Biome is deliberately not a colour.** A player reading the map wants to know
where the road, the water, the buildings and the things they cannot walk through
are — not which of five soils is underfoot. Grass, forest soil, mud, marsh, sand
and snow are one Terrain colour on purpose; region labels and the world itself
carry biome.

**This is NOT `BlockData.minimapColor`, and both stay.** That field is the
world-map PAINTER's per-material authoring view — paving and water option
swatches, stamp plan colours — where telling marsh from desert is the entire
point. Two consumers, two questions, two fields.

It replaced a per-ATLAS-LAYER table resolved off each block's `minimapColor`,
which failed in three ways that are worth not rebuilding:

- **Blocks sharing a texture fought over one slot.** All six water types wear
  `Water(2)`, so every body of water in the game painted in whichever the
  catalog listed last — a red-brown scum colour that read as terrain.
- **Every shared layer then needed its own authored fallback or it went grey**,
  because `BlockSurfaceData.minimapColor` defaults to a flat 0.3. That trap
  fired three times (water, DesertWall, and mud the moment Road stopped
  agreeing with it).
- **Distinctness was authoring, not structure.** Road, grass, forest soil, mud
  and rock all happened to author the identical `(0.478, 0.475, 0.251)`, so
  roads did not read as roads and nothing said so.

None of that can happen now: a block names a category, a category names a
colour, and there is nothing to share or to leave unauthored.

## Overlays never reach the tile channel

`ResolveSurfaceTileId` returns the BLOCK's own face surface and **never the
voxel's `OverlayId`**. It used to prefer the overlay, and that is most of why
terrain read as several different greens:

- **Moss is stamped on ~16% of surface voxels** (`WorldFinish`: 13605/84724 on
  the shipped world) and painted every one of them a saturated dark green
  `(0.240, 0.320, 0.160)` *instead of* the ground it grows on.
- **The three climb-growth layers were UNAUTHORED**, so any climb-dressed voxel
  that happened to be the topmost of its column painted **magenta**. Latent
  rather than common — climb growth is on cliff faces, which have solid above
  them — but one lip voxel from showing.

It cannot be fixed by authoring the overlays a colour instead: one moss layer
covers grass, forest soil AND cave limestone, so no single colour blends with
what is under it. The overlay has no business in a channel that means "what
material is this". Water films go the same way, which is what makes every body
of water read as one blue.

The cost is worldgen's road tread, which is an overlay rather than a block. The
painter's paving IS a block and is unaffected — see the roads note below.

## Exploration mask

A separate R8 texture per renderer (outdoor mask + per-slice masks). Soft-edged disk reveal writes `max(value, existing)`. Outdoor reveal uses `GameClient.minimapRevealMultiplier × player.visionRange`; slice reveal scales the same value linearly by `WorldState.GetPerceivedLightWorld(playerPos)` (zero light → zero reveal — you can't chart what you can't see).

### Line-of-sight reveal (`MinimapLos`)

Reveal isn't a plain disk — a mountain hides the valley behind it, walls hide the room behind them, and fog hides distant areas. Tuning lives on the `Minimap` node under the "Line of Sight" export group and is bundled into a `MinimapLos` struct (`MinimapData.cs`) once per reveal tick. `losEnabled = false` restores the old plain filled disks.

Three cases, chosen in `Minimap._PhysicsProcess`:

- **Ground (outdoor)** — `MinimapTextures.RevealViewshed`. For every cell in the disk it marches the **heightmap** from the eye toward the cell, tracking the max terrain elevation angle (the running horizon); the cell reveals when its own ground, lifted by `losForgivenessMeters`, reaches that horizon, fading out over the forgiveness band below. **Generosity is the eye height** (`losEyeHeightMeters`, default 5): a high eye looks down over small rises so a 2 m hillock never shadows what's behind it — only terrain taller than ~eye-height above the player occludes. Per-cell marching (not a spoked radial sweep) so there are no gaps; step grows with distance (`LosMaxStepsPerRay`) so a cell's cost is O(1) regardless of radius. Volumetric fog is accumulated along the same march (see below).
- **Bird's-eye (outdoor)** — `MinimapTextures.RevealCircleFogged`. No terrain occlusion (scouting looks straight down over the terrain), just a plain disk attenuated per-cell by the **local** fog density × distance. Cheap, and matches "flying above all terrain."
- **Indoor / underground** — `MinimapSliceAtlas.SliceLayer.RevealCircle` → `ComputeVisibility`. Per-cell 2D raymarch at the player's **real** camera eye height (`GameCamera.EYE_HEIGHT`, *not* the generous outdoor eye — else you'd see over 2-block walls) sampling `WorldState.GetVoxelWorld`; a solid voxel hard-blocks (returns 0), otherwise fog is accumulated along the ray as below. Marches stop one step short of the target so a wall pixel doesn't self-occlude (we still chart the wall face the player sees).

**Fog** (`losFogFullBlockMeters`) reads authored per-voxel `WorldState.GetFogWorld` (0–255 painted fog volumes — swamp pools, etc.), *not* the global diurnal haze. It's the meters of thickest fog along a sightline that fully hides the far end. Both ground and underground reveal **raymarch** it — accumulating optical depth (`fog01 × step / FogFullBlockMeters`) sample-by-sample along the sightline. Only bird's-eye skips the march, using the cheaper local-density × distance form instead.

**Slice-column gate** — `RevealOutdoorSliceColumns` (the cliff-face slice trace) is scaled by `MinimapTextures.ColumnVisibility` (terrain-only, fog excluded) on the ground so a cliff hidden behind a ridge stays dark; ungated in bird's-eye and when LOS is off. Because marker discovery reads the same outdoor mask (`UpdateMarkerDiscovery` → `IsRevealed`), landmarks occluded by terrain are no longer auto-sensed — LOS gates POI discovery for free.

**Slice reveal trace** (`Minimap.RevealOutdoorSliceColumns`) — *do not change to use the heightmap directly*. The aliasing reason is gone (the heightmap is per-column now; it used to take the max of each 2×2 block and misclassify cliff-edge cells into the wrong slice), but the **water** reason stands on its own: the trace treats water as content, matching the heightmap and slice-tile passes, and switching to `IsSolid` would skip water surfaces and never reveal lakes. The trace uses the heightmap as a search-start hint and walks `WorldState.GetVoxelWorld(wx, wy, wz)` downward at 1m granularity to find each column's actual topmost-non-air voxel. Treats water as content (matches the heightmap and slice-tile passes); using `IsSolid` would skip water surfaces and never reveal lakes.

**View radius (adaptive zoom) tracks the reveal distance.** `Minimap.ComputeRevealRadius` = effective vision range × `revealMultiplier`, where the effective vision range folds in the player's vision stats (`EStat.Vision` — base perception, buffs, gear via `ComposeStat`). That radius drives BOTH the charted map reveal (the fog banked to the world map) and the zoom, so anything extending the player's sight widens both together. The view radius (zoom) is `Minimap.ComputeVisibleRevealRadiusMeters() × viewRevealMargin`, computed in `Hud.UpdateMinimapViewRadius` and damp-lerped. `ComputeVisibleRevealRadiusMeters` dims the reveal radius by the **global time-of-day sun brightness** (`DaylightFactor01` — `SkyController.CurrentPrimaryIntensity` normalized by `SimData.dayIntensityBase`, with night-vision relief lifting the floor) and caps it by the **local painted fog** at the player (`losFogFullBlockMeters / fog01`, same fog model as the reveal viewshed), then floors it at `minViewRadiusMeters`. **Brightness must come from the global sky, NOT a locally-sampled `GetPerceivedLightWorld` — the local light sample flickers hard as the player walks under forest canopy and popped the zoom** (an earlier version did this and had to be reworked). Fog stays a local sample by design (it only flickers at a swamp-pool edge, which the Hud damp-lerp eases). So the map zooms *in* at night or in fog (out again with night-vision gear, at dawn, or leaving the fog) and *out* with a vision buff, up to "just smaller than the max reveal distance". `indoorZoom` divides the target in indoor mode so corridors read closer. Bird's-eye keeps its wider radius (multiplier is inside `ComputeRevealRadius`).

## Game-north (−X,−Z)

North is the world diagonal **(−X, −Z)** — the direction the isometric camera faces at its default 45° yaw. A world *axis* would read 45° off the screen; this diagonal lands straight up during normal play. Two consumers keep it:

- **HUD minimap** — still rotates its whole `MinimapTexture` to the live camera yaw (unchanged). The "N" glyph is a child of that texture anchored to the upper-left (local −X = left, −Z = up → their diagonal), so it stays glued over the world-(−X,−Z) terrain and swings with the map — riding at the **top** of the disk at the default camera, sweeping away as the player turns. The glyph carries a local −45° rotation to cancel the texture's default 45° spin, so it reads upright while at the top.
- **World map** (`WorldMapScreen`) — renders north-up via the shader's `map_rotation` uniform (`−π/4`), which spins the world *sampling* (not the Control) so labels/marker text stay upright. The rotated world becomes a diamond inscribed in the square panel, so the view half-extent is widened to the rotated bounding box. `WorldMapScreen.UpdateRegionLabels` and `MapMarkerOverlay` mirror the same rotation (`-map_rotation` on each world offset) so labels and icons track the terrain.

To repoint north, flip these together: the `Label` anchor **and** its local `rotation` in `hud.tscn`, and `WorldMapScreen.NorthMapRotation`.

## World map screen: two zoom levels (`WorldMapScreen`)

The world map is **two render targets, not one shader pass**, cross-faded:

| Level | Render target | Covers |
|---|---|---|
| Overview | `overviewMetersPerPixel` (1.25 m/texel), linear + antialiased | `overviewViewMeters` (512 m) across, capped at the world |
| Detail | `detailPixelsPerMeter` (12 texels/m), viewport sized to the panel | a window around the pan center |

**The overview's extent is ABSOLUTE, in metres** (`overviewViewMeters`), capped
at the world's own extent so it never zooms out into void. It was a fraction of
the world, which could not be authored against — the same setting meant a 288 m
read on the shipped map and a 5000 m one on a large map. Absolute also pins the
render target at `overviewViewMeters / overviewMetersPerPixel`, the same size in
every world, where the fraction form grew with the world until it hit the
`maxViewportPixels` clamp. On a world smaller than the setting the cap bites and
raising it does nothing.

**Zoom, target size and on-screen chunkiness are three quantities and you pick
two.** Doubling the zoom has two routes and they differ only in the third: raise
the per-metre resolution (a voxel becomes more texels, target unchanged) or
shrink the target (same texels per voxel, magnified further, chunkier). Both give
the same outline thickness on screen, because the outline is a fraction of a
CELL. The knobs above take the first route, and the arithmetic makes it free —
the overview target is `2 · (Rworld · fraction) / metersPerPixel`, so halving the
fraction and the metres-per-texel together leaves it exactly the size it was
(77 texels on the shipped world; 1414 on a 5000 × 5000 one — unchanged across two doublings of the zoom).

**Each level clamps the pan by its OWN radius and the displayed center
interpolates between them.** That is what lets the zoomed-out level be either
thing: where `overviewViewMeters` covers the whole world it has no room to pan
and sits on the world center while the detail level keeps the pan; where it
covers less, it pans within its own limits.

The STORED pan is clamped at the DETAIL level's freedom, the loosest of the two.
Clamping it by the current (wider) view radius instead dragged it to zero
whenever you were zoomed out, so zooming back in landed on the middle of the
world rather than where you had been looking — worth not reintroducing.

**Rendering at a FIXED resolution and magnifying with a nearest filter is the whole point of the split** — it is what gives the map an authored pixel size, and what leaves several screen pixels per voxel for per-voxel border lines to be drawn on. Pointing the shader straight at the panel (what the screen used to do) re-renders at whatever size the panel happens to be, so there is no pixel grid to hang a one-pixel line on. Same reasoning as `WorldMapCanvas`, which magnifies its image by an integer rather than fitting it to the control.

**One framing drives both layers.** Zooming animates a single `(center, viewRadius)` and each layer is scaled to the world area *it* covers, so the two stay registered on each other for the whole transition and only their alphas cross-fade. The view radius interpolates **geometrically** (`exp(lerp(log …))`) — a radius is a multiplier, and a linear lerp crawls at the wide end and lurches at the near one. A layer contributing nothing this frame has its `RenderTargetUpdateMode` set to `Disabled`; these are full-screen shader passes.

**Pan is stored in MAP space** (`_panOffsetMap`) — the north-rotated frame the textures are drawn in — because that is the frame pan input arrives in: screen-up is −Y there with no rotation to apply. It is clamped by converting to world space, where each render target is a square turned 45°, so its world-axis bounding half-extent is `radius · √2`. The clamp uses the CURRENT (blended) view radius rather than the detail one, since a wider view has to stop sooner; a world smaller than the window collapses the range to zero and the view stays centered.

**Overlays are panel-scale, never children of the layers.** The layers are scaled to the zoom, so an icon parented under one would magnify with the map. Region labels and both marker overlays are siblings sized to the map view, projected with the blended framing. Zoom also gates *what* they show: region names fade out with the overview, and the overview's marker overlay runs with `ActiveCampfireOnly` so the whole-world read carries the lit campfire alone.

Input while the tab is visible: mouse wheel or right stick (`LookUp` / `LookDown`) toggles the level; WASD or left stick (`Move*`) pans. `LookUp` / `LookDown` have no keyboard binding, so nothing collides.

## Shader sampling rules (`shaders/minimap.gdshader`)

**The two levels sample differently, on purpose.** The detail layer is
point-sampled and pixel-crisp; the overview is antialiased. That split is not
taste — it is which side of 1:1 each one sits on.

| Read | How |
|---|---|
| surface, magnifying fragment | one `texelFetch` — crisp |
| surface, minifying fragment | box average of RESOLVED colours over the covered texels |
| exploration / fog | `texture()`, sampler `filter_nearest` |
| `tile_lut` | `filter_nearest` |
| detail layer on screen | `texture_filter = 1` (nearest), displayed 1:1 |
| overview layer on screen | `texture_filter = 2` (linear) |

**Minification is where shimmer came from.** The overview draws ~1.25 m of world
per texel from a 1 m source, so a single tap keeps one voxel and discards the
rest — and `map_rotation` spins the sample grid 45° against the axis-aligned
voxel grid, which turns that into a moiré that crawls as you pan. It was baked
INTO the render target, so nothing at display time could have removed it.
Magnification never aliases, which is why the detail layer never had the problem
and why the tap count collapses to 1 there for free (`fwidth` of the
source-texel position drives it).

**The average is over RESOLVED COLOURS, never over the texture, and a linear
sampler on the surface would be WRONG rather than merely soft.** That texture is
packed INDEX data — R/G height bytes, B the category enum, A the prop flag — so
averaging category 1 (Stone) with 3 (Road) yields 2, which is Water. Empty
texels are skipped rather than averaged in: their height is the 0 sentinel,
which the elevation shade reads as a cliff and would darken every coastline
with. Coverage becomes the fraction that hit content, so the world edge fades
instead of stepping.

**On screen, the overview is linear because it magnifies ~9× at a non-integer
scale** (77 texels into a ~695 px panel). Nearest there gives blocks of uneven
size that jitter as you pan — irregularity rather than blur, but it moves, so it
reads as shimmer too. `overviewMetersPerPixel` is the knob that trades softness
for chunk: lowering it raises the target resolution, which sharpens the result
AND costs nothing in aliasing, because the box filter scales with it.

The surface samplers were declared `filter_linear` long after every read had
become a `texelFetch`; the hint was dead but misleading, and is now honest.

## Elevation shading is player-relative and curved

The signed delta between a cell's height and the PLAYER's own elevation
(`reference_elevation`, the smoothed eye height from
`ComputeReferenceElevationTarget`), normalised to `elevation_shade_range` and
pushed through `pow(|d|, elevation_shade_gamma)`. Two properties, both
deliberate:

- **Continuous, not banded.** It compared `floor(h / plateau_height)` against
  the player's band, so the shade boundaries sat at absolute multiples of 4 m in
  WORLD space: walk two metres uphill and a whole hillside changed shade at a
  line that had nothing to do with the player. `plateau_height` is gone from the
  shader with it, along with the six above/below brightness-saturation-contrast
  knobs the banded form needed.
- **Gamma BELOW 1 emphasises near deltas**, which are the ones a player can act
  on — the metre step you can walk up versus the cliff you cannot. At 0.5 a 1 m
  step over a 12 m range lands at 29% of full shading where a linear ramp gives
  it 8%. Past the range it clamps, so a distant mountain does not run to white.

**Applied to the composed CATEGORY colour**, so water, road, stone and props
shade with the ground instead of floating flat over it.

The reference was always the player's; only the quantisation was world-space.
That is worth remembering before blaming the reference for a shading complaint.

## Elevation step lines

Replaces the old topographic contour pass (`contour_interval` / `contour_color`
/ `contour_strength`, all removed along with their `.tres` entries). Contours
drew at fixed elevation intervals gated by a neighbour differential; they said
nothing about how big a step was, and going to 1 m/voxel gave them a step every
metre to draw on, which read as noise.

The rule is the world-map painter's: a line on any voxel edge where the surface
steps, drawn **INTO the higher cell** so it reads as the rim of a plateau rather
than a fence between two cells. Bucketed by the size of the step:

| Step | Ink |
|---|---|
| 1 m | black, 25% |
| 2 m | black, 75% |
| ≥3 m | white, 75% |

- **The texel grid IS the voxel grid**, which is the whole reason the source went
  to 1 m/pixel: a fragment's fractional position inside its texel is its position
  inside the metre cell, so a line can be a fraction of a cell wide.
- **Width is in OUTPUT PIXELS** (`step_line_pixels`, 2), converted to cells per
  fragment via `fwidth(world_xz)` and capped at `step_edge_max_fraction` of a
  cell. A cell FRACTION cannot serve both consumers — a voxel is ~3 px on the HUD
  minimap and 12 px on the zoomed-in world map, so one fraction is a hairline on
  one and a slab on the other.
- **Each edge is tested from THIS cell and only when this cell is higher**, so
  the fragment across the boundary draws nothing and the line cannot double up. A
  corner takes the largest step of the edges that apply.
- **A step against no-content is not a cliff.** `step_drop` requires content on
  both sides, so the world edge does not ink.
- **Gated on reveal**, or a contour would chart terrain the party has never seen.
- **Zoom fade is per-fragment, from `fwidth(world_xz)`.** Past ~1 metre per output
  pixel there is no cell left to draw an edge inside, so the whole-world overview
  (5 m/texel) fades to none and the detail view (0.33 m/texel) and HUD minimap
  (~0.3 m/pixel) get them at full strength — decided in the shader, with nothing
  to keep in sync on the C# side.

**Rotation makes these diagonal, and that is inherent.** `map_rotation` spins the
world *sampling*, not the Control, so texels stay axis-aligned and a world-axis
voxel edge lands at 45°. A one-pixel diagonal is a staircase; at exactly 45° it
is a clean 1:1 one, which is what isometric pixel art looks like anyway.
Supersampling (render the target at N× and let it downsample) would soften it,
but needs linear filtering and gives up the crisp pixel grid the nearest
sampling above exists to protect.

## Foliage stamps

`Minimap.StampPropsRecursive` — `MultimeshPropSprite` (trees, tall grass, decor), `MinimapFoliageStamp` (3D-mesh props) and `SpriteBase` (LitSprite/FlatLitSprite — chests, doors, berry trees) all expose `MinimapFoliageId`.

**An authored id is an opt-in, and almost nothing opted in** — exactly ten scenes in the project carried one (eight trees, the signpost, the climbable tree), which is why the map read far sparser than the world. Two fixes, and the second is the one that scales:

- The `SpriteBase` branch was **missing entirely** — documented as stamped, never read — so any chest or door that did author an id was dropped in silence.
- A prop with **static collision** and no authored id now stamps `Minimap.collidablePropFoliageId`. "You can walk into it" is the map-worthy fact, and it needs no per-scene tagging. `StaticBody3D` only: an `Area3D` is a trigger volume and blocks nothing, so a berry bush's pickup radius must not read as an obstacle. Set the id to 0 to restore authored-only behaviour.
- **A prop marks every column its COLLISION covers, not the one its origin lands in** (`PropFootprint`). A column counts when the collision contains the column's *centre* — the point the pixel stands for — so a boulder reads on the map at the width you actually have to walk around. The test is "does the world-vertical line through that centre hit the shape", run in each shape's local space against the real `BoxShape3D` / `SphereShape3D` / `CylinderShape3D` / `CapsuleShape3D` / `ConcavePolygonShape3D` (anything else falls back to its own bounds), so a shape tilted off vertical is still exact and a rock's trimesh silhouette is not squared off to its AABB. A concave shape's `Data` is marshalled once per stamp, never inside the per-column loop.
- **Too thin to cover a centre is not "not there".** A 0.3 m tree trunk usually contains no column centre at all, so a prop with static collision and no covered columns still stamps its origin pixel. Where the subtree authored an id, that id — the highest-priority one, when several are authored — is what the footprint stamps; the generic collidable id is only the fallback.
- **`Mob`s are skipped.** They are in `ActiveEntities` too, and a foliage stamp is burned into the surface texture and never rewritten — stamping one leaves a permanent smear of wherever a wolf stood when its chunk loaded. A stamp writes the A channel of both the outdoor heightmap AND the player's slice atlas layer, and the shader paints the **Prop** colour over whatever category the ground had. Detail scatter (grass tufts) no longer marks the map at all — it is neither collidable nor a category, and it was a third green tone over ordinary ground (slice level computed from `floor((worldY - 1) / PlateauHeight)` so a prop on a plateau's top face lands in the slice that owns the ground).

## Crossfade state ping-pong

`Minimap.UpdateStateTransition` — captures the *previous* render state into A when mode/slice changes, sets B to the new state, and damps `_stateTransition` 0 → 1 over ~0.3s. The shader renders both states and `mix()`es by transition. Reference elevation is also damp-lerped (`_smoothedReferenceY`) so the per-pixel above/below classification glides at slice crossings instead of every pixel reclassifying in one frame.

## Reveal cadence

The disk re-fires every `RevealIntervalSeconds` (0.1s) regardless of player movement. The previous movement gate caused chunks loaded async after spawn to never get revealed until the player moved. The mask uses `max()`-merge so re-running on a stationary player is essentially free — only newly-loaded chunks contribute writes.

## Tuning knobs

Live as `[Export]`s on the `Minimap` node itself, authored in `scenes/subsystems/minimap.tscn` (embedded as a child of `GameClient` in `game.tscn`): `wallSlotColor`, `foliageColors`, `viewRevealMargin`, `minViewRadiusMeters`, `indoorZoom`, `revealMultiplier`, `revealInnerFraction`, and the "Line of Sight" group (`losEnabled`, `losEyeHeightMeters`, `losForgivenessMeters`, `losMarchStepMeters`, `losFogFullBlockMeters`). `World` references this authored node and calls `Initialize`; it doesn't create it. Material shader parameters (above/below brightness/saturation/contrast, contour interval/width/strength, mask radius) live on `resources/materials/minimap.tres`. The runtime-driven uniforms (`view_radius_meters`, `surface_texture_*`, `world_origin_xz_*`, etc.) are pushed each frame from `Hud.UpdateMinimap` — don't author them in the .tres, the runtime overwrites whatever you set.
