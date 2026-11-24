using Godot;

// Places individual entities — a chest here, this NPC there — and the player
// spawn point.
//
// Deliberately the same interaction the scene tool has: click empty ground to
// drop the selected palette entry, click one to select it, drag to move it, R/F
// to turn it, RMB to delete it. That was the plan when the scene tool was built
// ("an interactive swaps in a prop list and a 1x1 footprint"), and it holds: the
// only differences here are the palette and the hit test, which is a proximity
// check because an entity is a point rather than a footprint.
//
// Aiming is the one gesture that is NOT the scene tool's, because an entity has
// a facing worth pointing at something and a stamp has a footprint that can only
// turn in quarter steps. It is a drag rather than a keypress: the direction you
// want is a place on the map, and dragging out from a mark says it in one
// gesture where R/F is up to eight presses and a readout to count them on. So
// dropping a new entity leaves the stroke AIMING it — the natural follow-through
// of the click that placed it — and shift+drag aims one already on the map.
//
// The entities a STAMPED SCENE brings are on the map too, in their own ink, and
// a click selects one into the same panel — but they stay where the scene put
// them: no drag, no aim, no delete. Editing one forks its entry for that stamp
// alone (SceneEntityTarget), which is how one house's door gets its own lock.
//
// EVERY placement can be aimed. A facing reaches whatever the entry files (see
// SpawnEntryData.FacingY), so there is no palette entry the gesture does nothing
// for and nothing here has to ask whether this one is turnable.
//
// A BOMB WALL is a volume rather than a point: it is drawn as its box, and a
// click anywhere in that box selects it into the panel (RMB deletes it), but its
// cells are edited only in the bomb-wall tool — so this tool neither moves nor
// turns one, nor drops a new one as a point with no cells.
//
// The PLAYER SPAWN is the first palette entry rather than a tool of its own.
// There is exactly one of it, so placing it MOVES it — a tool whose whole job is
// to move a single point does not need a button in the toolbar, and having it
// here means it is placed against the same map, with the same cursor, as
// everything else that stands on the ground.
public class EntityTool : IWorldMapTool
{
    public string Name => "Entities";
    public IWorldMapView View { get; }

    // A pointer, not a brush: entities are placed one at a time.
    public float Radius { get; set; } = 1f;

    public int PaletteIndex = 0;
    public EntityPlacement Selected;

    public EntityPlacement SelectedEntity => Selected;

    // A stamped scene's entity, selected for the panel. Mutually exclusive with
    // Selected.
    public SceneEntityTarget SelectedScene;

    public SceneEntityTarget SelectedSceneEntity => SelectedScene;

    // How near a click has to be, in metres, to grab an entity rather than place
    // a new one: 0 is the entity's own one-metre cell and nothing around it, so
    // a new entity can be dropped in the very next cell.
    private const int GrabRadius = 0;

    // How far the cursor must be from the anchor before an aiming drag adopts an
    // angle, in metres. An angle taken from a cursor sitting on top of the mark
    // is noise — a metre of hand jitter is a 180-degree swing — so a click that
    // does not really drag leaves the facing where it was.
    private const int AimDeadZone = 2;

    private EntityPlacement _pressHit;
    private SceneEntityTarget _pressScene;
    private bool _pressWasSpawn;
    // The press landed on a bomb wall's box.
    private bool _pressIsWall;
    private Vector2I _grabOffset;
    private bool _strokeActed;
    // This stroke turns the selection instead of moving it. Decided at the press
    // like every other modifier, or set by the placement itself so a new entity
    // is aimed by the same drag that dropped it.
    private bool _aiming;
    private Rect2I? _dirty;

    // Clicking again on a stack of overlapping entities steps the selection
    // through them. The press still grabs the CURRENT selection, so a drag moves
    // the one you have; only a click released without moving advances, to
    // _cycleNext (the entity after it in the stack).
    private object _cycleNext;
    private Vector2I _pressTexel;
    private bool _pressMoved;
    private readonly System.Collections.Generic.List<EntityPlacement> _stackEntities = new();
    private readonly System.Collections.Generic.List<SceneEntityTarget> _stackScene = new();
    private readonly System.Collections.Generic.List<EntityPlacement> _stackWalls = new();
    private readonly System.Collections.Generic.List<object> _stack = new();

    public EntityTool()
    {
        View = new CutawayGroundView();
    }

    private bool SpawnSelected => PaletteIndex == 0;

