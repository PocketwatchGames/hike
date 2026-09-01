# The painter host (`WorldMapPainter`), palette, placements and inspector

## Host (`WorldMapPainter : Node3D`)


A **pure 2D in-game program** — no live `World`, no `GameCamera`, no chunk
meshes. Launched from the main menu (`GuiMainMenu.OnStartPainter` →
`Main.StartPainter`), which just instantiates + `Init()`s the scene, so it opens
instantly. Holds the tool list + a colourised `Rgba8` display image fed to

**WHICH document opens is picked in the menu.** The World Map Painter button
shows the same file selector New Game and the World Editor use, listing every
`WorldMapData` under `GuiMainMenu.worldMapSearchDirs`; `Main.StartPainter` loads
the pick and assigns `painter.data` before `Init`, and an empty pick keeps the
document the scene authors. The `.tres` files are filtered by the class named in
their HEADER LINE rather than by loading them — the layer images, the brush and
the placements list share that directory and that extension, and loading a
document to find out what it is pulls in its whole `WorldGenData` graph. There is
no "new document" row: a document is a `.tres` plus the layer files it names, so
making one is an authoring step, not something a picker can mint.

**Escape opens a menu rather than leaving.** `WorldMapPauseMenu` is a full-rect
Control in the HUD layer, so an open menu also stops the canvas painting under
it, with Save & Bake / Resume / Quit to Menu wired to the Actions the painter
assigns — the same three paths Ctrl+S, Resume and the quit callback already
take, not a second implementation. Every other binding is refused while it is
open; **Ctrl+S is the exception**, since it means the same thing whatever is on
screen. With no menu wired Escape keeps its old meaning and quits, so the
painter is never a screen you cannot leave.
**Prop dots draw on every ground-based view** (`ESpawnPreview` is a flag SET, not
a choice): props are what the ground is furnished with, and nothing else on those
maps answers "is this spot already taken". Mob dots stay with the layers that
paint mobs — they are about encounters, not terrain — and draw last, since two
dots cannot share a cell and what LIVES somewhere is the more urgent answer.

**Props and mobs are the same machinery twice** — one `SpawnSetData` type, two
palettes (`propSets`, `mobSets`), two rasters of identical shape, one column
routine at bake, one dot preview parameterised by `IWorldMapView.PreviewLayer`.
They need separate LAYERS rather than separate types because a raster holds one
set per column: sharing a layer would make painting wolves erase the pine stand
under them. A mob set is simply a set whose tree and foliage slots are empty and
whose `entities` list carries the mobs.

**A mob set's `entities` is a PAINTER-OWNED list, forked from worldgen's.**
`mob_sets/*.tres` point at `resources/data/world_authoring/spawn_lists/ambient_*.tres`,
not at the `surface_entities_*.tres` that `zone_gen/*.tres` uses, even though the
ambient lists were filtered out of exactly those files. The split is by how a
thing wants to be placed: a brush places by AREA, which suits what you want many
of and do not care about the exact spot of — mobs, forage, traps, berry trees,
cacti. A well, a climbable tree, a chest or a goblin camp is a landmark, and the
map is the place to AIM one, so those live in `entityPalette` and go down one at
a time. Painting and hand-placing are not two qualities of content, they are two
questions about where it goes.

It is a fork rather than shared entries, and the reason is that worldgen is
frozen rather than co-maintained. Sharing would mean promoting the embedded
`[sub_resource]` entries to standalone files and rewriting worldgen's lists to
reference them — and there is no `spawn_check` to prove that rewrite preserved
what those lists resolve to, only reading the diff. Copies are the honest
encoding now that the two lists genuinely mean different things: worldgen's is
"everything a generated forest contains", the painter's is "the ambient stuff a
brush may produce". Rebalancing one SHOULD NOT move the other.

**Hiding a list from the painter needs no mechanism** — `propSets`, `mobSets` and
`entityPalette` are explicit authored arrays and are the only doors in. There is
no discovery and no scan (`SceneTool` globbing `.hikescene` is the one directory
scan in the painter), so a worldgen list is invisible the moment nothing names
it, and moving files between directories filters nothing.

**A palette entry is a FAMILY, and the member is picked per placement.** One
`goblin` row covering all 13 goblin descriptors, one `npc` row covering every
villager rig and outfit — because the question the palette answers is "what am I
placing", and "which biome's goblin" is a property of the one you placed. The
list is 26 entries where it was 54.

That is also what makes the map's highlight useful: selecting `npc` lights up
**every** NPC in the world, not one villager type. It needed no separate
mechanism, because `EntityPlacement.IsFrom` already matched on which palette file
a placement (or its fork) came from — collapsing the palette is the whole change.

**Family is `SpawnEntryData.FamilyName`, and it is the ONE answer.** The file's
basename, or the explicit `family` export where that is not enough. Everything
that groups placements reads it — the highlight, the hover, `worldmap_check`'s
by-entry listing — and a local reimplementation is how six migrated NPCs came
back reported as `NpcSpawnEntry`. It is deliberately NOT `DisplayName`, which now
decorates a family with the variant (`npc: villager_elder_m *`): a match must not
depend on a decoration, or an NPC would stop matching the entry it came from the
moment its appearance was picked. The tool's option row shows the family; the
hover readout and the panel title show family + variant, since there the
individual IS the answer being asked for.

**`family` is the explicit form, for an entry whose family is not its file.**
Nothing in the palette needs it today — it is what the seven migrated NPC forks
in `placements.tres` declare, since a hand-written fork has no `resource_name`
for `FamilyName` to fall back on. It is also the mechanism if a second authored
row of one family ever returns (see the merchant, below). A runtime fork needs
neither: `Duplicate` carries the export and `EditableEntry` sets the name.

