using System;
using Godot;

// A KnowledgeStone. If its inscription is written in a language, it teaches one
// lesson of that tongue: the (language, lesson, percent) is wrapped in a
// transient LanguageTeachable so the stone runs through the unified
// TeachableConcept path, and saves/loads re-synthesize it through
// EntitySerializer's Tag.KnowledgeStone wire format. An inscription in no
// language (the common tongue) teaches nothing and is just read — a tutorial
// stone; `lesson` / `percent` are ignored there.
//
// What a stone says and teaches belongs to the world, not to stones in general,
// so the shared palette entry carries only the scene — the same rule, and the
// same reason, as SignpostSpawnEntry. A painter placement picks its inscription
// and lesson on its own copy; worldgen names complete stones under
// worlds/shared/spawn_entries/. A stone with nothing to say is refused loudly.
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

    // What it teaches names the individual: "knowledge_stone: vyeshal lesson 2 (40%)";
    // a stone that teaches nothing is named by what it says: "knowledge_stone: tutorial_sneak".
    public override string VariantName()
    {
        LanguageData language = inscription?.language;
        if (language != null && !string.IsNullOrEmpty(language.ResourcePath))
        {
            return $"{language.ResourcePath.GetFile().GetBaseName()} lesson {lesson} ({percent:0}%)";
        }
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
            GD.PushError($"KnowledgeStoneSpawnEntry at {position}: no inscription — what a stone says "
                + "is picked on the PLACEMENT (in the painter's property panel), not on the shared "
                + "palette entry. Not placed.");
            return;
        }
        LanguageData language = inscription.language;
        Godot.Collections.Array<TeachableConcept> concepts = language == null ? null : new()
        {
            new LanguageTeachable { language = language, lesson = lesson, percent = percent },
        };
        ws.AddEntity(new KnowledgeStoneSimState(position, scene, inscription, concepts)
        {
            RotationY = FacingY(context),
        });
    }
}
