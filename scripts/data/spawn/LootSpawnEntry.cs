using System;
using Godot;

[GlobalClass]
public partial class LootSpawnEntry : SpawnEntryData
{
    // The item plus any permanent mods composed onto it (e.g. a "Fragile" bomb).
    // This is the weapon-customization seam: author the permutation per-spawn on
    // the descriptor rather than baking a unique ItemData for every combination.
    [Export] public ItemDescriptor Item;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Item?.item == null)
        {
            return;
        }
        var simState = new LootSimState(position, Item.item);
        // Eager-create the carried ItemState only when there are mods to compose
        // — plain drops leave Item null on the sim state and synthesize a fresh
        // state at pickup (cheaper, matches the world-loot default). Mirrors the
        // fairy-loot composition path in World.SpawnLoot.
        if (Item.HasStatusEffects)
        {
            simState.Item = Item.CreateState();
        }
        ws.AddEntity(simState);
    }
}