**Identity is now WHICH FAMILY, not which member** (`IsIdentityProperty`:
`family`, `variants`, `appearances`, `scene`, `altScene`, `outfit`, `palette`). A
fork keeps its palette file as its NAME, so what must stay un-editable is
anything that can move an entry OUT of its family — otherwise a placement that IS
a drake is still called `npc_hermit` by the panel title, the hover readout,
`worldmap_check` and the highlight. `variants` / `appearances` are hidden for a
sharper reason: they DEFINE the family, so showing them is the one edit that
could widen it from the inside.

**Which member is safe to edit precisely because the candidates are
constrained.** `SpawnEntryData.ResourceCandidates` is the resource analogue of
`NameCandidates` — the entry answers what its own row may hold, and the panel
uses that verbatim instead of the project-wide scan. The `goblin` entry offers 13
goblins and cannot reach a spider; `npc` offers its 8 appearances. Every mob
family constrains its row, including the single-member ones, or a dog could be
turned into a drake and still be called a dog.

**A row an entry type cannot use is hidden by that type** —
`SpawnEntryData.ShowsProperty`, the instance form of the static rule, which is
what the panel and `worldmap_check` both call. An NPC hides three:

| Hidden on an NPC | Because |
|---|---|
| `descriptor` | the two humanoid descriptors resolve to the SAME `MobData` and differ only in a bestiary `displayName`, so the row cannot change anything an author can see. Who this individual is was already settled by the appearance and the conversation. |
| `levelOverride` | a difficulty tier for a villager in a doorway is meaningless; the field belongs to the mobs it was added for. |
| `initialBehavior` | an NPC runs its conversation and its idle pose, not a combat brain's entry state. |

**Row order is the entry type's statement** (`PropertyOrder`), because
declaration order is the C# field order across a hierarchy — which floats the
base class's bookkeeping above the fields an author came to set. An NPC reads
appearance → idle animation → conversation → language → recruit template, then
everything unnamed in declaration order. The panel and the check share ONE
enumeration (`WorldMapEntityInspector.OrderedProperties`), so the report is of
the panel that will actually be built.

The lists are AUTHORED (`MobSpawnEntry.variants`, `NpcSpawnEntry.appearances`),
not derived, because neither derivable answer is right: grouping by `SpeciesData`
is per-BIOME (one swamp goblin's plain/elite/torchbearer share a species, the
forest goblin does not), and a filename prefix makes a naming rule load-bearing
with nothing enforcing it — the same reasoning that keeps the animation-clip list
un-filtered. Authoring it also lets the author say where a family's edges are,
e.g. whether a cube and a sphere slime are one creature.

**An NPC's look is ONE pick, not three.** `NpcAppearanceData` bundles
`scene` + `outfit` + `palette`, because those three are not independent: an
outfit names meshes that exist only in a particular rig, and the recolor names
them again. As three rows the only thing preventing a male rig in a female outfit
is the author remembering, and the failure is SILENT — the meshes do not resolve
and the NPC spawns in its rig's default clothes. It also sidesteps both of the
panel's standing read-only cases at once (`PackedScene` is excluded as a rig
choice, and `outfit` is an array). `NpcSpawnEntry` keeps the raw trio for
worldgen's house lists, which author it inline and always have; the bundle wins
where set, resolved once through `Rig` / `Outfit` / `Recolor` so the spawn path
and the idle-animation picker cannot disagree about which rig is in play.

**`idleAnimation` stays its own row** rather than joining the bundle: it is
genuinely per-individual (villagers built from one `MobData` each rest
differently) and its picker is driven by the chosen appearance's rig.

