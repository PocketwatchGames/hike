using System.Collections.Generic;

public class ItemState
{
	public virtual ItemData data => _data;
	private readonly ItemData _data;

	public int stackCount;

	public ulong cooldownExpireMs;
	public ulong cooldownDurationMs;

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

	// Menu of status effects this specific item instance can bestow when used.
	// Composed onto the state at drop/creation time from the loot source (e.g. a
	// fairy corpse's possible boons) rather than baked into the shared ItemData,
	// so the set is per-instance and can eventually be narrowed to the one the
	// player chooses. An ApplyStatusEffect event with no fixed statusEffect
	// applies one entry from this list (see ApplyStatusEffect.Apply). Empty for
	// ordinary items, whose use-effects are authored directly on their events.
	public readonly List<StatusEffectData> possibleStatusEffects = new List<StatusEffectData>();

	public ItemState(ItemData d)
	{
		_data = d;
		stackCount = 1;
	}

	public bool IsSameKind(ItemState other)
	{
		return other != null && other.data == _data;
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
