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
