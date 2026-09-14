using System;
using Godot;

// A KnowledgeStone teaching one or more language components. The taught
// (language, components) pair is wrapped in a transient LanguageTeachable so
// the stone runs through the unified TeachableConcept path; that resource is
// never serialized as its own .tres — saves/loads re-synthesize it through
// EntitySerializer's Tag.KnowledgeStone wire format.
//
// What a stone teaches and says belongs to the world, not to stones in general,
// so the shared palette entry carries only the scene — the same rule, and the
// same reason, as SignpostSpawnEntry: a language is a proper noun, and a default
// one parked on reusable vocabulary gets stamped at every placement nobody
// edited. A painter placement picks its language, components and text on its
// own copy; worldgen names complete stones under worlds/shared/spawn_entries/.
// A stone with no language teaches nothing and is refused loudly.
[GlobalClass]
public partial class KnowledgeStoneSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    [Export] public LanguageData language;
    [Export(PropertyHint.MultilineText)] public string text = "";
    [Export, CompactFlags] public ELanguageComponents components = ELanguageComponents.All;

    public override bool RequireFlatTerrain => true;

    private static readonly StringName[] Order =
    {
        PropertyName.language, PropertyName.components, PropertyName.text,
    };

    public override StringName[] PropertyOrder => Order;

    // What it teaches names the individual: "knowledge_stone: vyeshal Vocabulary2".
    public override string VariantName()
    {
        if (language == null || string.IsNullOrEmpty(language.ResourcePath))
        {
            return null;
        }
        return $"{language.ResourcePath.GetFile().GetBaseName()} {components}";
    }

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        if (language == null)
        {
            GD.PushError($"KnowledgeStoneSpawnEntry at {position}: no language — what a stone teaches "
                + "is authored on the PLACEMENT (pick it in the painter's property panel), not on "
                + "the shared palette entry. Not placed.");
            return;
        }
        var concepts = new Godot.Collections.Array<TeachableConcept>
        {
            new LanguageTeachable { language = language, components = components },
        };
        ws.AddEntity(new KnowledgeStoneSimState(position, scene, text, language, concepts)
        {
            RotationY = FacingY(context),
        });
    }
}
