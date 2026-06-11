using Godot;

// An item paired with the permanent status-effect mods it ships with — the
// reusable "complex item" unit. Spawn sources (LootSpawnEntry, ItemCount, mob /
// chest loot tables) hold one of these instead of a bare ItemData so any drop,
// starting item, or loot entry can carry mods (e.g. a "Fragile" bomb whose lobs
// detonate on contact) without authoring a unique ItemData per permutation.
//
// Composed, not inherited: a stack (ItemCount) or a spawn entry HAS-A
// descriptor; neither IS a kind of one. CreateState builds the runtime ItemState
// with the mods composed onto its item-side `statusEffects` controller.
[GlobalClass]
public partial class ItemDescriptor : Resource
{
	[Export] public ItemData item;

	// Permanent mods composed onto the created state's `statusEffects`
	// controller. Each entry pairs a StatusEffectData with the scope deciding
	// which of the weapon's attacks it modifies (all attacks, or one charge
	// tier). Added as a live effect, so the item carries it for its whole
	// lifetime — author the effect with duration 0 (see StatusEffectData's
	// "Weapon modifiers" section). Empty = a plain item.
	[Export] public Godot.Collections.Array<StatusEffectDescriptor> statusEffects = new();

	// Build the runtime state and compose the permanent mods onto it. Returns
	// null when `item` is unset. The composed effects live on the returned
	// state's item-side `statusEffects` controller (null actor / world / health,
	// so no fx or DoT — these are passive modifiers, not combat states).
	public ItemState CreateState()
	{
		if (item == null)
		{
			return null;
		}
		ItemState state = item.CreateState();
		ApplyTo(state);
		return state;
	}

	// Compose the mods onto an already-created state. Split out so callers that
	// build the state themselves (to set stackCount, etc.) still apply mods the
	// same way CreateState does.
	public void ApplyTo(ItemState state)
	{
		if (state == null || statusEffects == null)
		{
			return;
		}
		for (int i = 0; i < statusEffects.Count; i++)
		{
			StatusEffectDescriptor desc = statusEffects[i];
			if (desc?.effect != null)
			{
				state.statusEffects.AddWeaponMod(desc.effect, desc.scope, desc.chargeIndex);
			}
		}
	}

	// True when this descriptor carries at least one mod — i.e. CreateState
	// produces a state worth threading through the loot pipeline rather than
	// synthesizing a fresh one at pickup.
	public bool HasStatusEffects => statusEffects != null && statusEffects.Count > 0;
}
