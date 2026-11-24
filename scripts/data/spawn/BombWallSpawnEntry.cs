using System;
using Godot;

// A patch of rock that can be smashed open for good. The voxels are whatever the
// world already has there: the entry only MARKS them — it dresses their open
// faces in `crackSurface` while standing and clears them to Air once broken. So a
// wall is authored by building the rock with the ordinary tools and then picking
// which cells give way; nothing has to remember what they were made of, because
// breaking is permanent.
//
// `cells` is per placement, set on the placement's fork (the scene editor's and
// the painter's bomb-wall tools write it). Offsets are in the PLACEMENT's frame,
// from the cell `position` falls in, and turn with its facing — a stamped scene
// rotates a placement's facing but never its entry.
//
// How hard it is to break is the SCENE's (BombWall): health, regen and what it is
// weak to are what kind of wall it is, like a Destructible's.
[GlobalClass]
public partial class BombWallSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    // Worn on every open face of the wall's cells while it stands. An overlay
    // surface (BlockCatalog.overlaySurfaces), so its id is its atlas layer.
    [Export] public BlockSurfaceData crackSurface;
    [Export] public Godot.Collections.Array<Vector3I> cells = new();

    public override PackedScene PaletteScene => scene;

    private static readonly EEntityEvent[] WallEvents = { EEntityEvent.Break };

    public override EEntityEvent[] HookEvents => WallEvents;

    // The cells are drawn with the bomb-wall tools, never typed into a panel.
    public override bool ShowsProperty(StringName name)
        => name != PropertyName.cells && base.ShowsProperty(name);

    public override string MissingPlacementValue()
        => cells == null || cells.Count == 0 ? "no cells picked for the wall" : null;

    public override string InvalidPlacementValue()
    {
        if (scene == null)
        {
            return "no scene.";
        }
        if (crackSurface == null || crackSurface.atlasBaseIndex <= 0)
        {
            return "crackSurface is unset or not in the atlas.";
        }
        return null;
    }

    // The world cells a placement at `position` facing `facingY` covers.
    public Vector3I[] WorldCells(Vector3 position, float facingY)
    {
        var anchor = new Vector3I(Mathf.FloorToInt(position.X), Mathf.FloorToInt(position.Y), Mathf.FloorToInt(position.Z));
        int turns = QuarterTurns(facingY);
        int count = cells?.Count ?? 0;
        var result = new Vector3I[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = anchor + RotateOffset(cells[i], turns);
        }
        return result;
    }

    // The placement-frame offset that lands on world `cell` — what an authoring
    // tool writes when the author points at a voxel.
    public static Vector3I LocalOffset(Vector3 position, float facingY, Vector3I cell)
    {
        var anchor = new Vector3I(Mathf.FloorToInt(position.X), Mathf.FloorToInt(position.Y), Mathf.FloorToInt(position.Z));
        return RotateOffset(cell - anchor, (4 - QuarterTurns(facingY)) % 4);
    }

    // A facing snapped to whole quarter turns, 0..3: cells are a voxel grid, so a
    // wall can only turn the way the grid does.
    public static int QuarterTurns(float facingY)
    {
        return ((Mathf.RoundToInt(facingY / (Mathf.Pi * 0.5f)) % 4) + 4) % 4;
    }

    // Yaw by quarter turns about +Y, matching SubsceneRotator: (dx, dz) -> (dz, -dx).
    private static Vector3I RotateOffset(Vector3I offset, int turns)
    {
        for (int i = 0; i < turns; i++)
        {
            offset = new Vector3I(offset.Z, offset.Y, -offset.X);
        }
        return offset;
    }

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (!CheckPlaceable(position, context, out bool bare) || bare)
        {
            return;
        }
        float facing = FacingY(context);
        ws.AddEntity(new BombWallSimState(position, facing, scene, crackSurface, WorldCells(position, facing)));
    }
}
