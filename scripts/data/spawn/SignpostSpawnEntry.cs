using System;
using Godot;

// A readable signpost. What it SAYS belongs to the world, not to signposts in
// general, so the shared palette entry carries no text and no language — both are
// authored wherever the text is: worldgen embeds a per-POI copy in the containing
// PoiPlacement, and the painter forks this entry into the placement on first edit
// (EntityPlacement.EditableEntry, copy-on-write). Language rides with text because
// the two are one decision — an inscription is written IN something, and a shared
// default for either is a proper noun sitting in reusable vocabulary.
//
// A signpost with nothing to say is not placeable, so an unedited placement is
// refused loudly rather than silently drawing a blank post. Wants flat, grassy
// ground so the post does not tilt off a step edge.
[GlobalClass]
public partial class SignpostSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    [Export(PropertyHint.MultilineText)] public string text = "";
    [Export] public LanguageData language;

    public override bool RequireFlatTerrain => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        if (string.IsNullOrEmpty(text))
        {
            GD.PushError($"SignpostSpawnEntry at {position}: no text — a signpost's inscription is "
                + "authored on the PLACEMENT (edit it in the painter, or embed a copy in the "
                + "containing PoiPlacement), not on the shared palette entry. Not placed.");
            return;
        }
        ws.AddEntity(new SignpostSimState(position, scene, text, language));
    }
}
