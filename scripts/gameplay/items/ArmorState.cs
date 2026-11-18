public class ArmorState : GearState
{
	// This piece's armor points: ArmorData.maxArmor, doubled per composed level
	// (2^level, so level 0 = ×1), scaled by the item's own EStat.MaxArmorMultiplier
	// mods (ItemDescriptor.statusEffects). Summed into the wearer's max armor in
	// Player.RecalculateMaxArmor.
	public float ComposeMaxArmor()
	{
		float baseArmor = (_data?.maxArmor ?? 0f) * (1 << level);
		return baseArmor * statusEffects.FoldStat(EStat.MaxArmorMultiplier, 1f);
	}

	public override ArmorData data => _data;
	private readonly ArmorData _data;

	public ArmorState(ArmorData d) : base(d)
	{
		_data = d;
	}

	// Runes inscribed at the rune station: resistance to all damage from a species whose
	// sequence they match, while the piece is unbroken (see RuneInscription,
	// Player.ComposeRuneResistance).
	public readonly RuneInscription runes = new();

	public override void WriteSubclassState(System.IO.BinaryWriter w)
	{
		base.WriteSubclassState(w);
		runes.Write(w);
	}

	public override void ReadSubclassState(System.IO.BinaryReader r)
	{
		base.ReadSubclassState(r);
		runes.Read(r);
	}

	protected override void CopySubclassStateTo(ItemState dest)
	{
		base.CopySubclassStateTo(dest);
		runes.CopyTo(((ArmorState)dest).runes);
	}
}
