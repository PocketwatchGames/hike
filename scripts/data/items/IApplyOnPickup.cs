// Item data that is applied to the player the moment it is picked up instead of
// entering the backpack — the scroll read on the spot. Player.TakeItem branches
// on it, so every path that hands the player an item gets the same behaviour.
public interface IApplyOnPickup
{
	// Apply this item's payload to `player`. True when the pickup was spent and
	// should leave the world; false leaves it in place.
	bool ApplyOnPickup(Player player);
}
