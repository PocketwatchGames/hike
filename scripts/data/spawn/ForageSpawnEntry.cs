using System;
using Godot;

// Scatters forageable resource nodes (mushrooms, herbs) across a zone. Each
// placement becomes a persistent ForageSpawner that presents `item` as a pickup
// and regrows it `regrowDays` after it's collected. Replaces a plain
// LootSpawnEntry for anything that should come back over time rather than being
// gone for good once picked.
[GlobalClass]
public partial class ForageSpawnEntry : SpawnEntryData
{
    // Shared, visually-empty spawner scene (ForageSpawner). One scene serves
    // every forageable — the presented item varies per entry, not per scene.
    [Export] public PackedScene scene;

    // The item the spawner presents as a pickup.
    [Export] public ItemData item;

    // In-world days from harvest until the pickup regrows. Measured in days, so a
    // patch cleared at dusk is back several sleeps later.
    [Export(PropertyHint.Range, "1,60,1,or_greater")] public int regrowDays = 3;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null || item == null)
        {
            return;
        }
        ws.AddEntity(new ForageSpawnerSimState(position, scene, item, regrowDays));
    }
}
