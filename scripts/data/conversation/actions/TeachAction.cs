using Godot;

// Conversation side-effect that teaches the player one or more
// TeachableConcepts — the NPC-dialogue mouth of the same system knowledge
// stones, scrolls and WorldStartData.initialKnowledge already use. A concept is
// whatever a source can grant: a language (or a subset of its component bits),
// a recipe, a spell, an item identification, a bestiary entry, a map region, a
// treasure map, a quest flag.
//
// Authored on the response where the teaching actually lands ("[Teach me your
// tongue.]"), or on the line's completeActions when the telling IS the line.
//
// An array rather than a single concept because one exchange plausibly grants
// several at once (the tongue AND the name of the place it's spoken) — matching
// KnowledgeStone, whose inscription teaches a list.
//
// Dedup is the concept's own: Teach returns true only on a NEW grant, so
// re-running this on a repeatable response teaches nothing twice and the fx
// stays silent. It grants to the ACTIVE party member's provisional field store,
// which becomes permanent when the player next camps (Party.BankActive) — the
// same path every other teaching source takes.
[GlobalClass]
public partial class TeachAction : ConversationAction
{
    [Export] public Godot.Collections.Array<TeachableConcept> concepts = new();

    public override void Execute(ConversationContext ctx)
    {
        Player player = ctx.player;
        if (player == null || concepts == null)
        {
            return;
        }
        int count = concepts.Count;
        for (int i = 0; i < count; i++)
        {
            concepts[i]?.Teach(player);
        }
    }
}
