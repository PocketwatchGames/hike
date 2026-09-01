# World map tools (`scripts/worldmap/tools/`)

Each tool is an `IWorldMapTool` with its own `IWorldMapView`; see
[../CLAUDE.md](../CLAUDE.md) for the tool/view contract and the host.

## Carving and building (`VoxelEditTool`)


The one layer that is not a heightfield: a per-voxel byte saying either "this
voxel the height map would have filled is gone" (`EditCarve`) or "this voxel it
would have left as air is solid" (`EditAdd`).

**It is plain block drawing, and it is TWO tools over one implementation.** The
brush is a BOX — `Radius` wide, `Height` tall (**Q/E**, 3 m by default), hung off
`PaintY` (**R/F**, or alt+click).

**The box hangs off the level in the direction the tool writes**: a carve runs UP
from it, so `PaintY` is the first metre removed; a fill runs DOWN from it, so
`PaintY` is the new surface and the thickness goes under it out of sight. Either
way `PaintY` is the voxel you are acting ON, which is what lets one eyedropper
serve both.

**alt+LMB lands `PaintY` EXACTLY on the elevation sampled** — the highest floor
under the cut. Not one above it: that was tried, so a carve would preserve the
floor it sampled rather than take it, and it made the HUD disagree with every
pick. An eyedropper whose value is not the value you pointed at is not an
eyedropper. Two things about which floor: a FLOOR, not merely the highest solid
voxel, because on rock the latter is the cut plane itself, which is not a surface
anyone pointed at (there the pick is a no-op); and under the CUT, because
sampling the column's true top hands back the hilltop over a corridor instead of
the corridor's own floor, which is the one place the eyedropper is most useful.

**The hover readout reports the same number.** It read `TerrainHeight` — the raw
height field — so inside a passage it named the hilltop overhead while the map
drew the floor beneath and the pick sampled that floor. Three answers to one
question, and the two that were wrong were the ones an author checks a pick
against. Anything the painter says about "the elevation here" now comes from
`CutawayFloor` at the active view's plane. **`Tunnel` turns the box to
air on LMB; `Block` turns it to ground.** RMB, on both, reverts the box to the
height field.

One direction per tool, because that is the painter's convention everywhere
else: **LMB does the thing and RMB undoes it** — water fills / removes, climb
marks / unmarks, paving lays / lifts. Carve-on-one-button and build-on-the-other
made RMB a second POSITIVE action, the one shape no other tool has, and it left
the tool's own name lying about which button it was for. The two differ by
`PaintsSolid` and nothing else, so `BlockTool` is a six-line subclass rather than
a second tool to keep in step.

**The layer records only a DISAGREEMENT with the height field.** Carving a voxel
that is already air writes nothing, filling one the terrain already fills writes
nothing, and RMB writes `EditNone` outright. Three things fall out, and the first
is the one that matters:

- **Erasing a tunnel restores the hillside and cannot leave blocks standing
  where the height field has none.** A box straddling the surface carves only
  the solid half, so reverting it has nothing above the terrain to put back.
- Drawing a block back into a hole you cut leaves the mask genuinely *empty*
  rather than holding a cancelling pair, so there is no separate "revert to
  natural" binding to find.
- `CanSpawnAt` stays honest, because it asks whether the top solid voxel is
  still the painted ground.

**The cutaway is INDEPENDENT of the floor being painted** — **T/G**
(`EditorClipUp` / `EditorClipDown`) or **alt+wheel**, not R/F. That is the whole reason a 2D map
can answer "how tall is this corridor": sweep the plane up through one you have
cut and the metre it stops being drawn as open is its ceiling. While the plane
was pinned to the height being written it could only ever show the one slice
being painted into, which is what made an existing tunnel's height unreadable
and a too-tall one hard to fix.

It lives on `WorldMapState` beside `ShowWater` — display-only, never saved —
rather than on any tool, so every cutting view cuts at the same plane and
switching between them keeps the slice you were reading. `IWorldMapView.CutsAway`
is the one thing a view declares; the painter resolves the clip once per rebuild
and passes `int.MaxValue` for anything drawing the world from above.

