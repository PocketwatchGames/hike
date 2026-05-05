using System;
using Godot;

// Base class for one entry in a SpawnGroupData. Subclass per entity type
// (MobSpawnEntry, ChestSpawnEntry, ...) and override Spawn to construct the
// matching EntitySimState and add it to the world. Per-entity properties
// (SpawnAtNight on mobs, loot count on chests, etc.) live on the subclass —
// the group stays agnostic.
[GlobalClass]
public partial class SpawnEntryData : Resource
{
    [Export] public int CountMin = 1;
    [Export] public int CountMax = 1;

    public virtual void Spawn(WorldState ws, Vector3 position, Random rng)
    {
        GD.PushError($"SpawnEntryData subclass '{GetType().Name}' did not override Spawn");
    }
}
