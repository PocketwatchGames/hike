using Godot;

// Tops up the player's lantern by a fraction of its capacity — the payload of a
// lantern-oil pickup. The lantern's fuel is a time budget
// (LanternState.FuelRemainingMs counting down LanternData.BurnTimeMs), so a
// "portion of oil" is a fraction of that ceiling. No-op for a lantern that burns
// forever (unlimited fuel), so oil isn't silently wasted on one that can't hold it.
[GlobalClass]
public partial class RefillLanternOilEffect : ItemEffect
{
	// Portion of a full tank (BurnTimeMs) restored per use. 1 = a complete
	// refill; the default tops up half. Clamped to full so overfilling caps out.
	[Export(PropertyHint.Range, "0,1,0.05")] public float refillFraction = 0.5f;

	// Every lantern the player carries, not just the lit one (a fountain).
	[Export] public bool allCarried;

	// Optional one-shot fx spawned on the player (a refuel cue).
	[Export] public PackedScene effectScene;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player && player.Inventory != null)
		{
			if (allCarried)
			{
				foreach (ItemState item in player.Inventory.EnumerateAll())
				{
					Refill(item as LanternState);
				}
			}
			else
			{
				Refill(player.Inventory.LitLantern ?? FirstCarriedLantern(player.Inventory));
			}
		}
		if (effectScene != null)
		{
			ItemEventHandlers.SpawnOnActor(actor, effectScene);
		}
	}

	// With none lit, the oil goes into the first lantern carried rather than
	// being wasted.
	private static LanternState FirstCarriedLantern(Inventory inventory)
	{
		foreach (ItemState item in inventory.EnumerateAll())
		{
			if (item is LanternState lantern)
			{
				return lantern;
			}
		}
		return null;
	}

	private void Refill(LanternState lantern)
	{
		if (lantern?.data is LanternData lanternData && lanternData.HasLimitedFuel)
		{
			long max = lanternData.BurnTimeMs;
			long add = (long)(refillFraction * max);
			lantern.FuelRemainingMs = System.Math.Min(max, lantern.FuelRemainingMs + add);
		}
	}
}
