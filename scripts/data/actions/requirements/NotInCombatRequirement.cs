using Godot;

// Refuses an action while the player is in combat — a dangerous enemy they
// perceive is attacking, until it ends (CombatTracker, the state the combat music
// follows). Not NoDangerRequirement: a hostile merely nearby does not count.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class NotInCombatRequirement : ActionRequirement
{
	public override bool Evaluate(IActionActor actor, in ActionContext context)
	{
		return !(GameClient.Current?.InCombat ?? false);
	}
}
