using Godot;

// Tops up the player's lantern oil — the payload of a lantern-oil flask, an oil
// droplet and a fountain's refuel. Oil is the carrier's (Player.LanternOil),
// not any one lantern's.
[GlobalClass]
public partial class RefillLanternOilEffect : ItemEffect
{
	// Oil restored per use (a default supply is 100).
	[Export(PropertyHint.Range, "0,1000,1")] public float amount = 50f;

	// Oil past the player's max is kept as bonus oil instead of discarded (a
	// fountain). Flasks and droplets cap.
	[Export] public bool overflow;

	// Optional one-shot fx spawned on the player (a refuel cue).
	[Export] public PackedScene effectScene;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			player.AddLanternOil(amount, overflow);
		}
		if (effectScene != null)
		{
			ItemEventHandlers.SpawnOnActor(actor, effectScene);
		}
	}
}
