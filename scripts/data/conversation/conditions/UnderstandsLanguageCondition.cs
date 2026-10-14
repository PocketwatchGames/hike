using Godot;

// Conversation predicate on how much of a language the PLAYER understands, as
// TextScrambler.ComputeUnderstanding scores it: a share of the whole tongue, not
// of any one line, so a threshold reads the same for every speaker of it.
//
// Authored inline in a conversation sheet's condition cell -
// `understands>=60%`, `understands:vyeshal<100%` - rather than as a .tres per
// threshold.
[GlobalClass]
public partial class UnderstandsLanguageCondition : ConversationCondition
{
    // Language tested. Null = the SPEAKER's own tongue (the resolved
    // ConversationContext.speakerLanguage), the common case: "can the player and
    // this NPC talk?"
    [Export] public LanguageData language;

    // Share of the language, 0..1, compared against.
    [Export(PropertyHint.Range, "0,1,0.01")] public float threshold = 1f;

    // False: true when understood >= threshold. True: when understood < it, the
    // "we can't talk yet" side of the same gate.
    [Export] public bool below;

    // Matches ConversationController's fallback when there is no SimData.
    private const float DEFAULT_GRAMMAR_WEIGHT = 0.2f;

    public override bool Evaluate(ConversationContext ctx)
    {
        Player player = ctx.player;
        if (player == null)
        {
            return false;
        }
        // A speaker with no language pinned at all speaks the common tongue —
        // universally understood.
        LanguageData target = language ?? ctx.speakerLanguage;
        float grammarWeight = ctx.sim?.SimData?.languageGrammarWeight ?? DEFAULT_GRAMMAR_WEIGHT;
        float understood = TextScrambler.ComputeUnderstanding(player.GetLearnedComponents(target), grammarWeight);
        return below ? understood < threshold : understood >= threshold;
    }
}
