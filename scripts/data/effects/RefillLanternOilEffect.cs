using Godot;

// Tops up the player's lantern oil — the payload of a lantern-oil flask, an oil
// droplet and a fountain's refuel. Oil is the carrier's (Player.LanternOil),
// not any one lantern's.
[GlobalClass]
public partial class RefillLanternOilEffect : ItemEffect
{
	// Oil restored per use (a default supply is 100). Capped at the player's max.
	[Export(PropertyHint.Range, "0,1000,1")] public float amount = 50f;

	// Ignore `amount` and fill to the player's max (a fountain).
	[Export] public bool fillToMax;

	// Optional one-shot fx spawned on the player (a refuel cue).
	[Export] public PackedScene effectScene;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			if (fillToMax)
			{
				player.RefuelLantern();
			}
			else
			{
				player.AddLanternOil(amount);
			}
		}
		if (effectScene != null)
		{
			ItemEventHandlers.SpawnOnActor(actor, effectScene);
		}
	}
}
