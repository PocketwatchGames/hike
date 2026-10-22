using Godot;

public class WeaponState : GearState
{
	// A broken right-hand weapon can't fire at all; a broken melee weapon still
	// swings its uncharged tiers (ActionRunner.IsBrokenGear).
	public bool CanFire => !(IsBroken && _data != null && _data.rightHandSlot);

	// The world object standing in for this weapon while it is out of the
	// wielder's hand (a harpoon on its line). While set, the held model hides and
	// no action of this weapon starts. A node rather than a flag so that every way
	// the object leaves play hands the weapon back. Runtime-only.
	public Node3D outOfHandObject;
	public bool IsOutOfHand => outOfHandObject != null && GodotObject.IsInstanceValid(outOfHandObject);

	// Combo runtime. Two independent chains, each with its own position + window
	// (when the next press lands before the window, the chain continues; after
	// it, the chain restarts at 0). Kept separate so activating one doesn't
	// spuriously advance the other:
	//   comboIndex  / comboExpireMs  — tier-chain (author one ItemAction tier per
	//                 step); ActionRunner targets `comboIndex + 1` while open.
	//   repeatIndex / repeatExpireMs — repeat-swing cursor within a single
	//                 repeating tier (ItemAction.repeatActionOverrides); advances
	//                 one swing per press while open, wraps to 0 after the last.
	// Set at activation in ActionRunner.EnterActive; cleared by ResetCombo()
	// (getting hit). A non-repeat tier firing closes the repeat window on its own.
	public int comboIndex;
	public ulong comboExpireMs;
	public int repeatIndex;
	public ulong repeatExpireMs;

	// Break both combo chains immediately — the next press starts fresh at
	// swing 0. Called when the wielder is hit.
	public void ResetCombo()
	{
		comboIndex = 0;
		comboExpireMs = 0;
		repeatIndex = 0;
		repeatExpireMs = 0;
	}

	// Composed level (ItemState.level) doubles this weapon's outgoing health
	// damage per level (2^level, so level 0 = ×1). Applied at hit resolution on
	// top of the authored DamageData — melee/hitscan in ItemEventHandlers.ResolveHit,
	// projectiles threaded through Projectile.Launch.
	public float DamageMultiplier => 1 << level;

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
