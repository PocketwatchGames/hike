using Godot;

// Refuses picking up loot whose whole stack won't fit in the actor's backpack,
// so the press is answered with a reason ("Inventory Full") instead of the pile
// ignoring the player. Passes for any interactive that isn't loot.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class PickupFitsRequirement : ActionRequirement
{
	public override bool Evaluate(IActionActor actor, in ActionContext context)
	{
		if (context.primaryInteractive is not Loot loot)
		{
			return true;
		}
		return loot.FitsIn(actor as Player);
	}
}
