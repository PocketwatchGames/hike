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
    [Export] public PackedScene Scene;
    [Export] public LanguageData Language;
    [Export(PropertyHint.MultilineText)] public string Text = "";
    [Export, CompactFlags] public ELanguageComponents Components = ELanguageComponents.All;

    public override bool RequireFlatTerrain => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null || Language == null)
        {
            return;
        }
        var concepts = new Godot.Collections.Array<TeachableConcept>
        {
            new LanguageTeachable { language = Language, components = Components },
        };
        ws.AddEntity(new KnowledgeStoneSimState(position, Scene, Text, Language, concepts));
    }
}
