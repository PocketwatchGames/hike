using Godot;

// Per-tier gate. Actions are only selectable during charging when ALL their
// requirements pass. The gate is read-only — deductions (mana spend, ammo
// consume, reagent consume) happen via dedicated events at activation time
// so charging-and-aborting doesn't debit resources.
//
// Subclasses override Evaluate. Resource subclasses must be tagged
// [GlobalClass] so they show up in the inspector picker.
[GlobalClass]
public partial class ActionRequirement : Resource
{
	// Localization key for the event-log line shown when this requirement
	// refuses an interactive action (e.g. "Danger Nearby" for
	// NoDangerRequirement). Empty = the refusal prints no message (the reject
	// Fx still plays). Authored on the requirement sub-resource so the same gate
	// reuses one reason across actions. Only surfaced on the interactive press
	// path — weapon tier gating stays silent.
	[Export] public StringName rejectMessage = "";

	public virtual bool Evaluate(IActionActor actor, in ActionContext context)
	{
		GD.PushError($"ActionRequirement subclass {GetType().Name} must override Evaluate.");
		return false;
	}
}
