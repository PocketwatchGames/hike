public class WeaponState : ItemState
{
	public int ammo;

	// Combo runtime — set to the activated ItemAction's comboIndex on each
	// activation. comboExpireMs is when the chain breaks if not extended; the
	// ActionRunner uses (now < comboExpireMs) at press time to target
	// `comboIndex + 1` instead of restarting at 0.
	public int comboIndex;
	public ulong comboExpireMs;

	public int exp;
	public int level;

	public override WeaponData data => _data;
	private readonly WeaponData _data;

	public WeaponState(WeaponData d) : base(d)
	{
		_data = d;
		if (_data != null)
		{
			ammo = _data.maxAmmo;
		}
	}

	// Adds exp and promotes level while the running total has crossed the
	// next threshold in SimData.ExpPerLevel. WeaponData.maxLevel caps how
	// many entries this weapon may consume — a weapon with maxLevel=0 never
	// levels regardless of the table contents.
	public void AddExp(int amount, Godot.Collections.Array<int> thresholds)
	{
		if (amount <= 0 || _data == null)
		{
			return;
		}
		exp += amount;
		if (thresholds == null)
		{
			return;
		}
		int cap = System.Math.Min(_data.maxLevel, thresholds.Count);
		while (level < cap && exp >= thresholds[level])
		{
			level++;
		}
	}
}
