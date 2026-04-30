using Godot;
using Godot.Collections;

// Authored timeline for an item action. One profile per slot-driven verb on
// a weapon, consumable, or interactive (phase 5). The runner consumes a
// profile + context and runs the timeline; the input/AI/UI layer is what
// chooses *which* profile to run.
[GlobalClass]
public partial class ItemActionProfile : Resource
{
	// Charge tiers, ascending by chargeTime. A tap-fire weapon has a single
	// entry with chargeTime=0 and autoActivateAtMax=true. A charge-then-fire
	// bow has a single entry with chargeTime=0 and autoActivateAtMax=false
	// (release fires it). Multi-tier weapons (Light/Heavy) authored in phase 3.
	[Export] public Array<ChargedAction> chargedActions = new();

	// Events fired while Charging. PlayAnim "wind up", spawn charge particle,
	// etc. Authored on a timeline measured from pressMs. Empty for tap-fire
	// weapons that have no perceivable charge phase.
	[Export] public Array<ItemEvent> chargeEvents = new();

	// One-shot events fired on any Charging→exit transition (activate, abort,
	// interrupt). Used to clean up persistent charging effects (stop emitter,
	// stop loop sound). No timeline — all fire at t=0 of the transition.
	[Export] public Array<ItemEvent> chargeEndEvents = new();

	// Events fired when charging aborts WITHOUT reaching even the lowest
	// tier (player released too early). Distinct from a tier's abortEvents,
	// which fire when its Active phase is cut short.
	[Export] public Array<ItemEvent> abortEvents = new();

	// If true and the player holds past the highest tier's chargeTime,
	// auto-fire that tier (gated by its requirements when phase 4 lands).
	// If false, the player must release to commit.
	[Export] public bool autoActivateAtMax = true;

	// Apply a movement lock for the duration of the action (Charging + Active).
	// Drinking a potion locks; weapon swings typically don't.
	[Export] public bool locksMovement = false;

	// Damage-during-charge interrupt policy. Active-phase interrupt is gated
	// by ChargedAction.canInterrupt instead.
	[Export] public bool interruptOnDamage = true;

	// Phase 3 hook — the runner will queue an in-flight press if Active is
	// within queueWindowSeconds of ending and the profile is queueable.
	[Export] public bool queueable = false;
	[Export] public float queueWindowSeconds = 0.2f;
}
