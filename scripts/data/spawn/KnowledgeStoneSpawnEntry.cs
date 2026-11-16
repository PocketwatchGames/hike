using System;
using Godot;

// A KnowledgeStone teaching one language lesson: the tongue its inscription is
// written in. The taught (language, lesson, percent) is wrapped in a transient
// LanguageTeachable so the stone runs through the unified TeachableConcept path;
// that resource is never serialized as its own .tres — saves/loads re-synthesize
// it through EntitySerializer's Tag.KnowledgeStone wire format.
//
// What a stone says and teaches belongs to the world, not to stones in general,
// so the shared palette entry carries only the scene — the same rule, and the
// same reason, as SignpostSpawnEntry. A painter placement picks its inscription
// and lesson on its own copy; worldgen names complete stones under
// worlds/shared/spawn_entries/. A stone whose inscription is in no language
// teaches nothing and is refused loudly.
[GlobalClass]
public partial class KnowledgeStoneSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    [Export] public InscriptionData inscription;
    // Which lesson the stone teaches, and how much of the language (see
    // LanguageData). Two stones with one number are one lesson.
    [Export] public int lesson;
    [Export(PropertyHint.Range, "0,100,1")] public float percent = LanguageData.FULL_PERCENT;

    public override bool RequireFlatTerrain => true;

    private static readonly StringName[] Order =
    {
        PropertyName.inscription, PropertyName.lesson, PropertyName.percent,
    };

    public override StringName[] PropertyOrder => Order;

    // What it teaches names the individual: "knowledge_stone: vyeshal lesson 2 (40%)".
    public override string VariantName()
    {
        LanguageData language = inscription?.language;
        if (language == null || string.IsNullOrEmpty(language.ResourcePath))
        {
            return null;
        }
        return $"{language.ResourcePath.GetFile().GetBaseName()} lesson {lesson} ({percent:0}%)";
    }

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        LanguageData language = inscription?.language;
        if (language == null)
        {
            GD.PushError($"KnowledgeStoneSpawnEntry at {position}: no inscription in a language — what "
                + "a stone teaches is the tongue its inscription is written in, picked on the "
                + "PLACEMENT (in the painter's property panel), not on the shared palette entry. "
                + "Not placed.");
            return;
        }
        var concepts = new Godot.Collections.Array<TeachableConcept>
        {
            new LanguageTeachable { language = language, lesson = lesson, percent = percent },
        };
        ws.AddEntity(new KnowledgeStoneSimState(position, scene, inscription, concepts)
        {
            RotationY = FacingY(context),
        });
    }
}
