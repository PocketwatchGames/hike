using System;
using Godot;

// A readable signpost. What it SAYS belongs to the world, not to signposts in
// general, so the shared palette entry carries only the scene: the inscription
// is picked per placement — worldgen embeds a per-POI copy in the containing
// PoiPlacement, the painter forks this entry into the placement when one is
// picked (EntityPlacement.EditableEntry, copy-on-write).
//
// A signpost with nothing to say is not placeable, so an unedited placement is
// refused loudly rather than silently drawing a blank post. Wants flat, grassy
// ground so the post does not tilt off a step edge.
[GlobalClass]
public partial class SignpostSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    [Export] public InscriptionData inscription;

    public override bool RequireFlatTerrain => true;

    // What it says names the individual: "signpost: ilunas_way".
    public override string VariantName()
    {
        return inscription == null || string.IsNullOrEmpty(inscription.ResourcePath)
            ? null : inscription.ResourcePath.GetFile().GetBaseName();
    }

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        if (inscription == null || !inscription.HasText())
        {
            GD.PushError($"SignpostSpawnEntry at {position}: no inscription — what a signpost says is "
                + "picked on the PLACEMENT (in the painter's property panel, or on the copy embedded "
                + "in the containing PoiPlacement), not on the shared palette entry. Not placed.");
            return;
        }
        ws.AddEntity(new SignpostSimState(position, scene, inscription)
        {
            RotationY = FacingY(context),
        });
    }
}
