using System;
using Godot;

// A readable that teaches and stays — a knowledge stone, an open book. The
// scene is the look; what it says and teaches is the placement's.
//
// A read teaches the inscription's own lesson (InscriptionData.percent of the
// language it is written in — the inscription is the lesson), then every
// entry on `teaches` (a recipe, a region, an item's real name, a treasure
// map, ...), each authored inline on the placement.
//
// The inscription is optional when the source teaches something else — a book
// can grant a recipe and say nothing. Its scene decides whether the words show
// in a speech bubble (a stone) or on the book page (a book).
//
// What a source says and teaches belongs to the world, not to stones or books
// in general, so the shared palette entry carries only the scene — the same
// rule, and the same reason, as SignpostSpawnEntry. A source that neither says
// nor teaches anything is refused loudly.
[GlobalClass]
public partial class KnowledgeSourceSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    [Export] public InscriptionData inscription;
    // Taught on read, beside the inscription's lesson.
    [Export] public Godot.Collections.Array<TeachableConcept> teaches = new();

    public override bool RequireFlatTerrain => true;

    private static readonly StringName[] Order =
    {
        PropertyName.inscription, PropertyName.teaches,
    };

    public override StringName[] PropertyOrder => Order;

    // A language lesson is the inscription's, and a treasure map is picked as
    // its map item (ScrollTeachable) rather than retyped by name.
    public override Type[] RecordKinds(StringName property, Type[] kinds)
    {
        if (property != PropertyName.teaches)
        {
            return kinds;
        }
        return Array.FindAll(kinds, k => k != typeof(LanguageTeachable) && k != typeof(TreasureMapTeachable));
    }

    // Named by what it says, and how much it teaches of the tongue that is in:
    // "knowledge_stone: vyeshal_vocab1 (20%)", "knowledge_book: tutorial_sneak".
    public override string VariantName()
    {
        if (inscription == null || string.IsNullOrEmpty(inscription.ResourcePath))
        {
            return null;
        }
        string name = inscription.ResourcePath.GetFile().GetBaseName();
        return inscription.TeachesLanguage() ? $"{name} ({inscription.percent:0}%)" : name;
    }

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        bool teachesConcepts = teaches != null && teaches.Count > 0;
        if (inscription == null && !teachesConcepts)
        {
            GD.PushError($"KnowledgeSourceSpawnEntry at {position}: no inscription and nothing on `teaches` — "
                + "what a knowledge source says and teaches is picked on the PLACEMENT (in the painter's "
                + "property panel), not on the shared palette entry. Not placed.");
            return;
        }
        if (inscription != null && !inscription.HasText())
        {
            GD.PushError($"KnowledgeSourceSpawnEntry at {position}: inscription "
                + $"{inscription.ResourcePath} has no text. Not placed.");
            return;
        }
        Godot.Collections.Array<TeachableConcept> taught = null;
        if (teaches != null && teaches.Count > 0)
        {
            taught = new Godot.Collections.Array<TeachableConcept>(teaches);
        }
        ws.AddEntity(new KnowledgeSourceSimState(position, scene, inscription, taught)
        {
            RotationY = FacingY(context),
        });
    }
}
