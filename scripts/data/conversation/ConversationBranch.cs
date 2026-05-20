using Godot;
using Godot.Collections;

// A single NPC turn: one or more paragraphs of localized text plus the player
// responses shown after the last paragraph finishes typing. `name` is the
// identifier other branches / entries link to.
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

    // Localization keys for the NPC's spoken paragraphs. Each is resolved via
    // Loc.Get at speak time; the typewriter walks them in order, ui_accept
    // reveals-then-advances. After the last paragraph reveals, `responses`
    // appear (or the conversation ends if `responses` is empty).
    [Export] public Array<StringName> lineLocKeys = new();

    // Player choices shown after the last line finishes. Empty = the
    // conversation ends after the last line.
    [Export] public Array<ConversationResponse> responses;
}
