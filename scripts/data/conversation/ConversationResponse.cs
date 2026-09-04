using Godot;
using Godot.Collections;

// One player choice attached to a ConversationBranch. Picking it advances the
// conversation to `destination`; an empty destination ends the conversation.
[GlobalClass]
public partial class ConversationResponse : Resource
{
    // Localization key for the player's spoken line / button label. Empty
    // StringName = silent transition (rendered as a generic "continue"
    // prompt rather than echoing a player line).
    [Export] public StringName textLocKey;

    // Tongue the player speaks this line in. Null = the language of the branch
    // being answered (the NPC's), which is the default and the interesting one:
    // the player is attempting THEIR tongue, so a reply the player can't yet form
    // renders scrambled and the comprehension gate can hide it — you can only say
    // what you can say.
    //
    // Set it to SimData.commonTongue for the line that has to land BEFORE a
    // shared language exists ("Do you speak the common tongue?"), and for
    // out-of-language actions ("[Leave]", "[Attack]"). Any explicit language also
    // drops the branch comprehension cap (see ConversationVisibility): you are
    // not answering in kind, so how much of their line you followed has no
    // bearing on whether you can utter this one.
    //
    // A line that switches tongue MID-SENTENCE uses a [lang:<id>] span in
    // english.tsv instead; this field is the line's default, exactly as
    // ConversationBranch.language is for the NPC's.
    [Export] public LanguageData language;

    // Optional gate on whether the response is shown at all. Null = always
    // available.
    [Export] public ConversationCondition condition;

    // Name of the next ConversationBranch. Empty StringName ends the
    // conversation.
    [Export] public StringName destination;

    // Side effects fired in order when the player picks this response. Run
    // before the destination branch's text is shown — an action that ends
    // the conversation (e.g. OpenShop) will suppress the unread destination.
    [Export] public Array<ConversationAction> actions;

    // Tongue this line is spoken in, given the language of the branch it
    // answers. The one implementation of the fallback — the display render and
    // the visibility gate both resolve through here, so a response can never be
    // scored in one language and drawn in another.
    public LanguageData ResolveLanguage(LanguageData branchLanguage)
    {
        return language ?? branchLanguage;
    }
}
