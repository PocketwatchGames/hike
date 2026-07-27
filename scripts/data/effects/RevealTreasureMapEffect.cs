using Godot;

// Reveals a treasure map when the item is picked up: looks up the buried
// treasure this map was authored to point at (by name) in WorldState.TreasureSpots
// and records a TreasureMapState centered there. The link is fixed at worldgen —
// the treasure is placed and named by WorldGen.PlaceZoneTreasures, and this map's
// treasureName matches it — so a given map always points at the same treasure,
// never a dynamically chosen one. The treasure exists independently and is
// diggable with or without the map. Author into a ConsumableData's effects list.
[GlobalClass]
public partial class RevealTreasureMapEffect : ItemEffect
{
    // Name of the treasure this map reveals — matches a zone's
    // ZoneGenData.treasureName / the buried spot's TreasureName.
    [Export] public string treasureName = "";

    // Optional one-shot fx spawned on the player as the map is revealed.
    [Export] public PackedScene revealEffect;

    public override void Apply(IActionActor actor, in ActionContext context)
    {
        if (actor is not Player player)
        {
            return;
        }
        WorldState ws = player.Sim?.WorldState;
        SimState simState = ws?.SimState;
        if (ws == null || simState == null || string.IsNullOrEmpty(treasureName))
        {
            return;
        }
        if (!ws.TreasureSpots.TryGetValue(treasureName, out Vector3 location))
        {
            // Treasure already dug up (registry entry cleared) or not yet
            // streamed in — nothing to chart.
            return;
        }
        // Don't stack a duplicate map for a spot already charted.
        Vector3I key = MapMarkerRecord.KeyFor(location);
        for (int i = 0; i < simState.TreasureMaps.Count; i++)
        {
            if (MapMarkerRecord.KeyFor(simState.TreasureMaps[i].DigLocation) == key)
            {
                return;
            }
        }
        simState.AddTreasureMap(new TreasureMapState(location, DeriveRotation(location)));
        if (revealEffect != null)
        {
            ItemEventHandlers.SpawnOnActor(actor, revealEffect);
        }
    }

    // Deterministic per-location heading so a map's orientation is fixed
    // (predetermined by the treasure's position), not re-rolled each read.
    static float DeriveRotation(Vector3 location)
    {
        int h = (Mathf.RoundToInt(location.X) * 73856093) ^ (Mathf.RoundToInt(location.Z) * 19349663);
        return (h & 0xFFFF) / 65535f * Mathf.Tau;
    }
}
