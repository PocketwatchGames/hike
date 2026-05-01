public class WeaponState : ItemState
{
	public int ammo;

	// Combo runtime — set to the activated ChargedAction's comboIndex on each
	// activation. comboExpireMs is when the chain breaks if not extended; the
	// ActionRunner uses (now < comboExpireMs) at press time to target
	// `comboIndex + 1` instead of restarting at 0.
	public int comboIndex;
	public ulong comboExpireMs;

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
}
