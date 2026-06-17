using System;
using Godot;

// Single campfire entity. Spawns dark with AutoLightAtNight set so the
// campfire ignites only when its chunk activates after dark.
//
// To author a campfire surrounded by an encampment (mobs, chests scattered
// around the fire), wrap this entry inside a SpawnGroupData entry alongside
// the scatter mobs/chests. The group itself goes into the zone's
// SurfaceEntities list — the WorldGen surface scan picks the column, the
// group rolls each sub-entry's count and scatters within ScatterRadius.
[GlobalClass]
public partial class CampfireSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;

    // Radius (meters) around the campfire where worldgen-painted detail
    // sprites (small grasses, pebbles — see DetailEntry / ChunkDetailScatter)
    // are erased so the authored campfire scene's logs / stones / embers
    // don't share a footprint with scattered foliage. StampDetailScatter
    // runs before the surface-entity pass in WorldGen.Generate, so the
    // clear can fire inline from this Spawn — no post-pass needed.
    [Export] public float DetailSuppressionRadius = 2f;

    // Danger-zone radius mobs avoid while wandering and never spawn inside
    // (the campfire's damage sphere is ~0.75m; pad it). Small enough that an
    // encampment's scattered mobs still ring the fire — they just won't stand
    // in it. Attack pathing ignores it so the player can lure mobs in.
    [Export] public float HazardRadius = ForgeSimState.DefaultHazardRadius;
    public override float HazardSpawnRadius => HazardRadius;

    // Campfires sit visually awkwardly on cliff edges and ramp adjacencies
    // (the bowl tilts, surrounding fuel/rocks intersect the step face).
    public override bool RequireFlatTerrain => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        var campfire = new ForgeSimState(position, Scene);
        campfire.AutoLightAtNight = true;
        campfire.Active = false;
        campfire.HazardRadius = HazardRadius;
        ws.AddEntity(campfire);
        ws.ClearDetailVoxelsWithin(position, DetailSuppressionRadius);
    }
}
