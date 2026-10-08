public class ShieldState : ItemState
{
	// Live guard pool and the game-time at which its recharge may resume. Starts
	// full so a freshly-equipped shield guards the first crouch. Not saved: a load
	// is a dawn wake, and the guard restarts on its own.
	public float guard;
	public ulong guardRechargeStartMs;

	// Composed level (ItemState.level) doubles the parry cap and counter-strike
	// per level (2^level), the same curve WeaponState.DamageMultiplier uses.
	public float LevelMultiplier => 1 << level;

	public override ShieldData data => _data;
	private readonly ShieldData _data;

	public ShieldState(ShieldData d) : base(d)
	{
		_data = d;
		guard = d?.guardArmor ?? 0f;
	}
}
