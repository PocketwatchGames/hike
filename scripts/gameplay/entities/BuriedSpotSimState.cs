using Godot;

// Persistent sim state for a buried spot: what is in the ground, how the spot
// looks, and whether it has been dug. The payload is spawned at dig time, after
// which the dug-up entity (chest / loot / mob) persists on its own terms — this
// state never stores rolled contents.
//
// Excavated is always serialized (EntitySerializer, Tag.BuriedSpot). For a spot
// in a baked world that keeps a dug treasure dug while the session lasts; for a
// worldgen-scattered spot it is harmless — the chunk re-rolls it regardless,
// which is how carrots "forget".
public class BuriedSpotSimState : EntitySimState
{
    public readonly BuriedSpotStyleData Style;

    // What digging yields — see BuriedSpotSpawnEntry.
    public ItemData Item;
    public int Count = 1;
    public SpawnEntryData Payload;

    // Set true once the spot has been dug. Drives the runtime visual (hint vs
    // dirt mound) and gates re-digging.
    public bool Excavated;

    // The treasure this spot IS, when it was placed under a name (see
    // BuriedSpotSpawnEntry.Spawn). Empty for an ordinary spot. The name→position
    // lookup a map needs is WorldState.TreasureSpots, baked into the .hike
    // header; this copy is what lets digging the spot strike it off.
    public string TreasureName = "";

    // Scene is the shared buried_spot.tscn (carries the BuriedSpot script +
    // model anchor).
    public BuriedSpotSimState(Vector3 worldPosition, PackedScene scene, BuriedSpotStyleData style)
        : base(worldPosition, scene)
    {
        Style = style;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        if (Style == null)
        {
            return null;
        }
        return BuriedSpot.Create(sim, this);
    }
}
