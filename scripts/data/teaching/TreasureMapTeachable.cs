using Godot;

// Charts a named buried treasure onto the player's map — the TeachableConcept
// form of a treasure map, so a map can be granted by any teaching source (a
// scroll, a knowledge stone, an NPC's TeachAction) and not only by picking up a
// map item. RevealTreasureMapEffect is the item-shaped counterpart; both route
// through WorldState.RevealTreasureMap so the lookup and dedup rules are shared.
//
// CAVEAT: WorldState.TreasureSpots is a RUNTIME CACHE filled by BuriedSpot as
// its chunk streams in, not world data baked into the .hike. A treasure far from
// the player is therefore not in the registry and this teaches nothing (Teach
// returns false, IsKnown false — so the source stays offered rather than dimming
// as learned). That's fine for a treasure near the teaching source; an NPC
// handing out a map to the far side of the world needs TreasureSpots persisted
// in the world file header the way PointsOfInterest already is.
[GlobalClass]
public partial class TreasureMapTeachable : TeachableConcept
{
    // Name of the treasure to chart — matches a zone's ZoneGenData.treasureName
    // and the buried spot's TreasureName, the same key RevealTreasureMapEffect
    // uses.
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
        // Known only once the map is actually in hand. An unstreamed (or dug up)
        // treasure isn't in the registry, so it reads as still-teachable rather
        // than silently dimming its source.
        return ws.TreasureSpots.TryGetValue(treasureName, out Vector3 location)
            && (ws.SimState?.HasTreasureMapAt(location) ?? false);
    }
}
