// Inputs handed to ConversationCondition.Evaluate and the conversation
// runner. Add fields here (rather than overloading Evaluate) as new
// conditions need more context — every subclass picks out only the fields
// it cares about.
public struct ConversationContext
{
    public Sim sim;
    public Player player;
    // The entity the player is talking to (typically a Mob). Loosely typed
    // because there's no shared base class for talkable entities yet — the
    // condition subclass can cast as needed.
    public Godot.Node3D speaker;
    // Resolved default language for the speaker. Branches with a null
    // `language` fall back to this — Mob populates it from
    // (MobSimState.Language ?? MobData.language). Null when the speaker has
    // no language pinned at all (universal speech, never scrambled).
    public LanguageData speakerLanguage;
    // The controller running this conversation. Actions that need to take
    // over the screen (OpenShop, etc.) call controller.Close() before their
    // side effect so the conversation panel doesn't overlay the new UI.
    // Populated by ConversationController.Show — null when an evaluator is
    // called outside an active conversation (e.g. preview tooling).
    public ConversationController controller;
}