**Level is a field on the ENTRY, never the descriptor.**
`MobSpawnEntry.levelOverride` (negative = the descriptor's own). A descriptor is
shared by every placement of that variant AND by worldgen's spawns, and
`EntityPlacement`'s fork is shallow, so editing `descriptor.level` through the
panel would retune all of them at once. It keeps the field's semantics — a FLOOR,
with the painted difficulty layer still adding on top through
`SpawnContext.MobLevel`.

**A row is shown only if it can change something**, which is
`ShowsInPlacementEditor` — two independent reasons not to, kept as separate
questions because they mean different things: the value cannot reach a hand
placement (`IsHandPlacedProperty`), or it is implicit in the entry that was
chosen (`IsIdentityProperty`). What is deliberately still shown is the third
case: a property that WOULD vary per placement and simply has no editor yet — a
chest's `lootItems`, an NPC's `inventory` / `loyaltyGifts` / `itemPreferences`,
all of which want list editing. Those are marked `no editor yet` rather than
hidden, because a dimmed row otherwise reads as "this cannot change" when the
truth is "not here, not yet".

`spawn_entries/mobs/` is 11 family entries covering all 33 `MobDescriptor`s
(biome variants, elites and torchbearers included) — it was 33 one-field
wrappers, each holding nothing but a `descriptor`. `elites/elite_*.tres` are
excluded: they are `EliteMobDescriptor`, which decorates a descriptor rather than
being one.

**NPCs are palette entries like anything else** — `NpcSpawnEntry` is a
`SpawnEntryData`, so the entity tool places one, the property panel reflects its
fields (language, conversation, appearance, idle pose, recruit template), and the
copy-on-write fork makes each placement its own individual. There is ONE row —
`npc` — and its eight looks live in
`resources/data/worlds/shared/npcs/appearances/`, extracted from the per-NPC entries
they replace; the conversation, language, idle pose and recruit template each one
used are picked per placement, which is what let eight files become one.

**The leather merchant is a PLACEMENT, not a palette row.** Its `inventory` and
`loyaltyGifts` are arrays that no placement editor can author yet, which is an
argument for keeping the merchant that exists — it lives in `placements.tres` as
a fork carrying its own stock — and not an argument for a second palette entry,
which would have been a workaround for the missing list editor sitting
permanently in the list. The cost is real and worth knowing: **a NEW merchant
cannot be given stock from the painter.** Place an `npc` and author its
`inventory` in the resource, or add the list editor.

They remain **copies** of the NPCs embedded in worldgen's house spawn lists
(`world_authoring/spawn_lists/hub_house01`, `house_hermit`, `hub_house02`,
`village_house01`-`04`), not references to them — the same fork convention the
mob sets follow, so retuning a village cannot silently move what the map paints
or the reverse. The hermit and Talia carry a `recruitTemplate` and are
recruitable where they are placed.

**A spawn entry carries only what a hand placement can change.** Four fields
came off the panel and two off `NpcSpawnEntry` outright, on one rule: a control
that cannot change the result invites tuning that does nothing.

| Removed / hidden | Because |
|---|---|
| `squareMetersPerSpawn`, `placeAtAnchor`, `clusterCountMin/Max` | container-edge rules — the area roll and `SpawnGroupData`'s scatter. A hand-placed entity is one entity at one spot by construction. **These are no longer on an entry at all**: they moved to `SpawnListRow` / `SpawnGroupRow`, and a placement has no row, so there is nothing left to hide. |
| `minSpacing` | a rejection radius is how densely a PASS may sprinkle something. Authored in 4 files project-wide, all scatter lists or worldgen fixtures, never a palette entry. Now skipped for an authored position. |
| `initialBehaviorChance` | a POPULATION fraction ("a quarter of spawned goblins start in Wander"), authored in 50+ scatter entries and no palette one. It has nothing to be a fraction of for one placement, so an authored position always takes the behaviour it names. |
| `tamed`, `persistent` (deleted) | the starter-companion pair. Becoming a companion is a RUNTIME transition owning both halves — `Mob.Tame` flips `MobSimState.Tamed` at `MobData.tameLoyalty` and `Sim.PromoteCompanionToPersistent` moves the mob into the persistent store at that same moment. Nothing authored either flag. |

The last row is the one worth not undoing: a spawn-time shortcut is a second way
into a two-part transition, which is how the two parts come apart.

**A placement's R/F turn reaches the bake.** `StampEntities` sets
`SpawnContext.FacingY` from `placement.rotation` and clears it again — the shared
bake context is the one caller with a facing, and everything else it answers must
keep giving a scattered entity its random yaw. Without it the tool's rotation
readout was decorative and every hand-placed NPC faced a direction the hash
picked, which for a villager standing in a doorway is the whole point of aiming
one.

The rate and the cluster knobs are structurally out of reach now rather than
hidden: they live on the ROW that names an entry (`SpawnListRow.squareMetersPerSpawn`,
`SpawnGroupRow.countMin`/`countMax`/`placeAtAnchor`), and a hand placement holds a
bare `SpawnEntryData` with no row at all. An entry dropped into a spawn list by
mistake is still inert, for the same reason as before — its row would default to
`squareMetersPerSpawn = 0`, and `RollAreaChance` returns false at 0.

`SpawnEntryData.IsHandPlacedProperty` therefore only has two names left to hide,
`minSpacing` and `initialBehaviorChance`. The rule it encodes is unchanged: a
control that cannot change the result is worse than a missing one, because it
invites tuning that does nothing. Which fields the path reads is the entry
class's business, so the answer lives there rather than in the UI.

**`minSpacing` is skipped for an authored position, which is why it can be
hidden.** It is a rejection radius — a statement about how densely a PASS may
sprinkle something, not about a spot someone chose — and the whole project
authors it away from its 0.5 m default in exactly four files: two scatter lists
and two worldgen fixtures. Not one palette entry. It stays exported for that
path and no longer runs for a placement, on the same argument as the lateral
clearance beside it: the author put the mark exactly there and every other mark
is drawn on the same map. The cost is that a hand-placed entity may now land on
a SCATTERED prop, which the map only shows as per-column dots — thin at 0.5 m,
but not nothing.

**Hand placing an entity does NOT guarantee it spawns, and every rejection is
SILENT.** `TrySpawn` still runs its placement gates, and a rejected entity is
simply absent from the baked world with nothing said — the map cannot show it,
so short of going to stand where you put it there is no way to find out. Two
consequences:

- **`AuthoredPosition` is set for these, and only for these.** It skips the
  lateral-clearance gate, which wants 4-connected air around the anchor — which
  is exactly what a wall is not, so a villager placed in the doorway you aimed
  at would silently never spawn — and the `minSpacing` overlap gate with it. It is the same claim `WorldGen` makes for an
  entry it drops on an authored subscene marker. It **cannot** go on the shared
  `SpawnContextForBake`, because `RescatterColumns` uses that context too and a
  SCATTERED mob must keep the gate: rejecting a 1-voxel tunnel is what it is
  for. So `StampEntities` sets and clears it per entity, the same way it does
  the facing.
- **`worldmap_check` reports the flat-terrain gate**, which is the one
  answerable without a built world and the one most likely to bite: a mob,
  forge, fountain, campfire, signpost, knowledge stone or trap needs its column
  and all eight neighbours at one height, so anything placed on a slope or on
  the lip of a step is dropped. It found five in the default document the day it
  was added. The remaining gates (`minSpacing` against a neighbour, the hazard
  keep-out, the navigation-walkability probe) need voxels and are not checked.

Difficulty is deliberately not on the set — it is its own scalar layer, so
"which creatures" and "how dangerous" are painted apart. A level band on the set
would need "wolves-easy" and "wolves-hard" as separate assets.

**Difficulty is its own layer and its own colouring.** `MobLevelTool` paints a
per-column level into the shared scalar image (`R` = mob level, `G` reserved for
climb), and its view recolours the whole terrain one shade per level so a glance
answers "how dangerous is it here" with nothing competing for the colour.

The field is CONTINUOUS and smoothed **where it is painted**: the brush eases
toward the level you picked so its falloff is the gradient, and the map lerps the
ramp stops linearly so a soft edge reads as a fade rather than a ring. It is
rounded to a whole level only where a mob needs one. Smoothing at paint time
rather than at bake is what keeps the map honest — a bake that re-smoothed would
mean the shades on screen were not the levels the mobs got, the same
preview-versus-bake gap the spawn dots exist to close. Worldgen lerps difficulty
across a noise field for the same reason: a raw per-column byte would step a
whole level in one metre.

**Subscene stamps are a LIST, and the tool is a pointer, not a brush.** Click
empty ground to drop the palette's scene, click a stamp to select it, drag to
slide it (grabbed where you clicked, so it does not snap its anchor to the
cursor), **R/F** to turn it 90°, RMB to delete the one under the press. The press
only ever DECIDES what the stroke is about — it cannot place, because the right
button fires it too and a right-click on bare ground would drop a building for
the erase that follows to delete again.

`IWorldMapTool.TouchRect` is the other half of `LastPaintRect`, and undo needs
it: a tool that writes outside the brush disk must say so BEFORE it writes, or
the snapshot cannot cover it. The lake tool's body erase clears seeds anywhere
the fill reached, so it touches the whole map.

Two things this needed from the host, both small and both general: `Options` is
filled by **scanning the subscene directory** rather than an authored palette (a
`.hikescene` is made in the world editor, and a registration step in a second
resource is one that gets forgotten), and `IWorldMapTool.LastPaintRect` lets a
tool report the columns it actually changed — a stamp moves its whole footprint,
which is nowhere near the cursor's disk, and the move has to repaint the ground
it LEFT as well as the ground it arrived on.

**Stamps draw on EVERY view, not only the scene tool's.** A building is a fact
about the ground you need while painting the things that sit beside it — the
same argument climbing routes and spill edges are inked everywhere — and a
footprint you cannot see is one you scatter props into. The one view that
holds anything back is the tunnel cutaway, which shows a stamp only where it
reaches the cut plane — see Carving and building. The composite lives in
the painter's fill pass (`WorldMapState.StampColorAt`) rather than in `SceneView`,
which is now just the plain ground map; the selection highlight comes from
`IWorldMapTool.SelectedPlacement`, which only the scene tool answers, so the plan
stays plain while another tool is active.

The candidate stamps are resolved ONCE per rebuild (`StampsIn`) instead of per
texel. The hit test walks the placement list, and a full rebuild is ~295k texels,
so asking per texel would make drawing the map cost more the more buildings the
document holds. That prefilter is also the thing most able to break the
partial-rebuild invariant, so `worldmap_check` reports
`partial-vs-full disagreements` over chunk-sized rects and it must be 0.

**A stamp draws its own contents**, seen from above: the topmost solid voxel of
each footprint column in its block's `minimapColor`, shaded by height within the
scene so walls read brighter than the floor they stand on. That is what makes a
stamp placeable at all — which way a house faces and where its walls are cannot
be read off a rectangle. Built once per (scene, rotation) and cached beside the
rotated state, because the map asks per texel per rebuild and scanning a
building's full height every time would show. Columns the scene leaves empty (a
courtyard, the gap around a tower) still take the wash, or a stamp's extent would
vanish wherever its scene authors nothing.

