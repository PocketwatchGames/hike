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

	// Stamps ItemState.ephemeral on the created state — the item vanishes at the
	// next sunrise once acquired. Set on the descriptors an altar / forge offers
	// so the temporary gear it grants is intrinsically time-limited, while the
	// same base ItemData stays permanent when granted through an ordinary drop.
	[Export] public bool ephemeral = false;

	// Stamps ItemState.level on the created state — the item's power tier (0 =
	// base). A weapon deals 2^level damage, armor grants 2^level armor points.
	// Composed here, not earned, so the altar / forge grants a leveled piece from
	// the same base ItemData.
	[Export] public int level = 0;

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
		if (state == null)
		{
			return;
		}
		state.ephemeral = ephemeral;
		state.level = level;
		if (statusEffects == null)
		{
			return;
		}
		for (int i = 0; i < statusEffects.Count; i++)
		{
			StatusEffectDescriptor desc = statusEffects[i];
			if (desc?.effect == null)
			{
				continue;
			}
			if (!DeliveryAllows(desc.effect))
			{
				GD.PushWarning($"ItemDescriptor: mod '{desc.effect.ResourcePath}' requires delivery " +
					$"{desc.effect.weaponMod.requiredDelivery} but '{item.ResourcePath}' is " +
					$"{(item as WeaponData)?.delivery.ToString() ?? "not a weapon"} — skipping.");
				continue;
			}
			state.statusEffects.AddWeaponMod(desc.effect, desc.scope, desc.chargeIndex);
		}
	}

	// True unless the effect carries a weaponMod with a delivery requirement the
	// target weapon doesn't satisfy. None requirement always passes; a mod with a
	// requirement only attaches to a WeaponData sharing one of its delivery bits.
	private bool DeliveryAllows(StatusEffectData effect)
	{
		EWeaponDelivery required = effect.weaponMod?.requiredDelivery ?? EWeaponDelivery.None;
		if (required == EWeaponDelivery.None)
		{
			return true;
		}
		return item is WeaponData weapon && (weapon.delivery & required) != 0;
	}

	// True when this descriptor carries at least one mod.
	public bool HasStatusEffects => statusEffects != null && statusEffects.Count > 0;

	// True when CreateState produces per-instance data that must be threaded
	// through the loot pipeline rather than re-synthesized fresh at pickup —
	// composed mods, the ephemeral flag, or a non-base level (all of which a
	// fresh synthesis would drop).
	public bool NeedsComposedState => HasStatusEffects || ephemeral || level != 0;
}
