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
        View = new EntityView(this);
    }

    private bool SpawnSelected => PaletteIndex == 0;

    public string[] Options(WorldMapState ctx)
    {
        SpawnEntryData[] palette = ctx.EntityPalette;
        var names = new string[palette.Length + 1];
        names[0] = "Player spawn";
        for (int i = 0; i < palette.Length; i++)
        {
            names[i + 1] = palette[i]?.ResourcePath.GetFile().GetBaseName() ?? $"Entry {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex
    {
        get => PaletteIndex;
        set => PaletteIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapState ctx)
        => SpawnSelected ? ctx.Data.spawnInk : ctx.Data.entityInk;

    public string HintText(WorldMapState ctx)
        => "Click to place, drag to move, R/F to turn, RMB to delete";

    public string StatusText(WorldMapState ctx)
    {
        if (SpawnSelected)
        {
            return ctx.Placements.hasSpawn
                ? $"Player spawn at {ctx.Placements.spawnXZ.X}, {ctx.Placements.spawnXZ.Y}"
                : "Player spawn (not placed — world origin)";
        }
        SpawnEntryData[] palette = ctx.EntityPalette;
        int i = PaletteIndex - 1;
        return i >= 0 && i < palette.Length && palette[i] != null
            ? palette[i].ResourcePath.GetFile().GetBaseName()
            : "No entity palette authored";
    }

    public string LevelText(WorldMapState ctx)
        => Selected == null ? "" : $"Selected {(int)Selected.rotation * 90} deg";

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;

    // The press decides what the stroke is ABOUT and nothing else — it must not
    // place, because the right button fires it too and a right-click on bare
    // ground would drop an entity for the erase that follows to delete again.
    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
        _pressHit = ctx.EntityAt(texel.X, texel.Y, GrabRadius);
        _pressWasSpawn = _pressHit == null && ctx.IsSpawnNear(texel.X, texel.Y, GrabRadius);
        _strokeActed = false;
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
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
                SpawnEntryData[] palette = ctx.EntityPalette;
                int i = PaletteIndex - 1;
                if (i < 0 || i >= palette.Length || palette[i] == null)
                {
                    return;
                }
                Selected = new EntityPlacement
                {
                    entry = palette[i],
                    anchorXZ = ctx.WorldXZ(texel),
                };
                _grabOffset = Vector2I.Zero;
                ctx.AddEntity(Selected);
                return;
            }
        }

        if (Selected != null)
        {
            Selected.anchorXZ = ctx.WorldXZ(texel) + _grabOffset;
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

// The ground map with a dot per placed entity and one for the spawn. Ground
// rather than elevation: what matters when placing a chest is what is around it —
// the road it sits beside, the props already there — and the terrain shape still
// reads through the step outlines.
public class EntityView : IWorldMapView
{
    private readonly EntityTool _tool;

    public EntityView(EntityTool tool)
    {
        _tool = tool;
    }

    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props;

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        if (ctx.IsSpawnAt(px, pz))
        {
            return ctx.Data.spawnInk;
        }
        EntityPlacement hit = ctx.EntityAt(px, pz, 0);
        if (hit == null)
        {
            return ctx.GroundColorAt(px, pz);
        }
        Color ink = ctx.Data.entityInk;
        return hit == _tool.Selected ? new Color(1f, 1f, 1f) : ink;
    }
}