**Y is derived, with an authored nudge.** The seat is `WorldGen`'s own
`FootprintPlateauY` — the most common ground level across the footprint, ties to
the lower — refactored to take the ground lookup instead of a `HeightMap`, since
the painter has none. Averaging or taking the max would float a building over a
dip; the stamp overwrites its whole bbox, so cutting in is self-correcting and
floating is not.

`SubscenePlacement.yOffset` nudges that seat, and is deliberately a NUDGE rather
than an absolute Y: the seat is recomputed from the ground under the footprint,
so a scene follows terrain that moves under it while the offset keeps saying "and
a metre lower than that". An absolute Y would pin the building while the hill
walked out from under it. **alt+click solves the nudge from the ground under the
cursor** — point at the terrace you want the floor on — because the number that
matters is where the floor LANDS, not how far it moved. `SeatY` is one method
used by both the bake and the tool, so the height the HUD shows is the height the
bake uses.

Footprints are excluded from `CanSpawnAt`, the way worldgen reserves them with
`MarkNoSpawn`.

**Entity marks draw on EVERY view that shows props**, composited by the painter
(`WorldMapPainter.DrawEntityMarks`) rather than returned by `EntityView`, exactly
as stamps are — so `EntityView` is now just the ground map. A chest or a well is
a fact about the ground you need while placing the things that stand beside it,
and scene placement is the case that makes it urgent: a house dropped on top of
one is the mistake this prevents. The gate is `ESpawnPreview.Props`, the same
flag the scatter dots use, so "wherever props are visible" is one answer rather
than a second list to keep in step.

