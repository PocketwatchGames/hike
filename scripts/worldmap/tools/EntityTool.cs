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

    // How near a click has to be, in metres, to grab an entity rather than place
    // a new one. Generous, because a 1m dot is hard to hit and placing an unwanted
    // second chest on top of the first is worse than grabbing the first.
    private const int GrabRadius = 2;

    // How far the cursor must be from the anchor before an aiming drag adopts an
    // angle, in metres. An angle taken from a cursor sitting on top of the mark
    // is noise — a metre of hand jitter is a 180-degree swing — so a click that
    // does not really drag leaves the facing where it was.
    private const int AimDeadZone = 2;

    private EntityPlacement _pressHit;
    private bool _pressWasSpawn;
    private Vector2I _grabOffset;
    private bool _strokeActed;
    // This stroke turns the selection instead of moving it. Decided at the press
    // like every other modifier, or set by the placement itself so a new entity
    // is aimed by the same drag that dropped it.
    private bool _aiming;
    private Rect2I? _dirty;

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
            // The palette ENTRY, not the variant: this row is "what am I
            // placing", and one goblin row is the whole point of an entry that
            // offers variants. Which goblin is a per-placement choice and shows
            // on the hover readout and in the property panel, where it is the
            // answer being asked for.
            names[i + 1] = palette[i] == null
                ? $"Entry {i}" : SpawnEntryData.PaletteName(palette[i]);
        }
        return names;
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
            + "Shift+drag to aim, R/F to turn 45 deg, RMB to delete";

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
    public EntityPlacement EntityUnder(WorldMapState ctx, Vector2I texel)
        => ctx.EntityAt(texel.X, texel.Y, GrabRadius);

    // A facing is reported only where it does something. An entry that never
    // reads it says so, rather than showing a number that changes nothing.
    public string LevelText(WorldMapState ctx, WorldMapView view)
    {
        if (Selected == null)
        {
            return "";
        }
        return Aimable(Selected)
            ? $"Selected facing {(int)Selected.facing * 45} deg"
            : "Selected (this entry has no facing)";
    }

    // Can this placement be aimed at all? The entry answers, because whether a
    // facing reaches the spawned entity is the entry type's business.
    private static bool Aimable(EntityPlacement placement)
        => placement?.Entry != null && placement.Entry.UsesFacing;

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
        _pressHit = ctx.EntityAt(texel.X, texel.Y, GrabRadius);
        _pressWasSpawn = _pressHit == null && ctx.IsSpawnNear(texel.X, texel.Y, GrabRadius);
        _strokeActed = false;
        _dirty = null;
        // Shift: this drag aims what it grabbed instead of sliding it.
        _aiming = (mods & EStrokeMods.Constrain) != 0;
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Per stamp, not per stroke: a rect that accumulated over a whole drag
        // would repaint the entire path travelled on every motion event.
        _dirty = null;
        if (erase)
        {
            // Only what was under the press, and only once — a right-drag across
            // a village must not sweep it away. The spawn point cannot be
            // deleted, only moved: a world without one is a world you cannot
            // enter, and the bake would silently fall back to the origin.
            if (!_strokeActed && _pressHit != null)
            {
                _strokeActed = true;
                _dirty = CellRect(ctx, _pressHit.anchorXZ);
                if (Selected == _pressHit)
                {
                    Selected = null;
                }
                ctx.RemoveEntity(_pressHit);
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
                if (entry == null)
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
                // looks. An entry with no facing keeps the old meaning and
                // slides, which is the only thing a drag can still do for it.
                _aiming = Aimable(Selected);
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

        // Where it came from as well as where it went: the mark it left behind
        // needs repainting as much as the one it arrived at.
        Rect2I before = CellRect(ctx, Selected.anchorXZ);
        Selected.anchorXZ = ctx.WorldXZ(texel) + _grabOffset;
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
        if (!Aimable(Selected))
        {
            return;
        }
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

    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = ctx.EntityPalette.Length + 1;
        PaletteIndex = ((PaletteIndex + dir) % n + n) % n;
    }

    // R/F turn the SELECTED entity rather than stepping a tool parameter, the
    // way they turn the selected stamp in the scene tool. One eighth turn a
    // press, matching what an aiming drag can express, so the two agree about
    // what the authorable facings are.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        if (!Aimable(Selected))
        {
            return;
        }
        Selected.facing = (EEntityFacing)(((int)Selected.facing + dir) & 7);
    }
}
