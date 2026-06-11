public class WeaponState : ItemState
{
	public int ammo;

	// Game-time (ms) at which the next passive ammo charge completes, for
	// weapons with WeaponData.ammoRechargeSeconds > 0. 0 = no recharge in
	// flight (either at max ammo, or auto-recharge disabled). Player.TickAmmoRecharge
	// arms it the frame ammo drops below max and re-arms after each refill.
	public ulong ammoRechargeReadyMs;

	// Combo runtime — set to the activated ItemAction's comboIndex on each
	// activation. comboExpireMs is when the chain breaks if not extended; the
	// ActionRunner uses (now < comboExpireMs) at press time to target
	// `comboIndex + 1` instead of restarting at 0.
	public int comboIndex;
	public ulong comboExpireMs;

	public int exp;
	public int level;

	// Live block-armor guard pool + its recharge-delay gate. Capacity and
	// recharge tuning live on WeaponData (blockArmor / blockArmorRechargeDelay
	// / blockArmorRechargeSpeed); this is the current pool plus the game-time
	// at which recharge may resume. Only absorbs damage while the player is
	// charging this weapon (Player.OnHurtBoxHit), but recharges continuously
	// once the delay elapses so the guard is ready for the next charge. Starts
	// full so a freshly-equipped weapon guards on its first charge.
	public float blockArmor;
	public ulong blockArmorRechargeStartMs;

	// Arrows this bow has fired that are still recoverable. Spans both
	// forms an arrow can take: loose loot on the ground (ArrowLootSimState)
	// and stuck on a mob (ArrowStuck). Each entry returns 1 ammo via
	// OnArrowRemoved when it leaves play (player pickup, removeTimeMs
	// timeout, mob removed without dying). The weapon — not the arrow —
	// is the source of truth for ammo bookkeeping, so the binding survives
	// the player dropping the weapon: arrows still return ammo to this
	// WeaponState instance regardless of who's holding it. Runtime-only;
	// not persisted (the weapon itself lives on Inventory, which isn't
	// world-serialized).
	public readonly System.Collections.Generic.List<IWeaponArrow> outstandingArrows = new();
	public override WeaponData data => _data;
	private readonly WeaponData _data;

	public WeaponState(WeaponData d) : base(d)
	{
		_data = d;
		if (_data != null)
		{
			ammo = _data.maxAmmo;
			blockArmor = _data.blockArmor;
		}
	}

	// Registers a freshly-spawned arrow with this weapon. The caller is
	// responsible for invoking OnArrowRemoved (typically when its own
	// "leaving play" event fires) so the ammo bump is uniform across
	// removal causes.
	public void RegisterArrow(IWeaponArrow arrow)
	{
		if (arrow == null)
		{
			return;
		}
		outstandingArrows.Add(arrow);
	}

	// Called by IWeaponArrow implementations when the arrow leaves play for
	// any reason (player pickup, timeout, mob despawn). One arrow → one
	// ammo bump, capped at the authored maxAmmo so a stale reference can't
	// overshoot.
	public void OnArrowRemoved(IWeaponArrow arrow)
	{
		if (!outstandingArrows.Remove(arrow))
		{
			return;
		}
		if (_data != null && ammo < _data.maxAmmo)
		{
			ammo++;
		}
	}

	// Removes the arrow from the outstanding list WITHOUT bumping ammo.
	// Used when an arrow transitions between forms (stuck on a mob
	// becoming loose loot when the mob dies) — the new form re-registers
	// separately, so the net ammo accounting stays balanced.
	public void DetachArrow(IWeaponArrow arrow)
	{
		if (arrow == null)
		{
			return;
		}
		outstandingArrows.Remove(arrow);
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
