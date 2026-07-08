public class ArmorState : ItemState
{
	// Composed level (ItemState.level) doubles this piece's armor points per level
	// (2^level, so level 0 = ×1). Summed into the wearer's max armor in
	// Player.RecalculateMaxArmor.
	public float EffectiveMaxArmor => (_data?.maxArmor ?? 0f) * (1 << level);

	public override ArmorData data => _data;
	private readonly ArmorData _data;

	public ArmorState(ArmorData d) : base(d)
	{
		_data = d;
	}
}
