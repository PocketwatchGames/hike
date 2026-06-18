using Godot;

// Refuses the action while the actor is taking damage over time (poison,
// burning, bleeding, ...). Drop it on a "rest"-style interactive action
// (sleeping in a tent) so the player can't skip time — and integrate the full
// elapsed DoT — while a damaging effect is active. Reads
// IActionActor.HasDamagingStatusEffect; no per-press state of its own.
[GlobalClass]
public partial class NoDamagingEffectRequirement : ActionRequirement
{
    public override bool Evaluate(IActionActor actor, in ActionContext context)
    {
        if (actor == null)
        {
            // Fail closed from a half-initialised state, matching the other
            // requirement subclasses.
            return false;
        }
        return !actor.HasDamagingStatusEffect;
    }
}
