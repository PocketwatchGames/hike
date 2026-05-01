// Type tag on ItemEvent. Wire values are stable — append new ones, never
// reuse old numbers, so existing weapon/consumable .tres files keep loading.
// Combat values (Melee/Hitscan/UseAmmo) match the original WeaponEvent enum.
public enum EItemEventType
{
	Melee = 0,
	Hitscan = 1,
	UseAmmo = 2,
	ApplyEffect = 3,
	DecrementStack = 4,
	ToggleCarrierLight = 6,
	PlayAnim = 7,
	PlaySound = 8,
	// Calls Complete() on context.primaryInteractive — the universal way for
	// an interactive action's timeline to trigger the interactive's effect
	// (chest opens, door swings, lockpick succeeds).
	OpenInteractive = 9,
	// Decrements one unit from a matching item in context.supportingItems.
	// The matching item is identified by ev.reagent (an ItemData). On stack
	// reaching zero, the supporting item is removed from the player's
	// inventory.
	ConsumeFromInventory = 10,
	// Reserved (later): SpawnParticle, StopParticle.
}
