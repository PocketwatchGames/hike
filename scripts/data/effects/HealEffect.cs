using Godot;

[GlobalClass]
public partial class HealEffect : ItemEffect
{
	[Export] public float amount = 25f;
	[Export] public PackedScene effectScene;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			player.Heal(amount);
		}
		if (effectScene != null)
		{
			ItemEventHandlers.SpawnOnActor(actor, effectScene);
		}
	}
}
