using System;
using Godot;

// A readable signpost. What it SAYS belongs to the world, not to signposts in
// general, so the shared palette entry carries only the scene: the inscription
// is picked per placement — worldgen embeds a per-POI copy in the containing
// PoiPlacement, the painter forks this entry into the placement when one is
// picked (EntityPlacement.EditableEntry, copy-on-write).
//
// A signpost with nothing to say is not placeable, so an unedited placement is
// refused loudly rather than silently drawing a blank post (a scene editor
// preview excepted — see SpawnContext.PlacesIncomplete). Wants flat, grassy
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

    public override string MissingPlacementValue()
    {
        return inscription == null ? "no inscription" : null;
    }

    public override string InvalidPlacementValue()
    {
        return inscription != null && !inscription.HasText()
            ? $"inscription {inscription.ResourcePath} has no text."
            : null;
    }

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null || !CheckPlaceable(position, context, out _))
        {
            return;
        }
        ws.AddEntity(new SignpostSimState(position, scene, inscription)
        {
            RotationY = FacingY(context),
        });
    }
}
