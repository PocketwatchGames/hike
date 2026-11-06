using System;
using Godot;

// Places a cookpot, unlit unless THIS placement says otherwise; an unlit pot is
// lit by the player on first use.
[GlobalClass]
public partial class CookpotSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    // THIS placement starts with its fire lit (a pot someone is already tending).
    // Set on a placement's own copy, never on a shared entry.
    [Export] public bool lit;

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
            Lit = lit,
        });
    }
}
