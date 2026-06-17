using System;
using Godot;

// Spawns a Trap.cs-backed interactive (spike trap, future poison-gas trap, ...)
// from a per-column scan. Generic over the scene: what kind of trap it is lives
// in the .tscn composition (see Trap), so one entry type covers every variant.
//
// RequireFlatTerrain is on because the floor traps occupy a multi-voxel footprint
// and read wrong on a slope or step edge. The surface pass honors it (flat patch
// of desert); the cave-pocket pass leaves SpawnContext.IsFlatColumn null, so the
// check is skipped there and traps drop on any pre-validated cave floor.
[GlobalClass]
public partial class TrapSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;

    // Danger-zone radius mobs avoid while wandering and never spawn inside.
    // The spike field is a ~3x3m square (4x4 trigger), so this is larger than
    // the fire traps — a disc of this radius covers the spiked floor. Attack
    // pathing ignores it so the player can lure mobs across the spikes.
    [Export] public float HazardRadius = TrapSimState.DefaultHazardRadius;
    public override float HazardSpawnRadius => HazardRadius;

    public override bool RequireFlatTerrain => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        ws.AddEntity(new TrapSimState(position, Scene) { HazardRadius = HazardRadius });
    }
}