Two differences from the scatter dots underneath them. They are drawn LAST, over
the step outlines and the dots — a mark you placed outranks a contour line and a
previewed roll — and they are NOT gated on zoom, because a dot is an impression
of a random roll while an entity is one thing you put somewhere and finding it is
the reason you are looking. The pass walks the placement LIST, not the texels:
marks are sparse and one metre each, so it costs the number of entities, where
asking `EntityAt` per texel would walk the whole list ~295k times a rebuild —
the shape that made stamps the slowest thing on the map.

**`EntityTool` is that same interaction with two parts swapped**, which is what
the scene tool was shaped for: the palette is `WorldMapData.entityPalette` and
the hit test is a proximity check, because an entity is a point rather than a
footprint. Everything else — press decides what the stroke is about, drag slides
from where you grabbed, R/F turns the selection, RMB deletes what was under the
press, once — is the same code shape.

**Which mark is which is answered by the cursor and by the palette**, because
every entity draws the same one-metre dot and the map cannot say what one is.
The mark under the cursor GROWS (`entityMarkHighlightRadius`) and the HUD names
its entry, so a grab is aimed rather than guessed at — the hover asks the tool
(`EntityUnder`), which runs the same proximity test the press grabs with, so what
lights up is exactly what a click would pick up. Colour answers the other
question: every placement of the entry the palette has SELECTED is inked as a
match (`entityMatchInk`), so "where are the chests" is answered by choosing the
chest rather than by clicking every dot, and the one placement being edited is
inked over that (`entitySelectedInk`). Matches grow to the same size the hover
and the selection do: at a zoom where the whole world fits on screen a mark is a
few pixels, and a colour difference that small is not an answer. A placement's own FORK still counts as a
match (`EntityPlacement.IsFrom`, off the palette name the fork keeps): a chest
whose text has been edited is still a chest, and it is the one most worth
finding.

None of those three is spatial, which is what the repaint has to respect: a hover
or a selection change repaints the two marks involved and nothing else (this runs
on mouse motion), while changing the palette entry is a whole-map answer and goes
through the deferred `RebuildFull`. Selecting an entity has to repaint the one
that was selected before — it can be anywhere on the map, and the rect under the
cursor says nothing about where. A grown mark also reaches OUTSIDE its own cell,
so `DrawEntityMarks` allows for the growth when rejecting placements against the
rebuild rect, or a highlight is clipped off at a partial rebuild's edge.

The palette is **`SpawnEntryData`, the same entries the scatter layers use**, so
one palette covers props, mobs, chests, loot and NPCs, and a hand-placed chest
spawns through exactly the `TrySpawn` path a scattered one does. A placement
references its entry DIRECTLY rather than by palette index, so reordering the
palette cannot silently turn every chest in the world into a goblin.

**A placed entity's properties ARE its entry's**, edited in the panel top-right
(`WorldMapEntityInspector`) — the text on a signpost, the conditions on a chest,
the descriptor on a mob. There is no parallel set of per-placement overrides,
because a `SpawnEntryData` subclass already exports exactly the fields its entity
type needs; the panel REFLECTS them, so an entry type written tomorrow is
editable the day it is written.

**A flags property is a compact DROPDOWN**, not a row of checkboxes —
`MenuButton` + a checkable `PopupMenu`, mirroring the Godot-side
`addons/data_ed/FlagsPropertyEditor` that `[CompactFlags]` opts into. That one is
an `EditorProperty` behind `#if TOOLS` and cannot be instantiated in the running
game, so the behaviour is mirrored rather than shared, and the rules are ITS
rules: the menu stays open across toggles (the value is a SET), the item id IS
the bit so nothing depends on menu order, and both a zero member (`None`) and any
MULTI-BIT alias (`All`) are skipped — neither is independently togglable, and an
alias item toggles several primaries at once with an ambiguous checked state of
its own. That last rule is what the checkbox version was missing: the knowledge
stone's `ELanguageComponents` has `All = Grammar | Numbers | Vocabulary1 |
Vocabulary2`, and it drew as a checkbox that flipped four bits.

**The panel is pushed on selection CHANGE, not per frame.** It rides `UpdateHud`,
and a click on the map reaches neither on its own — so a selection made by
clicking left the panel showing the entry it was last built for (the previous
signpost's text, or nothing at all for the first selection of a session) until a
tool or option change happened to refresh it. Per frame is not the answer: a
rebuild destroys the widget being typed into.

**The entry is copy-on-write** (`EntityPlacement.EditableEntry`). A placement
starts out pointing at the palette's shared `.tres`, so a chest nobody has
customized keeps tracking whatever that entry is retuned to; the first edit forks
it, and the fork — path cleared, palette file kept as its `resource_name` — saves
into `placements.tres` as a `[sub_resource]` belonging to that placement alone.
Clearing the path is not optional: a duplicate that kept it saves as an
`ext_resource` pointing back at the palette and the fork is silently thrown away
on the next load. `worldmap_check` reports the entity list by entry with a
`(n customized)` count, which is where that failure would show.

**"Is this entry the placement's own copy?" is `SpawnEntryData.IsOwnedCopy`, and
it is TWO shapes.** A fresh fork has no path at all, but one that has been saved
and loaded back carries the sub-resource path Godot gives an embedded resource
(`res://…/placements.tres::Resource_abc`) — which is not empty and is not a
palette file either. Every site that asked `string.IsNullOrEmpty(ResourcePath)`
therefore read a reloaded fork as SHARED: the next edit forked the fork and named
it after the file it was embedded in ("placements"), which took it out of the
panel title, the hover readout, the palette-match highlight and
`worldmap_check`'s by-entry listing, and `PlacementsAspect` stopped capturing its
fields for undo.

