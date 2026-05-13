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

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        var campfire = new ForgeSimState(position, Scene);
        campfire.AutoLightAtNight = true;
        campfire.Active = false;
        ws.AddEntity(campfire);
    }
}
