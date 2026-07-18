using Godot;

// How a quest's progress is surfaced on its HUD widget. Orthogonal to the
// quest TYPE (the QuestData subclass) — a kill-count and a language quest both
// reuse Counter without duplicating formatting. The runtime QuestState supplies
// the current/target/remaining values; QuestState.ComposeText renders them.
public enum EQuestProgress
{
    // Objective text only ("Rescue Misha!", "Return to Camp").
    None,
    // Title + " (X/Y)".
    Counter,
    // Title + " (NN%)".
    Percent,
    // Title + " (M:SS)" counting down a sim-clock deadline.
    Countdown,
}

// Authored, static definition of a quest — its presentation config plus (on
// subclasses) any type-specific parameters. The mutable per-run tracking lives
// on the paired runtime QuestState, minted by CreateRuntime(). Mirrors the
// BehaviorData -> BehaviorBase split.
//
// Non-abstract (virtual + GD.PushError fallback) so Godot's editor resource
// picker can instantiate the base type; author concrete quests as one of the
// subclasses.
[GlobalClass]
public partial class QuestData : Resource
{
    // Localization key for the objective text. Formatted by the runtime — most
    // quests just Loc.Get it, but a quest with a placeholder (Rescue's "%0")
    // Loc.Formats it with its runtime args. Kept as a StringName so it can be
    // authored as data (see Loc.Get(StringName)).
    [Export] public StringName textKey;

    // Optional icon for the widget's TextureRect. Null leaves the scene's
    // authored default frame.
    [Export] public Texture2D icon;

    // How the widget renders progress. See EQuestProgress.
    [Export] public EQuestProgress progressDisplay = EQuestProgress.None;

    // Mint the runtime tracker for this quest. Each subclass overrides to
    // return its paired QuestState. Code paths that need runtime context (e.g.
    // the rescued Player) construct the runtime directly instead.
    public virtual QuestState CreateRuntime()
    {
        GD.PushError($"QuestData subclass '{GetType().Name}' did not override CreateRuntime");
        return null;
    }
}
