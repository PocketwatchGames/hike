# Minimap

Covers `scripts/gameplay/minimap/` and `shaders/minimap.gdshader`.

Two parallel renderers behind one HUD widget — the **world (heightmap) map** for outdoor view, and the **slice (atlas) map** for indoor / underground view. The HUD shader composites them with a state-A/state-B crossfade so mode toggles (camera cutaway) and slice-level crossings glide smoothly instead of snapping.

**Why two paths**: the outdoor map is a top-down silhouette of terrain — one height per XZ column suffices, so it's a single global texture. The slice map answers "what does this *level* of the world look like in plan view?" — every slice is a separate texture per Y-band, sparsely allocated, because most of the world's volume is empty air or solid rock and only the inhabited bands matter.

## Resolution split

- Outdoor heightmap: **2m/voxel** (`MinimapData.OutdoorMetersPerPixel`). Each pixel covers a 2×2 block; `GenerateSurfaceRow` writes the **max** top-face Y of the block. The max preserves cliff silhouettes (a single-voxel pillar still contributes its height) but **aliases at cliff edges** — see "Slice reveal trace" below.
- Indoor slice atlas: **1m/voxel** (`MinimapData.IndoorMetersPerPixel`). One full-extent texture per slice level the player has visited, allocated lazily into `MinimapSliceAtlas._layers`. Each cell encodes the slice center as its synthesized height (so all in-slice content has the same `h`).
- Plateau height = 4m (`MinimapData.PlateauHeight`). Slice levels = `floor(Y / 4)`.

## Surface texture layout (RGBA8, both maps)

- R = height low byte, G = height high byte (combined → world Y of top face, 0 = no content)
- B = resolved tile id (0..63, indexes the `MinimapTileColors` LUT)
- A = foliage id (0..255, indexes the `MinimapFoliageColors` LUT — multiplicative darken on terrain color)

The **wall slot** (`MinimapTileColors.WALL_SLOT = 32`) is reserved for slice cells that are solid throughout the slice with no air above. It paints kit-agnostic dark grey so underground rock reads consistently, regardless of biome.

## Exploration mask

A separate R8 texture per renderer (outdoor mask + per-slice masks). Soft-edged disk reveal writes `max(value, existing)`. Outdoor reveal uses `GameClient.minimapRevealMultiplier × player.visionRange`; slice reveal scales the same value linearly by `WorldState.GetPerceivedLightWorld(playerPos)` (zero light → zero reveal — you can't chart what you can't see).

### Line-of-sight reveal (`MinimapLos`)

Reveal isn't a plain disk — a mountain hides the valley behind it, walls hide the room behind them, and fog hides distant areas. Tuning lives on the `Minimap` node under the "Line of Sight" export group and is bundled into a `MinimapLos` struct (`MinimapData.cs`) once per reveal tick. `losEnabled = false` restores the old plain filled disks.

Three cases, chosen in `Minimap._PhysicsProcess`:

- **Ground (outdoor)** — `MinimapTextures.RevealViewshed`. For every cell in the disk it marches the **2 m heightmap** from the eye toward the cell, tracking the max terrain elevation angle (the running horizon); the cell reveals when its own ground, lifted by `losForgivenessMeters`, reaches that horizon, fading out over the forgiveness band below. **Generosity is the eye height** (`losEyeHeightMeters`, default 5): a high eye looks down over small rises so a 2 m hillock never shadows what's behind it — only terrain taller than ~eye-height above the player occludes. Per-cell marching (not a spoked radial sweep) so there are no gaps; step grows with distance (`LosMaxStepsPerRay`) so a cell's cost is O(1) regardless of radius. Volumetric fog is accumulated along the same march (see below).
- **Bird's-eye (outdoor)** — `MinimapTextures.RevealCircleFogged`. No terrain occlusion (scouting looks straight down over the terrain), just a plain disk attenuated per-cell by the **local** fog density × distance. Cheap, and matches "flying above all terrain."
- **Indoor / underground** — `MinimapSliceAtlas.SliceLayer.RevealCircle` → `ComputeVisibility`. Per-cell 2D raymarch at the player's **real** camera eye height (`GameCamera.EYE_HEIGHT`, *not* the generous outdoor eye — else you'd see over 2-block walls) sampling `WorldState.GetVoxelWorld`; a solid voxel hard-blocks (returns 0), otherwise fog is accumulated along the ray as below. Marches stop one step short of the target so a wall pixel doesn't self-occlude (we still chart the wall face the player sees).

**Fog** (`losFogFullBlockMeters`) reads authored per-voxel `WorldState.GetFogWorld` (0–255 painted fog volumes — swamp pools, etc.), *not* the global diurnal haze. It's the meters of thickest fog along a sightline that fully hides the far end. Both ground and underground reveal **raymarch** it — accumulating optical depth (`fog01 × step / FogFullBlockMeters`) sample-by-sample along the sightline. Only bird's-eye skips the march, using the cheaper local-density × distance form instead.

**Slice-column gate** — `RevealOutdoorSliceColumns` (the cliff-face slice trace) is scaled by `MinimapTextures.ColumnVisibility` (terrain-only, fog excluded) on the ground so a cliff hidden behind a ridge stays dark; ungated in bird's-eye and when LOS is off. Because marker discovery reads the same outdoor mask (`UpdateMarkerDiscovery` → `IsRevealed`), landmarks occluded by terrain are no longer auto-sensed — LOS gates POI discovery for free.

