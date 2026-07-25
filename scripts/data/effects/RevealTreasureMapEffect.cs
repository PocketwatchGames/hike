using Godot;

// Reveals a treasure map when the item is picked up: rolls a dig location a
// moderate distance from where the map was found (context.worldPosition), lands
// it on real terrain, plants an independent buried object there (a BuriedSpot,
// dug up through the normal shovel path), and records a TreasureMapState that
// shows as a switchable tab on the world-map screen. The map only points at the
// treasure — the buried object owns its payload and persists on its own; the map
// removes itself when that object is unearthed (Sim.TryDig). Author into a
// ConsumableData's effects list.
[GlobalClass]
public partial class RevealTreasureMapEffect : ItemEffect
{
    // The independent buried object planted at the marked spot — a
    // BuriedSpotSpawnEntry (shared buried_spot.tscn + a blind BuriedSpotData
    // whose payload is the treasure, a carrot for now).
    [Export] public SpawnEntryData buriedTreasure;

    // The dig spot is rolled at a random heading this far from the find location
    // so the player must read the zoomed map's terrain and travel to reach it.
    [Export(PropertyHint.Range, "10,400,1")] public float minDistanceMeters = 40f;
    [Export(PropertyHint.Range, "10,400,1")] public float maxDistanceMeters = 120f;

    // Optional one-shot fx spawned on the player as the map is revealed.
    [Export] public PackedScene revealEffect;

    // Candidate spots off charted land are re-rolled up to this many times before
    // falling back to the first candidate.
    const int MaxRollAttempts = 24;

    public override void Apply(IActionActor actor, in ActionContext context)
    {
        if (actor is not Player player)
        {
            return;
        }
        Sim sim = player.Sim;
        WorldState ws = sim?.WorldState;
        SimState simState = ws?.SimState;
        Minimap minimap = sim?.Minimap;
        if (ws == null || simState == null || minimap == null || buriedTreasure == null)
        {
            return;
        }

        Vector3 found = context.worldPosition;
        Vector3 digLocation = found;
        Vector3 firstCandidate = found;
        bool haveCandidate = false;
        bool placed = false;
        for (int i = 0; i < MaxRollAttempts; i++)
        {
            float angle = (float)GD.RandRange(0.0, Mathf.Tau);
            float dist = (float)GD.RandRange(minDistanceMeters, maxDistanceMeters);
            int wx = Mathf.RoundToInt(found.X + Mathf.Cos(angle) * dist);
            int wz = Mathf.RoundToInt(found.Z + Mathf.Sin(angle) * dist);
            // The 2m heightmap gives a start Y even for columns whose chunk isn't
            // resident (0 = off-map / uncharted → re-roll).
            ushort hint = minimap.SurfaceHeightAt(wx, wz);
            if (hint == 0)
            {
                continue;
            }
            if (!haveCandidate)
            {
                firstCandidate = new Vector3(wx, hint, wz);
                haveCandidate = true;
            }
            // Refine to the exact surface top-face and reject water — the buried
            // spot is placed at WorldPosition (no ground snap) and the dig match
            // is a 3D radius, so a coarse Y would put the treasure out of reach.
            if (!TryResolveLandSurface(ws, wx, wz, hint, out float surfaceY))
            {
                continue;
            }
            digLocation = new Vector3(wx, surfaceY, wz);
            placed = true;
            break;
        }
        if (!placed && haveCandidate)
        {
            digLocation = firstCandidate;
        }

        // The buried object exists in the world independently of the map.
        sim.SpawnEntryImmediate(buriedTreasure, digLocation, player);

        float rotation = (float)GD.RandRange(0.0, Mathf.Tau);
        simState.AddTreasureMap(new TreasureMapState(digLocation, rotation));

        if (revealEffect != null)
        {
            ItemEventHandlers.SpawnOnActor(actor, revealEffect);
        }
    }

    // Find the exact surface top-face Y at (wx, wz) by marching the voxel column
    // down from just above the heightmap hint. Returns false when the top of the
    // column is water (treasure shouldn't hide underwater) or the column isn't
    // resident (all air) so the caller re-rolls.
    static bool TryResolveLandSurface(WorldState ws, int wx, int wz, int hintTopY, out float surfaceY)
    {
        surfaceY = hintTopY;
        for (int y = hintTopY + 3; y >= hintTopY - 6; y--)
        {
            VoxelType v = ws.GetVoxelWorld(wx, y, wz);
            if (v == VoxelType.Air)
            {
                continue;
            }
            if (v == VoxelType.Water)
            {
                return false;
            }
            // Top face sits one cell above the topmost solid voxel.
            surfaceY = y + 1;
            return true;
        }
        return false;
    }
}
