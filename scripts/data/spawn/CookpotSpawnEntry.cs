using System;
using Godot;

// Places a cookpot. It always spawns unlit; the player lights it on first use.
[GlobalClass]
public partial class CookpotSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    public override bool RequireFlatTerrain => true;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new CookpotSimState(position, scene)
        {
            RotationY = FacingY(context),
        });
    }
}
