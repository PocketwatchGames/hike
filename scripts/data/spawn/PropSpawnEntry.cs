using System;
using Godot;

// One hand-placed prop — a tree, a boulder, a barrel. One file per prop, filed
// under spawn_entries/props/<category>/: the folder is the palette section, so
// adding a prop is its scene plus this three-line file in the right folder.
// `worldmap_check` reports a prop scene that has no entry.
[GlobalClass]
public partial class PropSpawnEntry : SpawnEntryData
{
    // The prop. Null places nothing.
    [Export] public PackedScene scene;

    // The placed state's PropType — a scatter slot (canopy vs ground cover) and a
    // wire byte, not derivable from the scene.
    [Export] public PropType propType = PropType.Tree;

    public override PackedScene PaletteScene => scene;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new PropSimState(propType, position, scene)
        {
            RotationY = FacingY(context),
        });
    }
}
