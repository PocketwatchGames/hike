using Godot;

// Persistent sim state for a buried-item spot. Deliberately tiny: the spot is
// almost entirely static (its payload and visuals live on the shared
// BuriedSpotData), so the only mutable bit is whether it has been dug. The
// payload is rolled and spawned at dig time, after which the dug-up entity
// (chest / loot / mob) persists on its own terms — this state never stores
// rolled contents.
//
// Excavated is always serialized (see EntitySerializer.Tag.BuriedSpot). For a
// spot authored into the persistent world that makes a dug treasure chest stay
// dug across save/load; for a worldgen-scattered spot it is harmless — the spot
// is re-rolled on chunk regeneration regardless, which is how carrots "forget".
public class BuriedSpotSimState : EntitySimState
{
    public readonly BuriedSpotData Data;

    // Set true once the spot has been dug. Drives the runtime visual (hint vs
    // dirt mound) and gates re-digging.
    public bool Excavated;

    // Per-instance name of the treasure this spot holds, stamped by worldgen
    // (WorldGen.PlaceZoneTreasures) so a treasure map can point to it by name.
    // Empty for ordinary buried spots. Lives here (not on the shared Data)
    // because the same BuriedSpotData is reused across zones with distinct names.
    // BuriedSpot re-registers it into WorldState.TreasureSpots on stream-in.
    public string TreasureName = "";

    // Scene is the shared buried_spot.tscn (carries the BuriedSpot script +
    // model anchor); Data carries the payload and per-spot visuals.
    public BuriedSpotSimState(Vector3 worldPosition, PackedScene scene, BuriedSpotData data)
        : base(worldPosition, scene)
    {
        Data = data;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        if (Data == null)
        {
            return null;
        }
        return BuriedSpot.Create(sim, this);
    }
}
