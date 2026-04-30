using Godot;

[GlobalClass]
public partial class HealEffect : ItemEffect
{
	[Export] public float amount = 25f;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			player.Heal(amount);
		}
	}
}