**Text applies as it is TYPED**, and the multiline rows are why it cannot be on
Enter: a signpost's text is several lines, so Enter is a NEWLINE there and never a
commit. Committing on Enter-or-focus-exit therefore left clicking away as the
only way to save one — and nothing in the painter takes focus away (the map
canvas is `FOCUS_NONE`, so clicking the map leaves the box focused), so typing and
then clicking the next signpost lost the edit outright.

The undo step is what commit-on-leave was really protecting, and it is kept by
BRACKETING instead: the first keystroke opens one step (`BeforeEdit`, which
snapshots the before state, so it must happen ahead of the first character
reaching the entry) and leaving the field closes it, so a typed sentence is still
one undo. Enter ends the step rather than committing a value that is already in.

The panel is still FLUSHED — `FlushPendingEdit`, which releases focus so each
widget's own path runs, and closes any open bracket — on a canvas press, on
Ctrl+S and whenever the panel switches entities, because the rows that are NOT
text (a `SpinBox` being typed into) still apply on focus-exit. Two rules keep
that safe: rows read and write through the placement they were BUILT for
(`_rowsOwner`), not through the one currently shown, or a write fired while the
panel is already switching lands one signpost's text on the next one selected;
and a write whose value has not moved is dropped, since focus-exit fires for a
box merely clicked into and forking the palette entry for that would silently
stop the placement tracking the palette.

Scalars get an editor — string, number, bool, enum, flags — and so does a
**single resource-typed field**, through a dropdown filled by
`ResourceTypeIndex`. That is what lets an NPC be given its own conversation,
language, recruit template or species without authoring a palette file per
villager, which is the shape `NpcSpawnEntry` asks for in its own class comment
("every placement is its own entity with its own dialogue and stock"). Signposts
and knowledge stones get their language the same way, off the same mechanism.

**A string field with a derivable set of values is a dropdown too.** Not from a
scan — from what the entry itself NAMES, through `SpawnEntryData.NameCandidates`:
`MobSpawnEntry` answers `initialBehavior` with its descriptor's brain nodes
(transitions already reference each other by `BehaviorNode.name`, so that IS the
valid set), and `NpcSpawnEntry` answers `idleAnimation` with the clips in the rig
it is drawn with. Both fail SILENTLY when mistyped — a bad behaviour name falls
through to the species default and a bad clip fails
`ModelAnimator.HasAnimation` — which is the case a free-text box is worst at.

The clips are read off the `PackedScene`'s **`SceneState`**, not by instantiating
it: the rig names its `AnimationLibrary` as a plain `ext_resource`, so the list
is reachable without building a node tree — and without running `_Ready` on
scripts that expect a live `Sim`, which the painter has none of. A rig whose
`AnimationPlayer` sits inside an INSTANCED sub-scene keeps its properties in that
sub-scene's state rather than this one's, so the walk finds nothing, `null` comes
back, and the field stays a text box. Degrading to free text is the required
behaviour for every un-derivable case, and it is why the list is **advisory**:
whatever the entry already holds is offered even when the candidates do not
contain it, marked `(not in this rig)`, so a value authored against another rig
is not silently rewritten by merely selecting the placement.

Ordering is the answer to relevance, not filtering: the human rig carries ~55
clips and about five are rest poses, so `idle*` sorts first and the rest follow
alphabetically. Hiding them would make the list a rule about naming that nothing
else enforces — a pose could reasonably be called `sit`.

