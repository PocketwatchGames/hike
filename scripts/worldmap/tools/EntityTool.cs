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

    private EntityPlacement _pressHit;
    private bool _pressWasSpawn;
    private Vector2I _grabOffset;
    private bool _strokeActed;

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

    public int OptionIndex
    {
        get => PaletteIndex;
        set => PaletteIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapInk ink)
        => SpawnSelected ? ink.Data.spawnInk : ink.Data.entityInk;

    public string HintText(WorldMapState ctx)
        => "Hover to name  |  Click to place, drag to move, R/F to turn, RMB to delete";

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

    public string LevelText(WorldMapState ctx, WorldMapView view)
        => Selected == null ? "" : $"Selected {(int)Selected.rotation * 90} deg";

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;

    // The press decides what the stroke is ABOUT and nothing else — it must not
    // place, because the right button fires it too and a right-click on bare
    // ground would drop an entity for the erase that follows to delete again.
    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        _pressHit = ctx.EntityAt(texel.X, texel.Y, GrabRadius);
        _pressWasSpawn = _pressHit == null && ctx.IsSpawnNear(texel.X, texel.Y, GrabRadius);
        _strokeActed = false;
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        if (erase)
        {
            // Only what was under the press, and only once — a right-drag across
            // a village must not sweep it away. The spawn point cannot be
            // deleted, only moved: a world without one is a world you cannot
            // enter, and the bake would silently fall back to the origin.
            if (!_strokeActed && _pressHit != null)
            {
                _strokeActed = true;
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
                return;
            }
        }

        if (Selected != null)
        {
            Selected.anchorXZ = ctx.WorldXZ(texel) + _grabOffset;
            // Re-seated as it slides, so dragging one along a passage keeps it on
            // that passage's floor and dragging it out of the mouth puts it back
            // on the ground.
            Vector2I at = ctx.TexelXZ(Selected.anchorXZ);
            Selected.floorY = ctx.FloorForEntity(at.X, at.Y, view.CutawayY);
        }
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = ctx.EntityPalette.Length + 1;
        PaletteIndex = ((PaletteIndex + dir) % n + n) % n;
    }

    // R/F turn the SELECTED entity rather than stepping a tool parameter, the
    // way they turn the selected stamp in the scene tool.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        if (Selected == null)
        {
            return;
        }
        Selected.rotation = (ESubsceneRotation)(((int)Selected.rotation + dir) & 3);
    }
}
