using Godot;

[GlobalClass]
public partial class HealEffect : ItemEffect
{
	[Export] public float amount = 250f;
	// Added on top of `amount`, as a fraction of the player's MaxHealth — a
	// fountain's full heal is amount 0, fraction 1.
	[Export(PropertyHint.Range, "0,1,0.05")] public float maxHealthFraction;
	[Export] public PackedScene effectScene;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			player.Heal(amount + player.MaxHealth * maxHealthFraction);
		}
		if (effectScene != null)
		{
			ItemEventHandlers.SpawnOnActor(actor, effectScene);
		}
	}
}