    public string[] Options(WorldMapState ctx)
    {
        SpawnEntryData[] palette = ctx.EntityPalette;
        var names = new string[palette.Length + 1];
        names[0] = "Player spawn";
        for (int i = 0; i < palette.Length; i++)
        {
            names[i + 1] = palette[i] == null
                ? $"Entry {i}" : SpawnEntryData.PaletteName(palette[i]).Capitalize();
        }
        return names;
    }

    // Each entry under the folder it sits in; the player spawn leads, ungrouped.
    public string[] OptionSections(WorldMapState ctx)
    {
        SpawnEntryData[] palette = ctx.EntityPalette;
        AuthoringPaletteSource source = AuthoringPaletteSource.Find(AuthoringPaletteSource.Entities);
        var sections = new string[palette.Length + 1];
        sections[0] = "";
        for (int i = 0; i < palette.Length; i++)
        {
            sections[i + 1] = palette[i] == null || source == null ? "" : source.SectionFor(palette[i].ResourcePath);
        }
        return sections;
    }

    public Color[] OptionColors(WorldMapInk ink) => null;

    // No 1-9: spawn entries are a directory, so the first nine rows are an arbitrary prefix that
    // moves whenever one is added.
    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => PaletteIndex;
        set => PaletteIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapInk ink)
        => SpawnSelected ? ink.Data.spawnInk : ink.Data.entityInk;

    public string HintText(WorldMapState ctx)
        => "Hover to name  |  Click to place then drag to aim, drag to move, "
            + "Shift+drag to aim, R/F to turn 45 deg, RMB to delete  |  "
            + "Click again on overlapping entities to cycle through them  |  "
            + "A scene's entities: click to edit for that stamp (fixed in place)  |  "
            + "Bomb walls: click to edit, drawn with the Bomb walls tool";

    public string StatusText(WorldMapState ctx, WorldMapView view)
    {
        if (SpawnSelected)
        {
            return ctx.Placements.hasSpawn
                ? $"Player spawn at {ctx.Placements.spawnXZ.X}, {ctx.Placements.spawnXZ.Y}"
                : "Player spawn (not placed — world origin)";
        }
        SpawnEntryData entry = SelectedEntry(ctx);
        return entry != null ? SpawnEntryData.PaletteName(entry) : "No entity palette authored";
    }

    // What the next click would place, and so what the map picks out: every
    // placement of this entry is inked as a match. Null while the spawn point is
    // the selection — there is one of those and it has its own ink.
    public SpawnEntryData SelectedEntry(WorldMapState ctx)
    {
        SpawnEntryData[] palette = ctx.EntityPalette;
        int i = PaletteIndex - 1;
        return i >= 0 && i < palette.Length ? palette[i] : null;
    }

    // The same proximity test the press grabs with, so what the cursor grows and
    // the HUD names is exactly what a click would pick up.
    // A bomb wall's box is the last thing hit: it is big, and a point mark or a
    // scene entity standing inside it must stay grabbable.
    public EntityPlacement EntityUnder(WorldMapState ctx, WorldMapView view, Vector2I texel)
    {
        int clip = Clip(view);
        return ctx.EntityAt(texel.X, texel.Y, GrabRadius, clip)
            ?? (ctx.SceneEntityAt(texel.X, texel.Y, clip) == null ? ctx.BombWallAt(texel.X, texel.Y, clip) : null);
    }

    public SceneEntityTarget SceneEntityUnder(WorldMapState ctx, WorldMapView view, Vector2I texel)
        => EntityUnder(ctx, view, texel) == null ? ctx.SceneEntityAt(texel.X, texel.Y, Clip(view)) : null;

    // The plane this tool's map is cut at — the one the marks are drawn against,
    // so a click grabs only what is on screen.
    private int Clip(WorldMapView view) => view.ClipFor(View.CutsAway);

    public string LevelText(WorldMapState ctx, WorldMapView view)
    {
        if (SelectedScene != null)
        {
            return "Scene entity: placed by its stamp, edits apply to this stamp only";
        }
        if (WorldMapState.BombWallOf(Selected) != null)
        {
            return "Bomb wall: reshape it with the Bomb walls tool";
        }
        return Selected == null ? "" : $"Selected facing {(int)Selected.facing * 45} deg";
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;

    // The mark this stroke changed — placed, moved, aimed or deleted. The host
    // grows it by the reach of a mark before repainting, so an aim (which moves
    // nothing, and whose cursor is metres away from the mark it is turning)
    // repaints the mark rather than the ground under the cursor.
    public Rect2I? LastPaintRect => _dirty;

    // The press decides what the stroke is ABOUT and nothing else — it must not
    // place, because the right button fires it too and a right-click on bare
    // ground would drop an entity for the erase that follows to delete again.
    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        int clip = Clip(view);
        _pressHit = ctx.EntityAt(texel.X, texel.Y, GrabRadius, clip);
        _pressScene = _pressHit == null ? ctx.SceneEntityAt(texel.X, texel.Y, clip) : null;
        _pressWasSpawn = _pressHit == null && _pressScene == null && ctx.IsSpawnNear(texel.X, texel.Y, GrabRadius, clip);
        if (_pressHit == null && _pressScene == null && !_pressWasSpawn)
        {
            _pressHit = ctx.BombWallAt(texel.X, texel.Y, clip);
        }
        // Shift: this drag aims what it grabbed instead of sliding it.
        _aiming = (mods & EStrokeMods.Constrain) != 0;
        _pressTexel = texel;
        _pressMoved = false;
        _cycleNext = null;
        if (!_aiming && !_pressWasSpawn && (mods & EStrokeMods.Pick) == 0)
        {
            GrabFromStack(ctx, texel, clip);
        }
        _pressIsWall = WorldMapState.BombWallOf(_pressHit) != null;
        _strokeActed = false;
        _dirty = null;
    }

    // Everything under the press, in the order a single press prefers it: hand
    // placements, then a scene's entities, then bomb walls. Where the current
    // selection is in the stack, the press is about IT (so LMB drags it and RMB
    // deletes it), and the entity after it is what a plain click advances to.
    private void GrabFromStack(WorldMapState ctx, Vector2I texel, int clip)
    {
        _stackEntities.Clear();
        _stackScene.Clear();
        _stackWalls.Clear();
        _stack.Clear();
        ctx.EntitiesAt(texel.X, texel.Y, GrabRadius, clip, _stackEntities);
        ctx.SceneEntitiesAt(texel.X, texel.Y, clip, _stackScene);
        ctx.BombWallsAt(texel.X, texel.Y, clip, _stackWalls);
        _stack.AddRange(_stackEntities);
        _stack.AddRange(_stackScene);
        _stack.AddRange(_stackWalls);
        if (_stack.Count < 2)
        {
            return;
        }
        object current = (object)Selected ?? SelectedScene;
        int at = current != null ? _stack.IndexOf(current) : -1;
        if (at < 0)
        {
            return;
        }
        _pressHit = current as EntityPlacement;
        _pressScene = current as SceneEntityTarget;
        _cycleNext = _stack[(at + 1) % _stack.Count];
    }

    public void EndStroke(WorldMapState ctx, WorldMapView view)
    {
        if (_cycleNext == null || _pressMoved)
        {
            return;
        }
        Selected = _cycleNext as EntityPlacement;
        SelectedScene = _cycleNext as SceneEntityTarget;
        _cycleNext = null;
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Per stamp, not per stroke: a rect that accumulated over a whole drag
        // would repaint the entire path travelled on every motion event.
        _dirty = null;
        if (texel != _pressTexel)
        {
            _pressMoved = true;
        }
        // An erase acts at once, so there is no click left to advance.
        if (erase)
        {
            _cycleNext = null;
        }

        // A scene's entity is selected and nothing else: the scene decides where
        // it stands, so a drag cannot move or aim it and RMB cannot delete it.
        if (_pressScene != null)
        {
            if (!_strokeActed && !erase)
            {
                _strokeActed = true;
                Selected = null;
                SelectedScene = _pressScene;
            }
            return;
        }

        if (erase)
        {
            // Only what was under the press, and only once — a right-drag across
            // a village must not sweep it away. The spawn point cannot be
            // deleted, only moved: a world without one is a world you cannot
            // enter, and the bake would silently fall back to the origin.
            if (!_strokeActed && _pressHit != null)
            {
                _strokeActed = true;
                _dirty = _pressIsWall ? ctx.BombWallTexelRect(_pressHit).Grow(1) : CellRect(ctx, _pressHit.anchorXZ);
                if (Selected == _pressHit)
                {
                    Selected = null;
                }
                ctx.RemoveEntity(_pressHit);
            }
            return;
        }

        // A wall is selected and nothing else; its box is the bomb-wall tool's.
        if (_pressIsWall)
        {
            if (!_strokeActed)
            {
                _strokeActed = true;
                SelectedScene = null;
                Selected = _pressHit;
            }
            return;
        }

        if (_pressWasSpawn || (!_strokeActed && SpawnSelected && _pressHit == null))
        {
            // Dragging the spawn, or placing it for the first time.
            _strokeActed = true;
            _pressWasSpawn = true;
            _dirty = ctx.Placements.hasSpawn
                ? CellRect(ctx, ctx.Placements.spawnXZ).Merge(CellRect(ctx, ctx.WorldXZ(texel)))
                : CellRect(ctx, ctx.WorldXZ(texel));
            ctx.SetSpawn(ctx.WorldXZ(texel));
            return;
        }

        if (!_strokeActed)
        {
            _strokeActed = true;
            SelectedScene = null;
            if (_pressHit != null)
            {
                // Grab where you clicked, so a drag slides it instead of
                // snapping its anchor to the cursor.
                Selected = _pressHit;
                _grabOffset = Selected.anchorXZ - ctx.WorldXZ(texel);
            }
            else
            {
                SpawnEntryData entry = SelectedEntry(ctx);
                // A wall dropped as a point has no cells and bakes nothing.
                if (entry == null || entry is BombWallSpawnEntry)
                {
                    return;
                }
                Selected = new EntityPlacement
                {
                    source = entry,
                    anchorXZ = ctx.WorldXZ(texel),
                    floorY = ctx.FloorForEntity(texel.X, texel.Y, view.CutawayY),
                };
                _grabOffset = Vector2I.Zero;
                ctx.AddEntity(Selected);
                _dirty = CellRect(ctx, Selected.anchorXZ);
                // The click that drops one leaves the stroke aiming it: the
                // entity is already where it was clicked, so there is nothing
                // left for the rest of the drag to say except which way it
                // looks.
                _aiming = true;
                return;
            }
        }

        if (Selected == null)
        {
            return;
        }

        if (_aiming)
        {
            Aim(ctx, texel);
            return;
        }

        // A click that only selects, or a motion event that stays in the same
        // cell, must not re-seat: with the plane above a passage the seat
        // resolves to the surface, so merely selecting a tunnel entity would
        // lift it out.
        Vector2I anchor = ctx.WorldXZ(texel) + _grabOffset;
        if (anchor == Selected.anchorXZ)
        {
            return;
        }

        // Where it came from as well as where it went: the mark it left behind
        // needs repainting as much as the one it arrived at.
        Rect2I before = CellRect(ctx, Selected.anchorXZ);
        Selected.anchorXZ = anchor;
        // Re-seated as it slides, so dragging one along a passage keeps it on
        // that passage's floor and dragging it out of the mouth puts it back
        // on the ground.
        Vector2I at = ctx.TexelXZ(Selected.anchorXZ);
        Selected.floorY = ctx.FloorForEntity(at.X, at.Y, view.CutawayY);
        _dirty = before.Merge(CellRect(ctx, Selected.anchorXZ));
    }

    // Turn the selection to face the cursor, snapped to the 45-degree steps a
    // facing is authored in.
    private void Aim(WorldMapState ctx, Vector2I texel)
    {
        Vector2I d = ctx.WorldXZ(texel) - Selected.anchorXZ;
        if (d.LengthSquared() < AimDeadZone * AimDeadZone)
        {
            return;
        }
        Selected.facing = EntityPlacement.Nearest(new Vector2(d.X, d.Y));
        _dirty = CellRect(ctx, Selected.anchorXZ);
    }

    // The one map cell a placement's anchor sits in. What its MARK covers is the
    // host's answer, not the tool's — the growth and the facing line are ink.
    private static Rect2I CellRect(WorldMapState ctx, Vector2I worldXZ)
        => new Rect2I(ctx.TexelXZ(worldXZ), Vector2I.One);

    // R/F turn the SELECTED entity rather than stepping a tool parameter, the
    // way they turn the selected stamp in the scene tool. One eighth turn a
    // press, matching what an aiming drag can express, so the two agree about
    // what the authorable facings are.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        if (Selected == null || WorldMapState.BombWallOf(Selected) != null)
        {
            return;
        }
        Selected.facing = (EEntityFacing)(((int)Selected.facing + dir) & 7);
    }
}
