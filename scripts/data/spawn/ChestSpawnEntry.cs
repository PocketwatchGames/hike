using System;
using Godot;

[GlobalClass]
public partial class ChestSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    // Rigged to spring when the chest opens; null = untrapped. Rolled at spawn
    // against trapChance.
    [Export] public RiggedTrapData trap;
    [Export(PropertyHint.Range, "0,1,0.01")] public float trapChance = 1f;
    // What the chest holds, rolled here at spawn into the ChestSimState's
    // Contents — so a chest that rolled "4 mushrooms" always holds 4, with no
    // re-roll on open and no surprise between save/load.
    [Export] public ItemCountRange[] contents = [];

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        var chest = new ChestSimState(position, scene)
        {
            Trap = trap != null && rng.NextDouble() < trapChance ? trap : null,
            RotationY = FacingY(context),
            SpawnConditions = context?.SpawnConditions ?? ESpawnConditions.None,
        };
        // This chest's own authored contents, plus any zone-unique drops for the
        // zone it spawned in (ZoneGenData.perChestLoot, threaded via
        // SpawnContext) — so a region's signature loot rides every chest without
        // forking the shared chest / spawn-group resources.
        Resolve(contents, rng, chest.Contents);
        Resolve(context?.ZonePerChestLoot, rng, chest.Contents);
        ws.AddEntity(chest);
    }

    private static void Resolve(ItemCountRange[] ranges, Random rng, System.Collections.Generic.List<ItemState> into)
    {
        if (ranges == null)
        {
            return;
        }
        for (int i = 0; i < ranges.Length; i++)
        {
            ranges[i]?.Resolve(rng, into);
        }
    }
}
