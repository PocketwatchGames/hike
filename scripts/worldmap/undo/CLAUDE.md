# World map undo / redo (`scripts/worldmap/undo/`)

## Undo / redo (`scripts/worldmap/undo/`)


**Snapshot-on-touch, the same contract as the world editor's** (`IMapEditAspect`
mirrors `IEditorEditAspect`) — a separate hierarchy only because none of the
state overlaps: that one moves voxels and entities in a `WorldState`, this one
moves pixels in layer images. Ctrl+Z undoes, Ctrl+Shift+Z or Ctrl+Y redoes, and
a whole drag is one edit (opened on press, committed on release).

**The HOST touches, not the tool.** `WorldMapPainter` opens an edit on every
press and declares the brush rect before each `Paint` call, so a tool is
undoable the moment it is added to the list and there is nothing a tool can
forget to declare. The price is that every layer over that region is
snapshotted, since the host does not know which one the tool writes — and it is
paid back at commit, where each aspect drops the tiles that did not change, so a
stroke that moved one layer keeps one layer.

That also makes it safe to open an edit on presses that paint nothing (an
alt+click pick, a click on empty ground): an edit that captured no change is
discarded instead of costing an undo slot.

Three aspects, split by what a snapshot naturally costs:

- **`RasterTilesAspect`** — the layer images, one chunk-square tile at a time.
  Tiled because a stroke is local: a brush touching a few tiles should cost
  kilobytes, not a copy of every layer in the document.
- **`TunnelTilesAspect`** — the carve mask, as whole-height columns over a tile.
  Separate because it is the one 3D layer and the one where a whole-layer copy
  would really hurt (~6MB on an 18x16 map). Whole-height because the tunnel tool
  can move its level mid-stroke, and taking the column entire costs one
  snapshot instead of per-slice bookkeeping.
- **`PlacementsAspect`** — everything in `WorldMapPlacements` (stamps, entities,
  spawn), snapshotted whole: a handful of entries, and add / delete / move /
  rotate have no useful spatial extent. It restores VALUES into the existing
  instances rather than replacing them with copies, which is what lets a tool's
  selection survive an undo of the drag that moved it.

  **Which values it captures comes from the resource, not from a list in the
  aspect.** It asks each placement for its script-declared properties
  (`ScriptVariable` usage, so engine bookkeeping like `resource_path` is left
  alone) and captures all of them, so a property added to `SubscenePlacement` is
  undoable the day it is added. The hand-written list it replaced named anchor /
  rotation / path only, which left `yOffset` — written by the scene tool's
  alt+click — outside undo entirely, and nothing failed to say so: a field
  missing from a snapshot does not error, it just stops being undoable. Values
  compare as TEXT because `Variant` does not compare by value here (measured:
  two Variants holding the same `Vector2I` are not `Equal`, which made every
  press register as a change and cost an undo slot).

  The same capture reaches one level DEEPER for entities, into a
  placement-OWNED entry — otherwise editing a signpost's text would be outside
  undo, since `entry` is captured as a reference and the reference does not move
  when a field inside it does. An entry still pointing at its palette file is
  skipped: it is shared with every other placement using it and is not ours to
  restore, and there the reference IS the change (the fork replaces it).

R/F are bracketed like a stroke, because for the scene tool they turn the
selected stamp — document state — while for every other tool they move a tool
parameter and the edit drops itself.
