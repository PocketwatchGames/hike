using Godot;

// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class AmmoRequirement : ActionRequirement
{
	[Export] public int amount = 1;

	public override bool Evaluate(IActionActor actor, in ActionContext context)
	{
		WeaponState weapon = context.primaryItem as WeaponState;
		if (weapon == null)
		{
			return false;
		}
		return weapon.ammo >= amount;
	}
}
