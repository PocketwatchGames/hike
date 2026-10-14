using System;
using Godot;

// A pile of poles at the top of a drop, built into a ladder down the face by
// interacting with it and climbed like a dressed cliff face afterwards.
//
// The edge it goes down is the kit's own facing, so the placement's rotation is
// the whole authoring — set the kit at the lip pointing out over the drop.
//
// Deliberately NOT RequireFlatTerrain: a kit belongs at the top of a cliff, and
// the flat gate measures the terrain column under it, which at a lip is by
// definition about to fall away.
[GlobalClass]
public partial class LadderKitSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        // The facing is the side the ladder goes down.
        ws.AddEntity(new LadderKitSimState(position, FacingY(context), scene));
    }
}
