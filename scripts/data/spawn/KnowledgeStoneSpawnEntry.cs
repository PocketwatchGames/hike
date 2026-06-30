using System;
using Godot;

// A KnowledgeStone teaching one or more language components. The taught
// (language, components) pair is wrapped in a transient LanguageTeachable so
// the stone runs through the unified TeachableConcept path; that resource is
// never serialized as its own .tres — saves/loads re-synthesize it through
// EntitySerializer's Tag.KnowledgeStone wire format.
[GlobalClass]
public partial class KnowledgeStoneSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    [Export] public LanguageData language;
    [Export(PropertyHint.MultilineText)] public string text = "";
    [Export, CompactFlags] public ELanguageComponents components = ELanguageComponents.All;

    public override bool RequireFlatTerrain => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null || language == null)
        {
            return;
        }
        var concepts = new Godot.Collections.Array<TeachableConcept>
        {
            new LanguageTeachable { language = language, components = components },
        };
        ws.AddEntity(new KnowledgeStoneSimState(position, scene, text, language, concepts));
    }
}
