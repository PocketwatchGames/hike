public class WeaponState
{
	public WeaponData data;
	public ulong cooldownTime;
	public int ammo;

	public WeaponState(WeaponData data)
	{
		this.data = data;
	}
}