**Slice reveal trace** (`Minimap.RevealOutdoorSliceColumns`) — *do not change to use the heightmap directly*. The 2m heightmap aliases mixed-elevation 2×2 blocks (cliff edge cells) to the column max, which would misclassify the lower-elevation voxels in those blocks into the wrong slice and leave the lower region unrevealed at the cliff base. The trace uses the heightmap as a search-start hint and walks `WorldState.GetVoxelWorld(wx, wy, wz)` downward at 1m granularity to find each column's actual topmost-non-air voxel. Treats water as content (matches the heightmap and slice-tile passes); using `IsSolid` would skip water surfaces and never reveal lakes.

**View radius (adaptive zoom) tracks the reveal distance.** `Minimap.ComputeRevealRadius` = effective vision range × `revealMultiplier`, where the effective vision range folds in the player's vision stats (`EStat.Vision` — base perception, buffs, gear via `ComposeStat`). That radius drives BOTH the charted map reveal (the fog banked to the world map) and the zoom, so anything extending the player's sight widens both together. The view radius (zoom) is `Minimap.ComputeVisibleRevealRadiusMeters() × viewRevealMargin`, computed in `Hud.UpdateMinimapViewRadius` and damp-lerped. `ComputeVisibleRevealRadiusMeters` dims the reveal radius by the **global time-of-day sun brightness** (`DaylightFactor01` — `SkyController.CurrentPrimaryIntensity` normalized by `SimData.dayIntensityBase`, with night-vision relief lifting the floor) and caps it by the **local painted fog** at the player (`losFogFullBlockMeters / fog01`, same fog model as the reveal viewshed), then floors it at `minViewRadiusMeters`. **Brightness must come from the global sky, NOT a locally-sampled `GetPerceivedLightWorld` — the local light sample flickers hard as the player walks under forest canopy and popped the zoom** (an earlier version did this and had to be reworked). Fog stays a local sample by design (it only flickers at a swamp-pool edge, which the Hud damp-lerp eases). So the map zooms *in* at night or in fog (out again with night-vision gear, at dawn, or leaving the fog) and *out* with a vision buff, up to "just smaller than the max reveal distance". `indoorZoom` divides the target in indoor mode so corridors read closer. Bird's-eye keeps its wider radius (multiplier is inside `ComputeRevealRadius`).

## Shader sampling rules (`shaders/minimap.gdshader`)

- **Height** comes from a single linear-filtered `texture()` sample (smooth elevation shading + smooth contour curves).
- **Tile and foliage IDs** sample the surface texture via `texelFetch` (nearest) — interpolated IDs would walk through intermediate slots and bleed grass/bush colors as a halo around tree stamps.
- **Terrain color** is a 4-tap **bilinear blend of resolved colors**: each corner runs through `tile_lut`, the four resulting RGB values blend by UV fraction. Smooth tile transitions without checkerboarding.
- **Foliage darken** is applied AFTER the bilinear terrain blend, using the nearest-sampled foliage id, so foliage edges stay sharp (no bilinear bleed of darken into surrounding fragments).
- **Plateau classification** uses the *nearest-sampled* height, not the linear one. Linear height drifts through values like 9, 8 across the boundary between content (slice center = 10) and empty (h = 0); fragments with `h0 ∈ [8, 12)` would falsely classify as "on the player's plateau" and render bright. Nearest snaps cleanly per cell. The contour line still uses linear height.
- **Topographic contour** is gated by `is_step` — neighbor differential ≥ 0.9 × plateau_height — so it only draws on actual cliff steps, not on every smooth ramp.

## Foliage stamps

`Minimap.StampPropsRecursive` — both `MultimeshPropSprite` (trees, tall grass, decor) and `SpriteBase` (LitSprite/FlatLitSprite — chests, doors, berry trees) expose `MinimapFoliageId`. Non-zero id stamps a single source pixel into the surface texture's A channel for both the outdoor heightmap AND the player's slice atlas layer (slice level computed from `floor((worldY - 1) / PlateauHeight)` so a prop on a plateau's top face lands in the slice that owns the ground).

## Crossfade state ping-pong

`Minimap.UpdateStateTransition` — captures the *previous* render state into A when mode/slice changes, sets B to the new state, and damps `_stateTransition` 0 → 1 over ~0.3s. The shader renders both states and `mix()`es by transition. Reference elevation is also damp-lerped (`_smoothedReferenceY`) so the per-pixel above/below classification glides at slice crossings instead of every pixel reclassifying in one frame.

## Reveal cadence

The disk re-fires every `RevealIntervalSeconds` (0.1s) regardless of player movement. The previous movement gate caused chunks loaded async after spawn to never get revealed until the player moved. The mask uses `max()`-merge so re-running on a stationary player is essentially free — only newly-loaded chunks contribute writes.

## Tuning knobs

Live as `[Export]`s on the `Minimap` node itself, authored in `scenes/subsystems/minimap.tscn` (embedded as a child of `GameClient` in `game.tscn`): `wallSlotColor`, `foliageColors`, `viewRevealMargin`, `minViewRadiusMeters`, `indoorZoom`, `revealMultiplier`, `revealInnerFraction`, and the "Line of Sight" group (`losEnabled`, `losEyeHeightMeters`, `losForgivenessMeters`, `losMarchStepMeters`, `losFogFullBlockMeters`). `World` references this authored node and calls `Initialize`; it doesn't create it. Material shader parameters (above/below brightness/saturation/contrast, contour interval/width/strength, mask radius) live on `resources/materials/minimap.tres`. The runtime-driven uniforms (`view_radius_meters`, `surface_texture_*`, `world_origin_xz_*`, etc.) are pushed each frame from `Hud.UpdateMinimap` — don't author them in the .tres, the runtime overwrites whatever you set.
