using Godot;

// Charts a named buried treasure onto the player's map. A treasure map item is a
// ScrollData carrying this concept; a knowledge stone or an NPC's TeachAction can
// grant the same chart. The link to the treasure is fixed when the world is
// built — the treasure is buried under a name (a zone's treasureName, or a named
// buried spot in the painter) and this concept's treasureName matches it — so a
// given map always points at the same treasure.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class TreasureMapTeachable : TeachableConcept
{
    // Name of the treasure to chart — a zone's ZoneGenData.treasureName, or the
    // name a buried spot was given in the painter.
    [Export] public string treasureName = "";

    // Player-facing name of the map ("Treasure Map", "Map of the Old City").
    // Authored here because a treasure spot is a worldgen string, not a named
    // Data resource to derive a name from.
    [Export] public string mapName = "";

    public override string GetDisplayName()
    {
        return mapName;
    }

    // A map is already an object — its scroll is titled by the map's own name.
    public override string ScrollTitle()
    {
        return mapName;
    }

    // The first map the player ever picks up opens the world map on itself, so
    // they learn where maps live. Pickup only: a stone or an NPC charting a map
    // mid-conversation must not throw the world map over it.
    public override void OnLearnedFromScroll(Player player)
    {
        WorldState ws = player?.Sim?.WorldState;
        StringName flag = ws?.SimData?.foundFirstMapVariable;
        if (flag is null || flag.IsEmpty || ws.SimState.ScriptVars.GetBool(flag))
        {
            return;
        }
        ws.SimState.ScriptVars.SetBool(flag, true);
        // RevealTreasureMap appends, so the new map is the last one.
        GameClient.Current?.OpenTreasureMap(ws.SimState.TreasureMaps[^1]);
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