**Six views cut**: the two voxel-edit tools, the **water** tool (so a passage can
be flooded deliberately), the **climb** tool (so a route can be painted on a
passage's walls), the **entity** tool (so a chest can stand in one) and the
**paving** tool (so a road can run through one, or under an arch). They share two
views, not six: the voxel-edit and climb tools take `CutawayElevationView`, and
the two that place something ON a floor — paving and entities — take
`CutawayGroundView`, which is the plain ground map above the plane and the
cutaway below it. Tools that differ in what they WRITE and in the ink the outline
pass lays over them, not in how the terrain is drawn, share the view; copies
would only drift.

Both remaining per-column layers are worth knowing about: a climb route and a
water surface are both per COLUMN, so marking one inside a passage marks the
whole column, exactly as draining a passage drains the lake above it. **Paving is
per column too**, and it is the same limit: a column carries ONE road, so a
passage cannot be paved under a paved hillside.

**An entity remembers the floor it was placed on** (`EntityPlacement.floorY`),
because nothing about the column describes where a passage's floor is. Two cases,
and the split is whether the floor is the TOP of the column:

- **On top** — the surface, or a deck you built. It records `OnTheGround` and is
  re-seated from the column at every bake, so it follows: raise the hill under it
  and it rises, dig a pit and it drops in, delete the deck and it lands on the
  ground. Seated from the top SOLID VOXEL, not from `TerrainHeight` — the two
  differ wherever something was carved or built, and an entity that says it
  stands on the ground should stand on the ground that is actually there rather
  than hang at the height the height field still claims.
- **Under something** — a passage, the underside of a deck. It records the
  ABSOLUTE Y, because re-seating would put it on the roof, and an offset from the
  ground would drag it around when the hill above is repainted. A passage is
  carved at a fixed Y and stays there.

`OnTheGround` is also what a document written before the field existed loads
with, since a field absent from a `.tres` keeps its C# initializer. The seat is
re-resolved as an entity is dragged, so sliding one along a passage keeps it on
that floor and sliding it out of the mouth puts it back on the ground.

The entity map itself switches: **lowering the plane turns it from the ground
layer into the cutaway**, because underground there is no ground TYPE to show —
the question becomes which floor is down there to stand something on. Entity
MARKERS stay visible whatever the plane is doing — and on every other map that
shows props, since the painter composites them. They are single texels and the
thing you need most from them is where they are; hiding them the way stamps hide
would make an entity you are looking for impossible to find.

**It starts parked at the top of the world, i.e. NOT CUT**, and `IsCutAway` says
so. A cutting view is then EXACTLY an uncut one until the plane is lowered, which
is what lets a tool whose ordinary job is the surface — the water tool — share
the mechanism without opening full of rock.

**The plane spans the world's whole height, so REACHING it is the problem.** On
the default document it starts at Y=79 and T/G walks it a metre a press, which
put most of a hundred presses between picking up a tool and the plane touching
any ground — and the painter said nothing about where the plane was, so the keys
read as doing nothing at all. Three things answer that, and none of them is a
bigger stride:

- **alt+wheel** is T/G under the hand already on the mouse, and scrubbing is how
  the plane is actually moved. Wheel DOWN lowers it — the same inversion the
  brush notch takes, and the same sense as scrolling down a page.
- **alt+RMB** aims it at the floor under the cursor plus `cutawayHeadroom`.
- **The HUD reports it on EVERY tool**, including the ten whose view does not
  cut — the plane is shared state and T/G moves it whatever is active, so a tool
  that said nothing about it made the control look broken. `CutawayText`
  distinguishes the three states that matter: a Y, "off (above the world)", and
  "this tool does not cut". The two tools that used to print the Y themselves
  (voxel-edit, paving) no longer do, so it is stated once.

**Two gestures bring it down to where you are working**, because a plane parked in
the sky is useless and hunting for it with T/G is worse. `IWorldMapTool.CutawayFor`
lets a tool ask for a plane when it is picked up — the voxel-edit tools want
`PaintY + cutawayHeadroom` (3), just over the level they paint at — and **alt+RMB**
aims it at the floor under the cursor plus the same headroom. The alt+RMB gesture
is live only where a cutaway is actually on screen; elsewhere it would swallow a
press whose effect nothing can show, so there it keeps its ordinary tool-pick
meaning. It reaches `BeginStroke` through `EStrokeMods.Secondary`, which is not a
modifier key but arrives the same way and is the only thing that distinguishes
alt+LMB (aim the brush) from alt+RMB (aim the plane).

**The view draws the highest FLOOR under the cut** — `WorldMapState.CutawayFloor`,
the highest solid voxel with air above it at or below the plane, in its own
elevation band. Where the cut is open that is simply the ground; where the cut
passes through rock it is the floor of the highest hollow beneath, so **the map
sees THROUGH the mountain to the passage under it** instead of stopping at the
rock. Only a column solid the whole way down with nothing hollow anywhere beneath
draws `cutawayRockColor` — the one case with no floor to draw at all.

**A floor found through rock keeps its EXACT band and is DITHERED** against
`cutawayRockColor`, checkerboarded on absolute display pixels (so the pattern
runs continuously across a whole buried passage instead of restarting every
metre). Not tinted: a tint moves the band into a shade some other height already
owns, which is the one thing an exactly-banded palette exists to prevent, so the
buried passage would start lying about its depth. The texture says "you are
looking at this through something" without touching the colour. It is the same
distinction the erase refuses to act on.

Step outlines follow the same floor, or they would contour one surface while the
colours show another; rock with no floor reads as a single flat level so that
only its edge inks and never a contour inside it. Water is composited only where
the cut is open to it, since a floor seen through rock is not under the pool
standing on top of that rock.

**RMB removes the WHOLE thing you made at a column** — the contiguous run of that
tool's own edit touching the exposed floor, however far it reaches ABOVE the cut
(a carve stands above the floor it left; an added slab IS the floor and stacks
below it). A box-shaped bite out of a passage leaves a metre of it behind and
needs the brush aimed at a height you may not know; "undo what is here" needs
neither. Undo covers it for free, because `TunnelTilesAspect` snapshots
whole-height columns.

**It refuses to erase what the cut is not open to** — a dimmed, roofed passage is
left alone. You are seeing it *through* something, and erasing what you cannot
see the top of is how a network loses a corridor silently. Lower the cutaway into
it and it erases like anything else.

**Stamps are SLICED at the plane, exactly as the terrain around them is.** The
plan draws the topmost solid voxel of the scene *at or below* the cut, so
lowering the plane walks down through a building's storeys — walls at that level
as content, the floor beneath them wherever the room is open. Only once the plane
drops BELOW a stamp's base (`StampBaseY` against `ClipY`) is it hidden outright,
which is when the cut has genuinely taken the building away and its plan would
otherwise paint over the passage you are boring under it. At or above its base it
draws, footprint wash included, so a plane parked over everything renders exactly
what no plane at all would.

The plan is therefore cached per **(scene, rotation, slice)**, and every plane at
or above a scene's own roof collapses onto ONE entry with the unclipped plan —
without that clamp a plane parked over the world would mint an entry per stamp
seat. Scrubbing the cutaway through a building costs one entry per metre of that
building, which is what bounds the cache.

It drew a fixed top-down ROOF plan before, cached per (scene, rotation) with no
notion of the plane at all, so the only thing a cutaway could do to a stamp was
make it vanish: a house was a roof or it was nothing, and its ground floor was
unreachable from the map. `worldmap_check` reports the slice as at-plane against
below-plane columns per level of the tallest stamp — a solid slab answers "all
at" every level, a building answers "walls at, floor below", and a sequence that
does not move as the level descends means the roof plan is back.

It was briefly hidden from BOTH sides, on the theory that a stamp entirely under
the cut is not what you are looking at either. That is wrong twice: a cutaway
shows you everything at or below the plane, so a building under it is precisely
what you SHOULD see; and it made every stamp vanish the moment the plane moved
at all, since the plane starts above the world and almost nothing reaches it.

The seats come from the per-rebuild `StampPlan` — `SeatY` walks the whole
footprint, so asking per texel would put that scan in the map's hottest loop.
`worldmap_check` runs its partial-vs-full comparison a second time WITH a clip,
because pairing a candidate list with a parallel seat array is exactly the shape
the prefilter could break.

**Edits are visible on EVERY view, not only this one** — a bridge deck standing
above the height map is a fact about the ground you need while painting the
things beside it, the same argument stamps and climbing routes are drawn
everywhere. The seam is `WorldMapState.SurfaceBelow` (view colours) and
`StandSurface` (step outlines), read instead of `TerrainHeight`;
`IWorldMapView.ClipY` is `int.MaxValue` on all of them but the tunnel one, so
only that view cuts anything away.

**`TerrainHeight` is deliberately NOT moved by the edit layer.** It is the
painted heightfield — what erosion is measured against, what the bake stamps
terrain up to, what a stamp seats on. A carve is a hole in that surface, not a
lower surface, and folding the two together would make weathering and grading
read a hillside that is not there. The one place the two meet is `CanSpawnAt`,
which asks that the top solid voxel still BE the painted ground: it refuses a
column whose top was carved away (the scatter would hang over a hole) and one
built over (the scatter would grow under a deck).

The per-column summary of how high the edits reach (`_topEdit`) is what keeps
this cheap: an unedited column answers every surface query with one array read,
and only edited ones walk their voxels. Anything rewriting the mask wholesale —
an undo restore, a resize — calls `InvalidateVoxelEdits` rather than maintaining
it.
