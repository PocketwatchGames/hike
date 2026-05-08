using System;
using Godot;

[GlobalClass]
public partial class BerryTreeSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;

    // Number of berries the spawned tree drops when picked. Typical forest
    // tree: 3..6.
    [Export] public int BerryCountMin = 1;
    [Export] public int BerryCountMax = 1;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        int berryCount = rng.Next(BerryCountMin, BerryCountMax + 1);
        ws.AddEntity(new BerryTreeSimState(position, Scene, berryCount));
    }
}
