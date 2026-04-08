public class WeaponState
{
	public WeaponData data;
	public ulong cooldownTime;
	public int ammo;
	public int lastWeaponEventIndex = -1;

	public WeaponState(WeaponData data)
	{
		this.data = data;
		ammo = data.maxAmmo;
	}
}
