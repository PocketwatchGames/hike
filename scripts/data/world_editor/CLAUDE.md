# Painter ink + brush tuning (`scripts/data/world_editor/`)

## The document vs the tool (`WorldMapInkData`, `WorldMapBrush`)


**Nothing that only affects how the map is DRAWN or how a brush behaves is
reachable from `WorldMapData`.** The inks, band hues, depth shading, mark sizes
and danger swatches are `WorldMapInkData`; the falloff/flow/noise tuning is
`WorldMapBrush`. Both live in `scripts/data/world_editor/`, both are single
shared assets under `world_authoring/editor/`, and both are `[Export]`s **on the
painter node** (`world_map_painter.tscn`) rather than on the document. A world
does not carry its editor's colour scheme, and there is no per-world copy to
drift.

`WorldMapState.Ink` is therefore INJECTED — the painter passes it to the
constructor and every headless caller (the bake, `worldmap_check`,
`WorldMapResize`) leaves it null. That null is the invariant, not an oversight:
if a headless path ever needs an answer that currently only a colour method
gives, the answer must be split out of the inking rather than the ink handed to
the bake. `StampHitAt` is that split and the reason it exists — `worldmap_check`
compares partial display rebuilds against a full one, and it used to do it by
comparing `StampColorAt` output, which made a display concern a dependency of a
headless check AND was a weaker test (two different stamps inking the same
colour compared equal). It now compares the plan's answer: which stamp, and the
local Y it draws.

Two things deliberately did NOT move, because the bake does read them:

- **`mobLevelCount`** stayed on `WorldMapData` while `mobLevelColors` moved. The
  count decodes the scalar layer — a column stores a 0..1 fraction — so it says
  what the painted world IS, and changing it re-reads every column already
  painted. The swatches only say how those levels are shown. It was one array
  doing both jobs, where appending a colour silently re-scaled the world.
- **`roughen*` and `climbRouteMinWallVoxels`** read like brush tuning and are
  not. Weathering is derived from the pristine elevation for every column of the
  bake (`TerrainHeight`), and the climb minimum is handed to
  `WorldFinish.StampClimbSurfaces`; both change the voxels.