**The resource candidates are SCANNED, not authored.** `ResourceTypeIndex` walks
`resources/` once per session and groups every `.tres` by the C# class it
carries, so a conversation written today is pickable today — the same argument
that discovers `.hikescene` stamps on disk rather than through a palette. Two
things it is careful about, both of which would show up as a picker quietly
offering an incomplete list (the worst failure one has, since it reads as "there
are none authored"):

- **Nothing is LOADED to identify it.** A `.tres` names its script as an
  `ext_resource` path, so the class is that script's basename, read off the
  text. Loading a resource to find out what it is pulls in its whole dependency
  graph — for one `WorldGenData` that is most of the game.
- **The header's `script_class` is not enough**, because plenty of files here
  were written without one (`spawn_entries/chest.tres` has a bare
  `[gd_resource type="Resource" format=3]`). The `[resource]` section's own
  `script =` line is the reliable answer, and it has to be that section's — a
  `sub_resource` names a script too, so taking the first one seen types a file
  as whatever it happens to embed.

The field's type comes from **reflection on the entry's C# type**, not from the
property hint: these are C# fields, so reflection is the exact answer while a
hint string is the editor's rendering of one.

**Two things stay read-only**, and neither is an oversight. **Arrays** (an
outfit, a merchant's stock, loyalty gifts) want list editing rather than one
pick. **`PackedScene`** is a rig choice rather than data — an NPC's `scene` has
to gender-match its `outfit`, and offering every scene in the project invites a
mismatch the panel cannot check. A value the scan cannot name (an embedded
`MobPalette` sub-resource) is offered as its own disabled `(embedded)` row, so
leaving it alone is what the row means; dropping it into "none" would read as an
empty field and invite a pick that silently discarded it.

`worldmap_check` reports, per palette entry type, which properties are editable,
which get a picker (with its candidate count), and which stay read-only — using
the panel's OWN classifier (`WorldMapEntityInspector.EditorFor`) rather than a
second copy of the rules. It is also the check on the scan: a picker row showing
0 candidates means the index failed to see that type's files.

Bare-key shortcuts are safe while typing for free — the painter
reads keys in `_UnhandledInput`, and a focused `LineEdit` has already consumed
them.

**The player spawn is the first palette entry, not a tool of its own.** There is
exactly one of it, so placing it MOVES it — a tool whose whole job is to move a
single point does not need a button in the toolbar, and having it here means it
is placed against the same map, with the same cursor, as everything else standing
on the ground. It cannot be deleted, only moved: a world without a spawn is a
world you cannot enter, and the bake would silently fall back to the origin (which
is still what an unplaced spawn means, for documents that predate this).

**Paving paints a BLOCK, where worldgen's roads paint an OVERLAY.** That is a
real divergence and a deliberate one. `CarveRoads` lays a `BlockSurfaceData`
tread as an additive skin (`SetOverlayIdWorld`) over whatever kit block is
already there: it blends softly into the terrain via the surface's own alpha, but
an overlay "names a LAYER, not a block", so it carries no footstep sound, no
speed multiplier and no dig yield, and it occupies the single overlay slot that
climbing routes and moss also want. A hand-painted road is a deliberate object,
so it gets to BE its material — the same call `StampDirtPatches` makes for dirt.
The cost is a hard 1 m kerb instead of a blended edge; if that matters, the
answer is an overlay-painting layer ALONGSIDE this one, not a switch on it.

Only ONE voxel is paved, and the kit channel is left alone: the kit says what
the column is made of, which a road laid over it does not change, and the rock
under a road is still the hillside's.

**WHICH voxel is the floor the map is SHOWING** — `CutawayFloor` at the shared
cutaway plane. With the plane parked over the world that is the surface, exactly
as it always was; lower it into a passage, or under an arch you built with the
block tool, and the stroke paves the floor down there instead. So the tool needs
no level of its own: **T/G already aims the cutaway** (and alt+RMB aims it at a
clicked floor), and a level you cannot see is a level you cannot aim. Solid rock
under the plane exposes no floor and takes no paving.

**A road on open ground records the surface SENTINEL, not that Y**, so it keeps
following ground repainted under it — and it resolves against the top SOLID
voxel, so it rides a deck later built over it and drops into a hole later carved
under it. Only a floor with something above it stores an absolute Y, because
nothing about the column describes where that floor is and re-seating would put
the road on the roof. That is the same split `EntityPlacement.floorY` makes, for
the same reason. `worldmap_check` reports the two counts, plus the paving whose
level is no longer a floor at all (**stranded**, and it bakes nothing) — which
only an absolute level can be.

**Erase clears the column, not the level on screen.** There is one paving per
column, so "lift what is here" cannot be ambiguous, and a seat stranded by
terrain repainted under it would otherwise be unreachable from every plane.

A column paved ON ITS SURFACE stamps no detail sprites and is excluded from
`CanSpawnAt` — worldgen's road pass deletes the scatter standing in its tread,
and grass growing through paving is the tell that a road was painted rather than
built. Paving on a floor UNDER the surface does neither: it belongs to that
floor and says nothing about the hillside over it, which is why both gates ask
`SurfacePavingAt` rather than `PavingAt`.

**Paving resolves inside `GroundColorAt`, not in the paving view**, so a road
appears on EVERY view that draws ground — you cannot lay props or mobs sensibly
along a road you cannot see. The colour is the block's own `minimapColor`, since
a block already authors what it looks like from above and a second palette would
only drift from it. Those views show SURFACE paving only: a road under an arch
belongs to the floor it is on, and colouring the hilltop with it would say the
hilltop is paved.

**`CutawayColorAt` resolves it the same way**, at the floor the cut exposes, so a
paved passage reads as paved on every cutting view and not just the paving
tool's — the same argument, and underground it is the one thing telling a
corridor you have finished from one you have not.

**Climbing routes are the second scalar, and they are AUTHORED, not covered.**
`ZoneGenData.climbCoverage` asks "how much of this zone's rock is climbable" and
worldgen answers it with cellular patches; the painter asks "where is the way
up", which is a route-design question with a specific answer. A coverage field
was built first and is the wrong shape for it: a fraction cannot say *this* wall,
and a patchy face is not a route. So the layer is a per-column FLAG.

Routed edges are inked on **every view, not just the climb tool's** — a route is
a fact about the terrain rather than a mode you switch into, and it has to stay
visible while you paint the things that route past it. The lookup is gated on the
step height first, which is already in hand, so the flat majority of edges never
touch the image.

The tool paints over the **elevation view, unchanged** — that is the map the
decision is read from — and a routed wall is drawn in `climbInk` (magenta)
**instead of its height ink**, so the tall edge you clicked is recoloured rather
than covered by a mark floating above it. Only columns that own a wall of at
least `climbRouteMinWallVoxels` (**4** — a 3m wall is not climbable, so a route
on one would promise a way up the player cannot take) take the flag, which is the same set of edges the
outline pass inks: the tool paints exactly what you can see, and dragging across
the flat ground between two cliffs marks neither. The brush is not eased by its
falloff either — a route is a thing or it is not, and its radius is how WIDE the
route is.

The bake runs `WorldFinish.StampClimbSurfaces`, which now takes the per-column
answers it cannot look up in a painted world (a route flag instead of a zone's
coverage, the painted water layer instead of a `HeightMap`) plus its wall minimum
and whether to be patchy — worldgen patchy, the painter not. Everything else is
shared: the exposed-face walk, the run heights, the per-block growth table.
Reimplementing that painter-side is exactly how the waterfall shading became two
copies that drifted. A marked column's whole exposed face is dressed, so a route
is currently a plain vertical column of climbable surface.

**Moss comes off the GROUND layer, not the zone layer.** `TerrainKitData.mossCoverage`
says how much of that material's exposed rock and ground wears the moss overlay,
and the bake answers `WorldFinish`'s per-column question with the column's
surface kit and cave kit — exactly the two coverages the pass wants. So painting
a material brings its moss with it: no second brush, and no moss where nothing
was painted.

It is NOT read off the zone, and that is not an oversight. Worldgen keeps moss
density on `ZoneGenData` (there it is a property of the biome being generated),
and the painter cannot reach that: its zone palette is `ZoneData`, which does not
correspond to `WorldGenData.ZoneGens` at all — 15 painted entries against 5 in
the default world, no index mapping, and the back-reference is ambiguous because
a `ZoneData` can be shared by several placements. `WorldFinish.Options.MossCoverageAt`
is the seam, the same shape `StampClimbSurfaces.coverageAt` already has.

The cost of keying per material is that a kit shared by two zones carries one
number: `marsh_kit` is both swamp (0.4) and swamp_fire (0.15) and takes 0.4, and
`cave_limestone_kit` is nearly every zone's cave and takes 0.5. Split the kit if
that ever matters.

Both scalars are deliberately absent from the preset brush: difficulty does not follow
biome, and neither does where the player is MEANT to be able to climb — both are
route-design decisions, so folding them in would tie together the layers that
most want to vary independently.

**Mobs AND forges read it, through two seams on `SpawnContext`.**
`MobSpawnEntry` asks `ComputeMobLevel` and `ForgeSpawnEntry` asks
`ComputeForgeLevel`; both otherwise read zone bands and noise fields that a
`Generate()` run leaves behind and a painted world never produces. Without the
seams a painted mob spawned at its species base and a painted forge baked at
level 0 — no pips, the mildest upgrade, wherever it stood.

The two stay SEPARATE delegates (`MobLevelOverride`, `ForgeLevelOverride`)
because in a generated world they are deliberately independent noise fields — a
zone's forges and its monsters vary apart. A painted world simply feeds both from
the one difficulty layer it has, which is also the scale a forge is authored
against ("a forge sits at the same tier as monsters in its zone").

`worldmap_check` reports the layer as the rounded tiers those seams hand out
(`danger: L0:… L1:…`), because a document that reads all-zero there bakes flat
and nothing says so until you are standing in it.

The **preset brush writes every per-column layer** — ground, props and mobs — so
the ordinary "this is boreal forest" stroke stays one stroke, and each layer is
still independently repaintable after. Zone stays its own tool: it is chunk
resolution, so a preset stroke narrower than a chunk would flip that chunk's
weather, and one zone covers ground of many kinds anyway. That split — per-column
layers composited by the preset, per-chunk layers painted alone — is what decides
whether a new layer belongs in it.

The HUD carries **two button groups, both built from lists rather than authored
one-per-node**, so adding a tool — or an op to a tool — cannot leave a stale
button behind. The first is one button per `IWorldMapTool`; the second is the
active tool's `Options(ctx)`, labelled with its **1-9** hotkey and rebuilt on
every tool change. `Options` takes the document because zone and region names
come out of its own palettes — a region's authored `displayName`, a zone's
resource file name — so those layers are painted by NAME rather than by index
(empty for tools whose primary parameter is not a small fixed set, like a region
index). Every way of changing either — button, Tab, number key, Q/E — routes
through `SelectTool` / `SelectOption`, so the bars cannot disagree with what the
map is showing.

The brush ring takes its colour from `IWorldMapTool.CursorColor`: while
flattening it is the band colour of the target height, so the cursor answers
"what am I about to paint" against the map it is hovering over. Ops that move a
column relative to where it already is (Raise, Lower, Smooth) have no single
value to show and stay white.

`WorldMapCanvas` (a dumb viewer: places the image at native scale with
middle-drag pan, draws the cursor, reports texel strokes via `OnPaint`, hover via
`OnHover` and wheel notches via `OnAdjustRadius`). Each tool view reads the layer
images directly, so nothing here needs the voxel world.

Keys: LMB paint / RMB erase · **1-9** pick the active tool's option · **Tab** or
the HUD toolbar cycle tool (+view) · **Q/E** step the tool's `Cycle`
parameter — the option index on most tools, and the parameter the option row
cannot show on the ones whose row is empty (the tunnel brush's
height) · **R/F** Flatten target level / tunnel floor · **T/G** or
**alt+wheel** cutaway level (**alt+RMB** aims it at a clicked floor) · **W** show/hide
water · **Ctrl+Z** undo, **Ctrl+Shift+Z** / **Ctrl+Y** redo
· **alt+click** pick a height (alt+drag spreads it) · **shift+drag**
constrain to that one height · **ctrl+drag** constrain to that height and above
· **wheel** or
**`[` `]`** brush size (proportional step) ·
**ctrl+wheel** zoom (cursor-anchored) · **middle-drag** pan
· **Ctrl+S** save layers, then bake the `.hike` in the background
· **Esc** pause menu (save / resume / quit to menu).

**The painter binds EDITOR-ONLY actions, never gameplay ones.**
`InputBindings.Apply` remaps `UseItem` / `Interact` / `InteractCancel` /
`Lantern` / `Dash` / `Sneak` at startup, so an editor bound to one of those gets
a different key — and a key that means something else — the moment that set is
edited. Q/E were bound to `UseItem` / `Interact`; Q became
`Lantern` while `UseItem` had moved onto **Ctrl**: Q did nothing here, and every
Ctrl press (the one held for Ctrl+Z and Ctrl+S included) cycled the tool's
parameter. They are `EditorParamLeft` / `EditorParamRight` now, alongside
`EditorUp` / `EditorDown` and `EditorClipUp` / `EditorClipDown` — actions
nothing remaps, which is what makes them rebindable in the input map without
touching this file.
