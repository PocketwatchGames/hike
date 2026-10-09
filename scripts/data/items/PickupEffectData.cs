using Godot;
using Godot.Collections;

// A pickup that is nothing but its effects: applied to the player the moment it
// is collected and never carried (an oil droplet). It takes no inventory space,
// so it always flies to the player (IApplyOnPickup.Magnetized) instead of
// waiting for an interact.
//
// Non-stackable on purpose — the pickup applies once per Loot, so a rolled count
// of N must eject as N separate pickups.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class PickupEffectData : LootData, IApplyOnPickup
{
	[Export] public Array<ItemEffect> effects = new();

	public bool Magnetized => true;

	public bool ApplyOnPickup(Player player)
	{
		if (player != null && effects != null)
		{
			var context = new ActionContext();
			foreach (ItemEffect effect in effects)
			{
				effect?.Apply(player, context);
			}
		}
		return true;
	}
}
