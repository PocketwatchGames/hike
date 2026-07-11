using System.Collections.Generic;

public class ItemState
{
	public virtual ItemData data => _data;
	private readonly ItemData _data;

	public int stackCount;

	public ulong cooldownExpireMs;
	public ulong cooldownDurationMs;

	// Day number (World.DayNumber) on which this item is destroyed wherever it
	// lives — backpack, hotbar, or an equipped slot. 0 = no scheduled removal
	// (the default). A time-limited drop (e.g. the fairy corpse) stamps this to a
	// future day directly, so it vanishes at the next sleep-to-sunrise (there is no
	// wall-clock sunrise to project toward — the clock stops at midnight). The
	// player checks it in TickItemExpiry and a dropped instance honors it in Loot.
	public int removeOnDay;

	// Power tier, composed onto the state at construction (ItemDescriptor.level),
	// NOT earned through use. 0 = base. WeaponState scales outgoing damage by
	// 2^level and ArmorState scales its armor points by 2^level; harmless (unused)
	// on other item kinds.
	public int level;

	// Set once this item has ever entered the player's inventory (picked up,
	// bought, cooked, withdrawn from a chest, starting gear — every Inventory
	// acquisition path stamps it). Travels with the object like `statusEffects`
	// — stays true after the item is dropped back into the world, so a
	// re-encountered drop reads as "already handled" rather than pristine. Split
	// stacks inherit it from their source. Never cleared.
	public bool touched;

	// Per-item status effects (wetness on a garment, a timed enchantment on
	// a sword, etc.). Lives on the item so it travels with the object: a wet
	// shirt unequipped into the backpack stays wet; an enchanted sword
	// dropped into a chest keeps the enchant. Constructed with null actor /
	// world / damage callback because items have no world position to spawn
	// fx at and no HP to chip — the controller's null-safe paths skip those
	// branches. Audiovisual cues for item-side effects ride on the wearer's
	// own status (e.g. wet armor cascading into the player's Wet meter, which
	// arms the player-side effect and surfaces the splash + loop fx there).
	public readonly StatusEffectController statusEffects = new StatusEffectController(null, null, null);

	// Menu of boons this specific item instance can bestow when used. Composed
	// onto the state at drop/creation time from the loot source (e.g. a fairy
	// corpse's possible boons) rather than baked into the shared ItemData, so
	// the set is per-instance and is narrowed to the one the player chooses. An
	// ApplyStatusEffect event with no fixed statusEffect applies one entry from
	// this list (see ApplyStatusEffect.Apply). Empty for ordinary items, whose
	// use-effects are authored directly on their events.
	public readonly List<BoonData> possibleBoons = new List<BoonData>();

	public ItemState(ItemData d)
	{
		_data = d;
		stackCount = 1;
	}

	public bool IsSameKind(ItemState other)
	{
		return other != null && other.data == _data;
	}

	// Two items combine into one stack only when they're the same kind AND share
	// a spoil deadline — perishables acquired on different days keep separate
	// stacks so each cohort expires on its own day. Non-perishables both carry
	// removeOnDay == 0, so this reduces to IsSameKind for them.
	public bool CanStackWith(ItemState other)
	{
		return IsSameKind(other) && removeOnDay == other.removeOnDay;
	}

	public int RemainingStackSpace()
	{
		if (_data == null)
		{
			return 0;
		}
		return _data.maxStack - stackCount;
	}
}
