using Godot;

// What lives in and turns up in a place, as opposed to how the place LOOKS
// (ZoneData). Split from the zone so one zone's palette can sit under several
// habitats, and one habitat under several palettes.
//
// Recorded per chunk in the world (ChunkState.HabitatIndex into
// WorldState.Habitats) and read live by position (WorldState.HabitatAt), so
// retuning a habitat needs no rebake. A painted world paints it on its own
// per-chunk layer; a generated world takes each zone's ZoneGenData.habitat.
// A chunk with none has no fairies and no habitat loot.
[GlobalClass]
public partial class HabitatData : Resource
{
    // Chance each of the day's fairy spawn windows actually produces a fairy
    // while the player stands here. 0 = fairies never appear. See FairySpawner.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fairySpawnChance = 0f;

    // Rolled the moment a breakable that fills with habitat loot breaks
    // (Destructible._fillWithHabitatLoot) or a creature that carries it dies
    // (MobData.carriesHabitatLoot), on top of their own drops. Each row rolls
    // its own chance. Breakables and mobs both come back, so nothing here
    // should be a one-off — that belongs in a chest.
    [Export] public ItemCountRange[] loot = System.Array.Empty<ItemCountRange>();
}
