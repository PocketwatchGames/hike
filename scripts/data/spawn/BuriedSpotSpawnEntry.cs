using System;
using Godot;

// Worldgen placement for a buried-item spot. Use this in a zone's SpawnList to
// scatter forgettable buried items (carrots, small caches) across the terrain —
// because worldgen re-rolls these on chunk regeneration, a dug spot is simply
// forgotten and regrows. Remembered treasure spots are instead authored into
// the persistent world (editor / .hike) with the same BuriedSpotSimState, so
// their dug state survives.
[GlobalClass]
public partial class BuriedSpotSpawnEntry : SpawnEntryData
{
    // Shared buried_spot.tscn (carries the BuriedSpot script + model anchor).
    [Export] public PackedScene scene;
    // Payload + visuals for spots placed by this entry.
    [Export] public BuriedSpotData data;
    // Restrict placement to flat patches (the column and its 8 neighbours share
    // a surface height). On by default so the surface hint / dirt mound sit
    // level and the dug-up payload doesn't tumble down a slope. Clear it for
    // spots that should scatter anywhere.
    [Export] public bool requireFlat = true;

    public override bool RequireFlatTerrain => requireFlat;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null || data == null)
        {
            return;
        }
        ws.AddEntity(new BuriedSpotSimState(position, scene, data));
    }
}
