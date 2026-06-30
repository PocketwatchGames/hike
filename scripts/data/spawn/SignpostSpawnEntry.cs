using System;
using Godot;

// A readable signpost. The inscription text and language live on the entry
// (one signpost per region, each carrying its own text) so the same scene is
// reused across regions without forking it. Wants flat, grassy ground so the
// post doesn't tilt off a step edge.
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
