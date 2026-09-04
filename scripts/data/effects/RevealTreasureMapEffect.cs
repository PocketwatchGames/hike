using Godot;

// Reveals a treasure map when the item is picked up: looks up the buried
// treasure this map was authored to point at (by name) in WorldState.TreasureSpots
// and records a TreasureMapState centered there. The link is fixed at worldgen —
// the treasure is placed and named by WorldGen.PlaceZoneTreasures, and this map's
// treasureName matches it — so a given map always points at the same treasure,
// never a dynamically chosen one. The treasure exists independently and is
// diggable with or without the map. Author into a ConsumableData's effects list.
//
// The item-shaped half of the pair: TreasureMapTeachable charts the same
// treasure as a TeachableConcept (scroll, knowledge stone, NPC conversation).
// Both route through WorldState.RevealTreasureMap.
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
        // False when the spot is already charted, already dug up, or its chunk
        // hasn't streamed in — nothing to chart, and no fx.
        if (player.Sim?.WorldState?.RevealTreasureMap(treasureName) != true)
        {
            return;
        }
        if (revealEffect != null)
        {
            ItemEventHandlers.SpawnOnActor(actor, revealEffect);
        }
    }
}
