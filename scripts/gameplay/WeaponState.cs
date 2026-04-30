public class WeaponState : ItemState
{
	public int ammo;

	// Combo runtime — incremented when a combos=true tier activates within
	// the previous combo'd activation's window; reset to 0 by any non-combo
	// activation. Read by the ComboBonus event handler and by combo'd
	// tier.events that scale by index. comboExpireMs is when the chain
	// breaks if not extended.
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
