using Godot;

// Teaches one or more component bits of a single language. The bridge from
// the polymorphic TeachableConcept system to the existing
// Player.LearnLanguageComponents flow — pre-existing knowledge stones and
// language scrolls land here so they reuse TextScrambler unchanged.
[GlobalClass]
public partial class LanguageTeachable : TeachableConcept
{
    [Export] public LanguageData language;
    // Bitset of pieces this concept grants. Default All keeps single-shot
    // "learn the whole tongue" scrolls / stones authoring-cheap; partial
    // grants set a subset (Grammar / Numbers / Vocabulary*).
    [Export, CompactFlags] public ELanguageComponents components = ELanguageComponents.All;

    public override string GetDisplayName()
    {
        return language != null ? language.displayName.ToString() : string.Empty;
    }

    public override bool Teach(Player player)
    {
        if (player == null || language == null)
        {
            return false;
        }
        return player.LearnLanguageComponents(language, components);
    }
}
