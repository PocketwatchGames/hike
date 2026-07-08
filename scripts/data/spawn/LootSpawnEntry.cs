using System;
using Godot;

[GlobalClass]
public partial class LootSpawnEntry : SpawnEntryData
{
    // The item plus any permanent mods composed onto it (e.g. a "Fragile" bomb).
    // This is the weapon-customization seam: author the permutation per-spawn on
    // the descriptor rather than baking a unique ItemData for every combination.
    [Export] public ItemDescriptor item;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (item?.item == null)
        {
            return;
        }
        var simState = new LootSimState(position, item.item);
        // Eager-create the carried ItemState only when the descriptor has
        // per-instance data to compose (mods or ephemeral) — plain drops leave
        // Item null on the sim state and synthesize a fresh state at pickup
        // (cheaper, matches the world-loot default). Mirrors the fairy-loot
        // composition path in World.SpawnLoot.
        if (item.NeedsComposedState)
        {
            simState.Item = item.CreateState();
        }
        ws.AddEntity(simState);
    }
}
