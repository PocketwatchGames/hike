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

	// Composed level (ItemState.level) doubles this weapon's outgoing health
	// damage per level (2^level, so level 0 = ×1). Applied at hit resolution on
	// top of the authored DamageData — melee/hitscan in ItemEventHandlers.ResolveHit,
	// projectiles threaded through Projectile.Launch.
	public float DamageMultiplier => 1 << level;

	// Live block-armor guard pool + its recharge-delay gate. Capacity and
	// recharge tuning live on WeaponData (blockArmor / blockArmorRechargeDelay
	// / blockArmorRechargeSpeed); this is the current pool plus the game-time
	// at which recharge may resume. Only absorbs damage while the player is
	// charging this weapon (Player.OnHurtBoxHit), but recharges continuously
	// once the delay elapses so the guard is ready for the next charge. Starts
	// full so a freshly-equipped weapon guards on its first charge.
	public float blockArmor;
	public ulong blockArmorRechargeStartMs;

	// Arrows this bow has fired that are still recoverable, oldest first.
	// Spans both forms an arrow can take: loose loot on the ground
	// (ArrowLootSimState) and stuck on a mob (ArrowStuck). Each entry returns
	// 1 ammo via OnArrowRemoved when it leaves play (player pickup, central
	// recharge-timer auto-recovery via RecoverOldestArrow, mob removed without
	// dying). The weapon — not the arrow —
	// is the source of truth for ammo bookkeeping, so the binding survives
	// the player dropping the weapon: arrows still return ammo to this
	// WeaponState instance regardless of who's holding it. Runtime-only;
	// not persisted (the weapon itself lives on Inventory, which isn't
	// world-serialized).
	public readonly System.Collections.Generic.List<IWeaponArrow> outstandingArrows = new();
	// Live summoned minions owned by this weapon, oldest first. Runtime-only
	// (not persisted — the weapon lives on Inventory, which isn't world-
	// serialized). Capacity is WeaponData.maxMinions: summoning past it recycles
	// the oldest. Destroyed wholesale when the weapon is unequipped or removed
	// (Inventory hook → DestroyMinions) so minions never outlive their source
	// weapon being put away.
	private readonly System.Collections.Generic.List<Mob> _minions = new();

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
	// any reason (player pickup, central recharge-timer auto-recovery, mob
	// despawn). One arrow → one ammo bump, capped at the authored maxAmmo so
	// a stale reference can't overshoot. The refund is gated on the arrow
	// still being tracked here — DestroyOutstandingArrows relies on that to
	// forfeit ammo (it untracks first, so this no-ops on the refund).
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
		// Topped back off — stop the recharge timer; a full magazine has
		// nothing left to recharge. This is the "player hand-recovered the
		// last arrow while the timer was running" case; the timer re-arms on
		// the next shot. (The auto-recovery path in Player.TickAmmoRecharge
		// re-evaluates the deadline itself, so this clear is harmless there.)
		if (_data != null && ammo >= _data.maxAmmo)
		{
			ammoRechargeReadyMs = 0;
		}
	}

	// Auto-recover the oldest outstanding arrow, returning its 1 ammo to this
	// weapon (the arrow's own removal path calls back into OnArrowRemoved).
	// Driven by the central ammo-recharge timer when it elapses, so a bow
	// refills by reclaiming the arrows it left in the world — oldest first —
	// rather than each arrow self-expiring on its own timer. No-op when no
	// arrows are outstanding (the caller falls back to a direct ammo bump for
	// self-recharging weapons like the bomb).
	public void RecoverOldestArrow()
	{
		if (outstandingArrows.Count == 0)
		{
			return;
		}
		outstandingArrows[0].Recover();
	}

	// Forfeit every arrow this weapon has out in the world — destroy them from
	// the world WITHOUT refunding ammo. Called from Inventory.Remove when the
	// weapon leaves the inventory entirely (dropped, sold, stashed): the in-world
	// ammo is lost on purpose, and the central recharge timer (whose absolute
	// deadline survives the exit) is what climbs the magazine back to full once
	// the weapon is re-acquired. We untrack the
	// arrows FIRST so each one's normal removal path (OnArrowRemoved) sees an
	// already-removed entry and grants nothing — we only want the world-side
	// teardown, not the ammo bump.
	public void DestroyOutstandingArrows()
	{
		if (outstandingArrows.Count == 0)
		{
			return;
		}
		IWeaponArrow[] forfeit = outstandingArrows.ToArray();
		outstandingArrows.Clear();
		foreach (IWeaponArrow arrow in forfeit)
		{
			arrow.Recover();
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

	// Register a freshly-summoned minion, recycling the oldest live minion(s)
	// first so the count never exceeds WeaponData.maxMinions. Dead/stale entries
	// (a minion that self-drained to death) are pruned first. Subscribes to the
	// minion's TreeExiting so the reference drops when it leaves the tree on its
	// own (self-drain death, chunk eviction) — no double-free or ghost recycle.
	public void AddMinion(Mob minion)
	{
		if (minion == null)
		{
			return;
		}
		PruneDeadMinions();
		int cap = _data != null && _data.maxMinions > 0 ? _data.maxMinions : 1;
		while (_minions.Count >= cap && _minions.Count > 0)
		{
			Mob oldest = _minions[0];
			_minions.RemoveAt(0);
			if (Godot.GodotObject.IsInstanceValid(oldest))
			{
				oldest.Despawn();
			}
		}
		_minions.Add(minion);
		minion.TreeExiting += () => _minions.Remove(minion);
	}

	// Destroy every live minion this weapon owns. Called from Inventory when the
	// weapon is unequipped or removed from inventory, so a summoner's minions
	// vanish with the weapon being put away. Untracks first so the per-minion
	// TreeExiting closure no-ops.
	public void DestroyMinions()
	{
		if (_minions.Count == 0)
		{
			return;
		}
		Mob[] doomed = _minions.ToArray();
		_minions.Clear();
		foreach (Mob minion in doomed)
		{
			if (Godot.GodotObject.IsInstanceValid(minion))
			{
				minion.Despawn();
			}
		}
	}

	private void PruneDeadMinions()
	{
		for (int i = _minions.Count - 1; i >= 0; i--)
		{
			if (!Godot.GodotObject.IsInstanceValid(_minions[i]))
			{
				_minions.RemoveAt(i);
			}
		}
	}
}
