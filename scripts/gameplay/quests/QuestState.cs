using System.IO;
using Godot;

// Terminal status drives removal from the QuestLog: an Active quest stays, a
// Complete or Failed one is dropped (firing onQuestRemoved) on the next tick.
public enum EQuestStatus
{
    Active,
    Complete,
    Failed,
}

// What a QuestItem widget renders for one quest. The composed Text carries the
// title plus any progress suffix; Fraction (0..1, or -1 for none) is here so a
// future progress bar needs no quest-class changes.
public readonly struct QuestDisplay
{
    public readonly string Text;
    public readonly float Fraction;

    public QuestDisplay(string text, float fraction)
    {
        Text = text;
        Fraction = fraction;
    }
}

// Runtime tracker for one active quest: its authored QuestData plus mutable
// status and any per-quest references (the rescued member, a kill count).
// Paired 1:1 with a QuestData subclass via QuestData.CreateRuntime — the
// BehaviorData -> BehaviorBase idiom. A plain class, not a Resource: the
// QuestLog owns the live instances and SaveGame persists them.
public abstract class QuestState
{
    public QuestData Data { get; protected set; }
    public EQuestStatus Status { get; protected set; } = EQuestStatus.Active;

    protected QuestState(QuestData data)
    {
        Data = data;
    }

    // Subscribe to the events this quest watches (via the GameClient.Current /
    // Sim.Current singletons). Called by QuestLog.Add and after a load
    // restore. Poll-driven quests can leave it empty.
    public virtual void OnStart() { }

    // Unsubscribe. Called by QuestLog when the quest leaves the log.
    public virtual void OnEnd() { }

    // Poll-driven status update on the sim clock. Quests watching derived state
    // (Rescue's corpse, Learn's component count) resolve completion here; purely
    // event-driven ones (Return to Camp) can leave it empty.
    public virtual void Tick(ulong nowMs) { }

    protected void Complete() { Status = EQuestStatus.Complete; }
    protected void Fail() { Status = EQuestStatus.Failed; }

    // Restore status on load (it lives on the base, not the subclass payload).
    public void SetStatus(EQuestStatus status) { Status = status; }

    // --- Display -----------------------------------------------------------

    public QuestDisplay GetDisplay(ulong nowMs)
    {
        string text = ComposeText(GetTitle(), nowMs);
        float frac = Data != null && (Data.progressDisplay == EQuestProgress.Counter
            || Data.progressDisplay == EQuestProgress.Percent) ? Fraction01 : -1f;
        return new QuestDisplay(text, frac);
    }

    // The objective line without any progress suffix. Base reads the authored
    // loc key; a quest with a placeholder (Rescue's "%0") overrides to Loc.Format.
    protected virtual string GetTitle() => Data != null ? Loc.Get(Data.textKey) : string.Empty;

    // Progress inputs — each subclass overrides only the ones its display mode
    // uses.
    protected virtual int Current => 0;
    protected virtual int Target => 0;
    protected virtual float Fraction01 => Target > 0 ? Mathf.Clamp((float)Current / Target, 0f, 1f) : 0f;
    // Sim-clock deadline (GameTimeMs) for a Countdown quest; 0 = none.
    protected virtual ulong DeadlineMs => 0;

    string ComposeText(string title, ulong nowMs)
    {
        switch (Data?.progressDisplay ?? EQuestProgress.None)
        {
            case EQuestProgress.Counter:
                return $"{title} ({Current}/{Target})";
            case EQuestProgress.Percent:
                return $"{title} ({Mathf.RoundToInt(Fraction01 * 100f)}%)";
            case EQuestProgress.Countdown:
                ulong remaining = DeadlineMs > nowMs ? DeadlineMs - nowMs : 0;
                return $"{title} ({FormatClock(remaining)})";
            default:
                return title;
        }
    }

    static string FormatClock(ulong ms)
    {
        int totalSeconds = (int)(ms / 1000);
        return $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
    }

    // --- Persistence (SaveGame v4+) ---------------------------------------
    // The QuestLog writes Data.ResourcePath + Status around this, so a subclass
    // serializes ONLY its own extra state. Its payload is length-prefixed by the
    // log so a quest whose .tres is missing on load can be skipped cleanly.
    public virtual void Serialize(BinaryWriter w) { }
    public virtual void Deserialize(BinaryReader r) { }
}
