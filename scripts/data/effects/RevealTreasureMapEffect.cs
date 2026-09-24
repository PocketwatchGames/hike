using Godot;

// Reveals a treasure map when the item is picked up: looks up the buried
// treasure this map was authored to point at (by name) in WorldState.TreasureSpots
// and records a TreasureMapState centered there. The link is fixed when the
// world is built — the treasure is buried under a name (a zone's treasureName,
// or a named buried spot in the painter) and this map's treasureName matches it
// — so a given map always points at the same treasure, never a dynamically
// chosen one. The treasure exists independently and is diggable with or without
// the map. Author into a ConsumableData's effects list.
//
// The item-shaped half of the pair: TreasureMapTeachable charts the same
// treasure as a TeachableConcept (scroll, knowledge stone, NPC conversation).
// Both route through WorldState.RevealTreasureMap.
[GlobalClass]
public partial class RevealTreasureMapEffect : ItemEffect
{
    // Name of the treasure this map reveals — a zone's ZoneGenData.treasureName,
    // or the name a buried spot was given in the painter.
    [Export] public string treasureName = "";

    // Optional one-shot fx spawned on the player as the map is revealed.
    [Export] public PackedScene revealEffect;

    public override void Apply(IActionActor actor, in ActionContext context)
    {
        if (actor is not Player player)
        {
            return;
        }
        // False when the spot is already charted or already dug up — nothing to
        // chart, and no fx.
        WorldState ws = player.Sim?.WorldState;
        if (ws?.RevealTreasureMap(treasureName) != true)
        {
            return;
        }
        if (revealEffect != null)
        {
            ItemEventHandlers.SpawnOnActor(actor, revealEffect);
        }
        OpenIfFirstMap(ws);
    }

    // The first map the player ever picks up opens the world map on itself, so
    // they learn where maps live.
    static void OpenIfFirstMap(WorldState ws)
    {
        StringName flag = ws.SimData?.foundFirstMapVariable;
        if (flag is null || flag.IsEmpty || ws.SimState.ScriptVars.GetBool(flag))
        {
            return;
        }
        ws.SimState.ScriptVars.SetBool(flag, true);
        // RevealTreasureMap appends, so the new map is the last one.
        GameClient.Current?.OpenTreasureMap(ws.SimState.TreasureMaps[^1]);
    }
}
