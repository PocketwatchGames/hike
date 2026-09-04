using Godot;

// Conversation predicate on whether the PLAYER understands a language. The gate
// for offering a different way in when the two of you share no tongue: put it on
// the ConversationEntry whose branch greets a stranger in gestures and points at
// a response group of common-tongue fumbling, and its negation on the entry that
// greets a player who can actually be spoken to.
//
// Also useful on a single ConversationResponse — "[Answer in Vyeshal]" appears
// only once you can.
[GlobalClass]
public partial class SpeaksLanguageCondition : ConversationCondition
{
    // Language tested. Null = the SPEAKER's own tongue (the resolved
    // ConversationContext.speakerLanguage), which is the common authoring case:
    // "can the player and this NPC talk at all?" — and it keeps one condition
    // resource reusable across every NPC rather than one per language.
    [Export] public LanguageData language;

    // Which pieces must be known to count as speaking it. All = fluent, the
    // sensible default for "can we converse". Lower it for a gate that only
    // needs the player to catch words (Vocabulary1) rather than follow sentences.
    [Export, CompactFlags] public ELanguageComponents components = ELanguageComponents.All;

    // False inverts the test: true when the player does NOT have `components`.
    // The "we have no shared tongue" branch is exactly this, and authoring it as
    // a flag on one condition beats a second near-identical resource type.
    [Export] public bool speaks = true;

    public override bool Evaluate(ConversationContext ctx)
    {
        Player player = ctx.player;
        if (player == null)
        {
            return false;
        }
        // A speaker with no language pinned at all speaks the common tongue —
        // universally understood, so the test is trivially satisfied.
        LanguageData target = language ?? ctx.speakerLanguage;
        bool known = (player.GetLearnedComponents(target) & components) == components;
        return known == speaks;
    }
}
