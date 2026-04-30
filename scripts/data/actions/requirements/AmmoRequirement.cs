using Godot;

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
