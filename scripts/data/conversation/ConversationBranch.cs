using Godot;
using Godot.Collections;

// A single NPC turn: one or more paragraphs (ConversationLine) plus a
// pointer to the player ConversationResponseGroup shown after the last
// paragraph. `name` is the identifier other branches /
// entries / responses link to.
//
// In the bipartite flow model, branches and response groups alternate as
// graph nodes. A branch has at most one outgoing edge — its `exitGroup`.
// Multiple branches may name the same exitGroup (loops / return flows);
// one of them must be flagged `isPrimaryGroupEntry` so the runtime knows
// which branch's text to use as the visibility context for that group.
[GlobalClass]
public partial class ConversationBranch : Resource
{
    [Export] public StringName name;

    // Language the NPC speaks for this branch's lines. The controller pipes
    // each resolved line through TextScrambler against the listening
    // player's learned components, so untaught portions render as gibberish.
    // Null = fall back to ConversationContext.speakerLanguage (the speaker's
    // default, normally MobSimState.Language ?? MobData.language). Set this
    // explicitly when a branch should override the speaker's default — e.g.
    // a wizard greets in Common then chants a spell in Elvish.
    [Export] public LanguageData language;

    // The NPC's spoken paragraphs, typed in order; ui_accept reveals then
    // advances. Advancing past the last one fires its completeActions and then
    // shows the exitGroup's chooser (or closes if exitGroup is empty).
    [Export] public Array<ConversationLine> lines = new();

    // Name of the ConversationResponseGroup shown after the last paragraph.
    // Empty = the conversation ends after this branch with no chooser.
    [Export] public StringName exitGroup;

    // Marks this branch as the canonical introduction to its exitGroup.
    // The runtime resolves response visibility for that group using
    // THIS branch's lines + language, regardless of which branch
    // the player actually arrived from — so a loop-back path through
    // "tell_more" or "beard" still scores responses against the
    // greeting's text. Only one branch per group should set this; if
    // none does, the first branch whose exitGroup names the group is
    // used as an implicit fallback.
    [Export] public bool isPrimaryGroupEntry;
}
