using System;
using Godot;

// A coil of rope at the top of a drop, thrown over the edge by interacting with
// it and climbed like a dressed cliff face afterwards.
//
// The edge it goes over is the coil's own facing, so the placement's rotation is
// the whole authoring — set the coil at the lip pointing out over the drop.
//
// Deliberately NOT RequireFlatTerrain: a coil belongs at the top of a cliff, and
// the flat gate measures the terrain column under it, which at a lip is by
// definition about to fall away.
[GlobalClass]
public partial class CoiledRopeSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        // The facing is where the rope pays out.
        ws.AddEntity(new CoiledRopeSimState(position, FacingY(context), scene));
    }
}
