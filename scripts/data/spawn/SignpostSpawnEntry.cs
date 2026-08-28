using System;
using Godot;

// A readable signpost. The inscription is a PLACEMENT decision, not a property of
// signposts in general, so the text here is only the palette default a placement
// starts from — worldgen embeds a per-POI copy in the containing PoiPlacement, and
// the painter forks this entry into the placement on first edit
// (EntityPlacement.EditableEntry). Keep it generic; a sign that names one location
// gets stamped at every placement that has not been edited. Wants flat, grassy
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
        if (scene == null || string.IsNullOrEmpty(text))
        {
            return;
        }
        ws.AddEntity(new SignpostSimState(position, scene, text, language));
    }
}
