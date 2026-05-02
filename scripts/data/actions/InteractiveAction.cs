using Godot;
using Godot.Collections;

// Authored timeline for one verb on an IInteractive (pickup, unlock, break,
// lockpick). Distinct from ItemActionProfile because interactives don't
// charge, queue, combo, or auto-activate — there's exactly one way to
// perform a given verb, and it runs to completion. The runner walks
// `interactEvents` over `durationSeconds` and then fires every entry in
// `completionEvents` as the action ends naturally; an aborted or interrupted
// action skips completion. Authoring "the chest opens after a 3s pick" is
// therefore: durationSeconds=3, interactEvents=picking-anim/sfx,
// completionEvents=[OpenInteractive, chest-creak-sfx].
[GlobalClass]
public partial class InteractiveAction : Resource
{
	// Verb tag — what kind of interaction this is (Open, Lockpick, Break).
	// Diagnostic at runtime; primarily used by Complete() implementations
	// that need to branch on what just resolved (e.g. a chest's lockpick
	// success vs its smash) and by save data / combat logs.
	[Export] public EActionVerb verb = EActionVerb.Use;

	// UI label shown in the radial / hold-prompt. Localization key —
	// resolved through Loc.Get at draw time. Empty string falls back to
	// the verb's enum name.
	[Export] public StringName displayName = "";

	// Events fired during the action, on a timeline measured from the start
	// of Active. For most interactives this is just the per-frame animation
	// and sound that plays *while* the player is holding interact.
	[Export] public Array<ItemEvent> interactEvents = new();

	// Events fired as a batch when the action's duration elapses naturally.
	// The OpenInteractive event lives here, so authors don't have to align
	// its time field to durationSeconds. NOT fired on abort/interrupt.
	[Export] public Array<ItemEvent> completionEvents = new();

	// Total length of the action in seconds. The runner exits Active when
	// this elapses. May be 0 — interactEvents at t=0 and all completion
	// events fire on the same tick (cheap "pick up the loot" with no animation).
	[Export] public float durationSeconds = 0f;

	// Per-action gates (e.g. HasReagentRequirement for lockpicks). The
	// action is only selectable when all evaluate true. Same evaluator
	// type as ItemAction.requirements so existing requirement subclasses
	// work unchanged.
	[Export] public Array<ActionRequirement> requirements = new();

	// Apply a movement lock for the duration of the action. Picking a
	// chest typically locks; grabbing a coin off the ground typically doesn't.
	[Export] public bool locksMovement = false;

	// Cancel the action if the actor takes damage during it. No tier-level
	// canInterrupt — interactives have a single phase.
	[Export] public bool interruptOnDamage = true;
}
