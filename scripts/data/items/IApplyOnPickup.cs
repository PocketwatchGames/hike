using Godot;

// Item data whose payload is applied to the player at world-pickup (interact)
// instead of being deposited into inventory — the potion you drink, the scroll
// you read, on the spot. Loot branches on this (TryApplyOnPickup) before the
// normal deposit, mirroring the fairy-corpse boon path; the interactives it
// covers are never carried, so they need no inventory slot. Implementors set a
// non-Material category so field pickup requires an interact rather than
// auto-grabbing on contact.
public interface IApplyOnPickup
{
	// Apply this item's payload to `player`. `worldPosition` is where the loot
	// sat when picked up — used by payloads keyed to the find location (a
	// treasure map rolls its dig spot near here). Returns true when the pickup
	// was spent and the loot should be removed from the world; false leaves it
	// in place (e.g. nothing to apply) so the player can retry.
	bool ApplyOnPickup(Player player, Vector3 worldPosition);
}
