using System;
using Godot;

[GlobalClass]
public partial class BerryTreeSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    // Number of berries the spawned tree drops when picked. Typical forest
    // tree: 3..6.
    [Export] public int berryCountMin = 1;
    [Export] public int berryCountMax = 1;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        int berryCount = rng.Next(berryCountMin, berryCountMax + 1);
        ws.AddEntity(new BerryTreeSimState(position, scene, berryCount));
    }
}
