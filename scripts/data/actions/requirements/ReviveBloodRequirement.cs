using Godot;

// Refuses the revive interactive when the player lacks the corpse's
// MobData.reviveHealthCost in spendable health. Per-mob, not per-action: the
// cost is read off the interactive being revived (context.primaryInteractive),
// so one shared requirement on the revive action covers every companion
// species. A cost of 0 always passes (HasBlood short-circuits).
[GlobalClass]
public partial class ReviveBloodRequirement : ActionRequirement
{
    public override bool Evaluate(IActionActor actor, in ActionContext context)
    {
        float cost = (context.primaryInteractive as Mob)?.ReviveHealthCost ?? 0f;
        return actor.HasBlood(cost);
    }
}
