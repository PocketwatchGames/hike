using Godot;

// Refuses the action while any dangerous mob is a threat to the player —
// triggered (on combat alert) or currently visible. Drop it on a "safe-only"
// interactive action (cooking at a campfire) so the player can't perform it
// mid-danger. Reads Sim.IsDangerPresent, which scans loaded mobs on demand;
// no per-press state of its own.
[GlobalClass]
public partial class NoDangerRequirement : ActionRequirement
{
    public override bool Evaluate(IActionActor actor, in ActionContext context)
    {
        Sim sim = Sim.Current;
        if (sim == null)
        {
            // Sim not loaded — fail closed so the action can't fire from a
            // half-initialised state, matching the other requirement subclasses.
            return false;
        }
        return !sim.IsDangerPresent();
    }
}
