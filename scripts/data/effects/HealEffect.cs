using Godot;

[GlobalClass]
public partial class HealEffect : ItemEffect
{
	[Export] public float amount = 250f;
	// Added on top of `amount`, as a fraction of the player's MaxHealth — a
	// full heal is amount 0, fraction 1.
	[Export(PropertyHint.Range, "0,1,0.05")] public float maxHealthFraction;
	// Health past MaxHealth is kept as BonusHealth instead of discarded (a
	// fountain). Potions and spells cap.
	[Export] public bool overflow;
	[Export] public PackedScene effectScene;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			player.Heal(amount + player.MaxHealth * maxHealthFraction, overflow);
		}
		if (effectScene != null)
		{
			ItemEventHandlers.SpawnOnActor(actor, effectScene);
		}
	}
}
