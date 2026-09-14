using Godot;

// Charts a named buried treasure onto the player's map — the TeachableConcept
// form of a treasure map, so a map can be granted by any teaching source (a
// scroll, a knowledge stone, an NPC's TeachAction) and not only by picking up a
// map item. RevealTreasureMapEffect is the item-shaped counterpart; both route
// through WorldState.RevealTreasureMap so the lookup and dedup rules are shared.
[GlobalClass]
public partial class TreasureMapTeachable : TeachableConcept
{
    // Name of the treasure to chart — a zone's ZoneGenData.treasureName, or the
    // name a buried spot was given in the painter. The same key
    // RevealTreasureMapEffect uses.
    [Export] public string treasureName = "";

    // Player-facing name of the map, used for the "Scroll of <name>" title when
    // this concept is a scroll's payload. Authored here because a treasure spot
    // is a worldgen string, not a named Data resource to derive a name from.
    [Export] public string conceptName = "";

    public override string GetDisplayName()
    {
        return conceptName;
    }

    public override bool Teach(Player player)
    {
        return player?.Sim?.WorldState?.RevealTreasureMap(treasureName) ?? false;
    }

    public override bool IsKnown(Player player)
    {
        WorldState ws = player?.Sim?.WorldState;
        if (ws == null || string.IsNullOrEmpty(treasureName))
        {
            return false;
        }
        // Known only once the map is actually in hand. A dug-up treasure isn't
        // in the registry, so it reads as still-teachable rather than silently
        // dimming its source.
        return ws.TreasureSpots.TryGetValue(treasureName, out Vector3 location)
            && (ws.SimState?.HasTreasureMapAt(location) ?? false);
    }
}
