// Item data spent the moment it is used from the hotbar (Player.UseHotbarSelection):
// the potion drunk. No action timeline — contrast IUsableItem,
// whose use runs an ItemActionProfile through the runner.
public interface IInstantUseItem
{
	// False when this item is instead used over an action timeline — the menus
	// then offer no Use verb, since a timeline needs the hotbar's press / hold.
	bool CanUseInstantly { get; }

	// Apply this item's payload to `player`. True when a unit was spent and
	// should come off the stack; false leaves the item untouched.
	bool UseOn(Player player);
}
