using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	public void Heal(float amount)
	{
		if (amount <= 0f)
		{
			return;
		}
		// Healing climbs all the way to MaxHealth regardless of any
		// outstanding blood drain — a potion brings you to full even
		// while a spell's HP debt is still pending. Any drain the heal
		// climbs into is forgiven (the invariant `Health + DrainedHealth
		// <= MaxHealth` is restored), since the bar's dark region
		// represents debt that would be repaid into bright HP — and
		// you've already paid yourself up to the cap.
		float before = _health;
		_health = Mathf.Min(MaxHealth, _health + amount);
		_drainedHealth = Mathf.Min(_drainedHealth, Mathf.Max(0f, MaxHealth - _health));
		float restored = _health - before;
		if (restored > 0f)
		{
			GameClient.Current?.onHeal?.Invoke(GlobalPosition, restored, EHudTextType.HealLight);
		}
	}

	// IActionActor — press-time blood gate. Non-mutating peek. Costs of 0
	// or less always pass; otherwise refuses when the cost would drop HP
	// to 0, so a drain can never kill the actor directly.
	public bool HasBlood(float amount)
	{
		if (amount <= 0f)
		{
			return true;
		}
		return _health > amount;
	}

	// IActionActor — unconditional spend at EnterActive. Subtracts from
	// current HP, adds to _drainedHealth, and re-arms the single shared
	// regen delay (PlayerData.bloodRegenDelay). Mirrors armor: every
	// drain pushes _bloodRegenStartMs forward so chained spells hold
	// regen back until the player stops drawing.
	public void DrainBlood(float amount)
	{
		if (amount <= 0f || data == null)
		{
			return;
		}
		_health -= amount;
		_drainedHealth += amount;
		ulong now = _world?.GameTimeMs ?? 0;
		_bloodRegenStartMs = now + (ulong)(data.bloodRegenDelay * 1000f);
	}

	// Per-tick refund. No-op while _drainedHealth is empty or before the
	// shared delay elapses; otherwise pays back bloodRegenSpeed * dt to
	// _health and shrinks _drainedHealth by the same amount so the bright
	// and dark HUD zones meet seamlessly.
	private void TickBloodDrain(float dt)
	{
		if (_drainedHealth <= 0f || data == null)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (now < _bloodRegenStartMs)
		{
			return;
		}
		float refund = Mathf.Min(_drainedHealth, data.bloodRegenSpeed * dt);
		_drainedHealth -= refund;
		_health = Mathf.Min(MaxHealth, _health + refund);
	}

	// Sums the hosted member's innate class armor with maxArmor across every
	// equipped armor slot. Current armor is capped at the new max — unequipping
	// a piece can only shrink the available pool, it never grants free armor.
	// Increases leave the current value alone so the recharge logic owns the
	// climb back up to the new max.
	private void RecalculateMaxArmor()
	{
		float total = Member?.maxArmor ?? 0f;
		if (_inventory != null)
		{
			AccumulateArmor(EInventorySlot.Helmet, ref total);
			AccumulateArmor(EInventorySlot.Armor, ref total);
		}
		_maxArmor = total;
		if (_armor > _maxArmor)
		{
			_armor = _maxArmor;
		}
	}

	private void AccumulateArmor(EInventorySlot slot, ref float total)
	{
		if (_inventory.GetEquipped(slot) is ArmorState armor && armor.data != null)
		{
			total += armor.EffectiveMaxArmor;
		}
	}

	// Compose a single stat across inherent PlayerData modifiers, the hosted
	// member's class modifiers, equipped armor modifiers, and active
	// status-effect modifiers. Seeds with the stat's neutral identity (1 for
	// multiplicative, 0 for additive) and folds each source. Multiplicative for
	// most stats, additive for the four additive ones (Camouflage / MaxStamina /
	// ColdResist / HeatResist) per StatModifierUtil.IsAdditive.
	public float ComposeStat(EStat stat)
	{
		float value = StatModifierUtil.NeutralValue(stat);
		if (data?.modifiers != null)
		{
			value = StatModifierUtil.Fold(stat, data.modifiers, value);
		}
		if (Member?.modifiers != null)
		{
			value = StatModifierUtil.Fold(stat, Member.modifiers, value);
		}
		value = AccumulateArmorStat(EInventorySlot.Helmet, stat, value);
		value = AccumulateArmorStat(EInventorySlot.Armor, stat, value);
		value = _statusEffects?.FoldStat(stat, value) ?? value;
		value *= MemberStat(stat);
		return value;
	}

	// The hosted party member's multiplicative contribution to a composed
	// stat (1 = neutral, or no member hosted). Folded into ComposeStat /
	// ComposeMaskMul so the character sheet rides the same pipeline as gear
	// and status modifiers. Returns 1 for every stat the sheet doesn't map
	// (including all additive stats), so multiplying it in is always safe.
	// Melee strength is applied at the hit site (ItemEventHandlers.ResolveHit),
	// not here; health/stamina are pool bases, not composed stats.
	private float MemberStat(EStat stat)
	{
		if (Member is not PlayerState m)
		{
			return 1f;
		}
		switch (stat)
		{
			case EStat.Vision:
			case EStat.Hearing:
				return m.perception;
			case EStat.Noise:
			case EStat.Scent:
				// Higher stealth = quieter / less scent, so it divides the
				// louder-is-higher Noise/Scent multipliers.
				return m.stealth > 0f ? 1f / m.stealth : 1f;
			case EStat.FortitudeResistance:
				// Higher fortitude = smaller buildup multiplier = more resistant.
				return m.fortitude > 0f ? 1f / m.fortitude : 1f;
			default:
				return 1f;
		}
	}

	// Multiplicative compose across all sources for a tag mask — used at
	// hit-application sites (damage / armor-penetration chance / blunt chip / knockback
	// magnitude). Walks every entry whose single-bit stat overlaps the mask
	// and multiplies. The StatusEffectController routes through this
	// callback when scaling buildup contributions and DoT damage ticks.
	public float ComposeMaskMul(EStat mask)
	{
		float product = 1f;
		if (data?.modifiers != null)
		{
			product = StatModifierUtil.FoldMask(mask, data.modifiers, product);
		}
		if (Member?.modifiers != null)
		{
			product = StatModifierUtil.FoldMask(mask, Member.modifiers, product);
		}
		product = AccumulateArmorMask(EInventorySlot.Helmet, mask, product);
		product = AccumulateArmorMask(EInventorySlot.Armor, mask, product);
		product = _statusEffects?.FoldMask(mask, product) ?? product;
		// Member-sheet contribution for the mask path. Only FortitudeResistance
		// (the combat-buildup channel) rides ComposeMaskMul today — the sense
		// stats are single-stat composes via ComposeStat.
		if ((mask & EStat.FortitudeResistance) != 0)
		{
			product *= MemberStat(EStat.FortitudeResistance);
		}
		return product;
	}

	// Maps a data-authored ETraitCondition to this player's live state for the
	// conditional-modifier fold (ConditionalModifierData). Handed to the
	// StatusEffectController as its condition evaluator, it's consulted at
	// stat-compose time so a trait's situational bonus blinks on/off with the
	// condition without churning the effect list. Unknown conditions read false.
	// `_evaluatingStaminaCondition` guards the StaminaBelowFraction branch: it reads
	// MaxStamina, which composes status effects (a conditional MaxStamina trait like
	// Empathetic among them) and could otherwise recurse; on reentry we treat the
	// condition as unmet rather than loop.
	private bool _evaluatingStaminaCondition;

	private bool EvaluateTraitCondition(ETraitCondition condition, float parameter)
	{
		switch (condition)
		{
			case ETraitCondition.StaminaBelowFraction:
				if (_evaluatingStaminaCondition)
				{
					return false;
				}
				_evaluatingStaminaCondition = true;
				float max = MaxStamina;
				_evaluatingStaminaCondition = false;
				return max > 0f && _stamina < max * parameter;
			case ETraitCondition.PartyMemberFallen:
				Party party = _world?.WorldState?.SimState?.Party;
				return party != null && party.AliveCount < party.Count;
			default:
				return false;
		}
	}

	private float AccumulateArmorStat(EInventorySlot slot, EStat stat, float value)
	{
		if (_inventory == null) { return value; }
		if (_inventory.GetEquipped(slot) is ArmorState armor && armor.data?.modifiers != null)
		{
			value = StatModifierUtil.Fold(stat, armor.data.modifiers, value);
		}
		return value;
	}

	private float AccumulateArmorMask(EInventorySlot slot, EStat mask, float product)
	{
		if (_inventory == null) { return product; }
		if (_inventory.GetEquipped(slot) is ArmorState armor && armor.data?.modifiers != null)
		{
			product = StatModifierUtil.FoldMask(mask, armor.data.modifiers, product);
		}
		return product;
	}

	// Composite cold / heat resistance from every equipped armor piece plus
	// every active status effect. Used by the temperature path to shift the
	// cold/hot trigger thresholds and by the inventory's player-stats panel
	// to display the resolved total.
	public void GetThermalResistances(out float coldResistance, out float heatResistance)
	{
		coldResistance = ComposeStat(EStat.ColdResist);
		heatResistance = ComposeStat(EStat.HeatResist);
	}

	// Composite sense stats from every equipped armor piece plus every
	// active status effect. Camouflage is an additive sum (0 = neutral);
	// the four sense modifiers are multiplicative products (1.0 = neutral).
	// Callers fold the multipliers into a PlayerData base value when an
	// effective absolute is wanted; the inventory stats panel just renders
	// them as signed deltas off neutral.
	public void GetSenseStats(out float camouflage, out float visionMultiplier, out float hearingMultiplier, out float noiseMultiplier, out float scentMultiplier)
	{
		camouflage = ComposeStat(EStat.Camouflage);
		visionMultiplier = ComposeStat(EStat.Vision);
		hearingMultiplier = ComposeStat(EStat.Hearing);
		noiseMultiplier = ComposeStat(EStat.Noise);
		scentMultiplier = ComposeStat(EStat.Scent);
	}

	// Composite movement multiplier from every active status effect. Doesn't
	// include armor — armor doesn't carry a speed modifier in the current
	// model. Cold and similar effects multiply in here.
	public float SpeedMultiplier => _statusEffects?.FoldStat(EStat.MoveSpeed, 1f) ?? 1f;

	// Pushes the armor recharge window out whenever damage actually touches
	// armor — a direct hit OR a status DoT that chips it (e.g. burn). Damage
	// that fully bypasses armor (poison / heals, armorPenetration=1) never gets
	// here, so it can't stall recovery. `hasArmorLeft` true => a chip landed but
	// armor survived, use the short delay; false => armor is (or already was)
	// empty, use the long recover window and fire the depleted one-shot on the
	// transition. Called even when armor was already at zero so sustained armor
	// damage keeps the recover window from starting mid-fight.
	private void RefreshArmorRecharge(bool hasArmorLeft)
	{
		ulong now = _world?.GameTimeMs ?? 0;
		if (hasArmorLeft)
		{
			_armorDepleted = false;
			_armorRechargeStartMs = now + (ulong)(data.armorRechargeDelay * 1000f);
		}
		else
		{
			if (!_armorDepleted)
			{
				_armorDepleted = true;
				SpawnWorldEffect(_armorDepletedFx);
			}
			_armorRechargeStartMs = now + (ulong)(data.armorRecoverTime * 1000f);
		}
		_armorRecharging = false;
	}

	private void TickArmor(float dt)
	{
		// MaxArmor (not the raw _maxArmor equipment sum) so a MaxArmor stat
		// modifier actually expands the rechargeable pool, not just the readout.
		float maxArmor = MaxArmor;
		if (maxArmor <= 0f || _armor >= maxArmor)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (now < _armorRechargeStartMs)
		{
			return;
		}
		if (!_armorRecharging)
		{
			_armorRecharging = true;
			SpawnWorldEffect(_armorDepleted ? _armorRecoverStartFx : _armorRechargeStartFx);
		}
		// Rate derived from the current max so a full refill always takes
		// armorRechargeTime seconds, whatever armor the player has equipped (and
		// so a +MaxArmor buff doesn't slow the refill). armorRechargeDelay /
		// armorRecoverTime stay as flat timing.
		float rechargeTime = data.armorRechargeTime;
		float speed = rechargeTime > 0f ? maxArmor / rechargeTime : 0f;
		_armor = Mathf.Min(maxArmor, _armor + speed * dt);
		if (_armor >= maxArmor)
		{
			_armorDepleted = false;
		}
	}

	// The weapon whose block-armor guard is currently live — non-null only
	// while the player is sneaking and the equipped melee weapon carries a
	// block pool. Sneaking is a defensive crouch that doubles as a guard
	// stance; the guard "active" (absorbs damage) only while sneaking, but
	// still recharges between crouches (TickBlockArmor) so it's topped up for
	// the next block.
	private WeaponState GetSneakBlockWeapon()
	{
		if (!_sneaking)
		{
			return null;
		}
		// Re-engage cooldown after stopping a block: the guard is down (no soak,
		// no parry) even while re-crouched until it elapses.
		if ((_world?.GameTimeMs ?? 0) < _blockCooldownEndMs)
		{
			return null;
		}
		// A "guard" is a melee weapon that can either soak damage passively
		// (blockArmor) or parry (maxParryDamage). Either qualifies, so a knife
		// with no passive block still guards for the sake of the parry.
		if (_inventory?.GetEquipped(EInventorySlot.WeaponMelee) is WeaponState weapon
			&& weapon.data != null
			&& (weapon.data.blockArmor > 0f || WeaponCanParry(weapon.data)))
		{
			return weapon;
		}
		return null;
	}

	// True while a well-timed parry would deflect an incoming blow: the sneak
	// guard is up, the crouch's parry window is still open, and the guard is off
	// its recharge cooldown — the same gate OnHurtBoxHit checks (minus the
	// per-hit damage cap). Read by the HUD to tint the block bar during the window.
	public bool IsParryWindowActive
	{
		get
		{
			if (_parryDeadlineMs == 0 || (_world?.GameTimeMs ?? 0) >= _parryDeadlineMs)
			{
				return false;
			}
			WeaponState guard = GetSneakBlockWeapon();
			return guard != null && IsGuardReadyToParry(guard);
		}
	}

	// A weapon parries only if it authors both a window (parryTimeMs) and a
	// non-zero negation cap (maxParryDamage).
	private static bool WeaponCanParry(WeaponData data)
	{
		return data != null && data.parryTimeMs > 0 && data.maxParryDamage > 0f;
	}

	// Opens the parry window on the frame a sneak-block begins (rising edge of
	// _sneaking, from ProcessInput). A parry-capable melee weapon arms a
	// GameTimeMs deadline; a well-timed block before it elapses fully negates
	// the blow and counter-strikes (OnHurtBoxHit → TryParry). A weapon that only
	// blocks passively leaves the window closed — it soaks but never parries.
	private void BeginSneakBlock()
	{
		_parryDeadlineMs = 0;
		// Guard-up cue, only when the crouch raises a live guard — a crouch
		// inside the re-engage cooldown (or with no guarding weapon) stays
		// silent so the cue reliably means "the block is up".
		if (GetSneakBlockWeapon() != null)
		{
			SpawnWorldEffect(_blockStartFx);
		}
		if (_inventory?.GetEquipped(EInventorySlot.WeaponMelee) is WeaponState weapon
			&& WeaponCanParry(weapon.data))
		{
			_parryDeadlineMs = (_world?.GameTimeMs ?? 0) + (ulong)weapon.data.parryTimeMs;
		}
	}

	// Fires the subtle "too late to parry" cue on the tick the parry window
	// elapses, consuming the deadline so it's a one-shot (a successful parry
	// zeroed it already). Silent when the guard isn't up at expiry — the player
	// stood mid-window, or crouched during the re-engage cooldown and the
	// guard never rose.
	private void TickParryWindow()
	{
		if (_parryDeadlineMs == 0 || (_world?.GameTimeMs ?? 0) < _parryDeadlineMs)
		{
			return;
		}
		bool guardUp = GetSneakBlockWeapon() != null;
		_parryDeadlineMs = 0;
		if (guardUp)
		{
			SpawnWorldEffect(_parryWindowEndFx);
		}
	}

	// Effective parry cap for `weapon`: the authored maxParryDamage scaled by
	// the same per-level multipliers the weapon's own hits enjoy — its composed
	// item level (DamageMultiplier, 2^level) and the Melee forge upgrade's
	// shared curve (OutgoingLevelScale) — so an upgraded weapon deflects
	// proportionally bigger blows, keeping parry viable against higher-level
	// mobs whose damage rides the same curve.
	private float EffectiveMaxParryDamage(WeaponState weapon)
	{
		if (weapon?.data == null || weapon.data.maxParryDamage <= 0f)
		{
			return 0f;
		}
		return weapon.data.maxParryDamage * weapon.DamageMultiplier * OutgoingLevelScale(EInventorySlot.WeaponMelee);
	}

	// Whether `weapon`'s guard is off its recharge cooldown and so free to
	// parry. blockArmorRechargeStartMs is the game-time at which the guard's
	// recharge may resume — pushed into the future by every guard-touching hit
	// and by each parry (SpendParryGuard) — so the parry is gated by the same
	// delay the passive pool recharges on. A weapon never hit yet has the
	// timestamp at 0 (in the past), so its first parry is immediately available.
	private bool IsGuardReadyToParry(WeaponState weapon)
	{
		return weapon != null && (_world?.GameTimeMs ?? 0) >= weapon.blockArmorRechargeStartMs;
	}

	// Consume the guard on a successful parry: re-arm the block recharge delay so
	// the next parry (and the passive pool's own recharge) waits out
	// blockArmorRechargeDelay. This is the whole coupling to the block system —
	// the parry spends no pool amount (it negated the hit itself, not via the
	// pool), only the guard's readiness.
	private void SpendParryGuard(WeaponState weapon)
	{
		if (weapon?.data == null)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		weapon.blockArmorRechargeStartMs = now + (ulong)(weapon.data.blockArmorRechargeDelay * 1000f);
	}

	// Routes the armor-touchable slice of an incoming hit through the charging
	// weapon's guard before the player's central armor. Overflow model: the
	// guard absorbs up to its remaining charge and passes only the UNABSORBED
	// OVERFLOW on to central armor — it never re-applies the whole slice
	// downstream, so a partial block genuinely reduces what armor/health take.
	// `absorbable` is lowered by the absorbed damage-equivalent (0 if the guard
	// soaked it all) and the return is the pool amount consumed (> 0 whenever
	// the guard stopped anything, so the caller shows "BLOCKED!" on any partial
	// block). The guard recharge follows the same rule as central armor: any
	// guard-touchable hit (absorbable > 0) resets the recharge delay — even one
	// that lands while the pool is already empty — while a fully-bypassing hit
	// (poison / armor-penetrating, absorbable == 0) never touches the guard and
	// so leaves its recovery alone. The guard only engages while the player is
	// sneaking (null weapon no-ops here), so a player not actively guarding
	// never resets it.
	private float AbsorbWeaponBlock(WeaponState weapon, ref float absorbable, float blunt)
	{
		if (weapon == null || absorbable <= 0f)
		{
			return 0f;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		weapon.blockArmorRechargeStartMs = now + (ulong)(weapon.data.blockArmorRechargeDelay * 1000f);
		if (weapon.blockArmor <= 0f)
		{
			return 0f;
		}
		float blockDamage = absorbable * (1f + blunt);
		if (blockDamage <= weapon.blockArmor)
		{
			// Guard soaks the whole slice.
			weapon.blockArmor -= blockDamage;
			absorbable = 0f;
			return blockDamage;
		}
		// Guard depletes: it absorbs what it can and only the overflow
		// continues. Convert the consumed pool back to damage units (undo the
		// blunt multiplier) so the leftover `absorbable` is the true damage
		// that got past the guard.
		float absorbed = weapon.blockArmor;
		absorbable -= absorbed / (1f + blunt);
		weapon.blockArmor = 0f;
		return absorbed;
	}

	// Per-tick recharge of every equipped weapon's block-armor guard. Mirrors
	// TickArmor but keyed off the weapon's own (independent) recharge stats and
	// driven for both weapon slots so a guard refills whether or not it's the
	// one being charged. No depletion fx — the HUD bar carries the feedback.
	private void TickBlockArmor(float dt)
	{
		ulong now = _world?.GameTimeMs ?? 0;
		TickWeaponBlockArmor(_inventory?.GetEquipped(EInventorySlot.WeaponMelee) as WeaponState, now, dt);
		TickWeaponBlockArmor(_inventory?.GetEquipped(EInventorySlot.WeaponRanged) as WeaponState, now, dt);
	}

	private static void TickWeaponBlockArmor(WeaponState weapon, ulong now, float dt)
	{
		if (weapon?.data == null)
		{
			return;
		}
		float max = weapon.data.blockArmor;
		if (max <= 0f || weapon.blockArmor >= max)
		{
			return;
		}
		if (now < weapon.blockArmorRechargeStartMs)
		{
			return;
		}
		// Rate derived from the pool size so a full refill takes
		// blockArmorRechargeTime seconds regardless of the guard's capacity.
		float rechargeTime = weapon.data.blockArmorRechargeTime;
		float speed = rechargeTime > 0f ? max / rechargeTime : 0f;
		weapon.blockArmor = Mathf.Min(max, weapon.blockArmor + speed * dt);
	}

	// Per-tick ammo recharge for every weapon the player owns that opts in
	// (WeaponData.ammoRechargeSeconds > 0) — the single, unified ammo timer.
	// Driven for both equip slots AND the backpack so an unequipped weapon
	// keeps reclaiming arrows / refilling while stashed. A weapon dropped on
	// the ground isn't ticked at all, but ammoRechargeReadyMs is an absolute
	// game-time deadline, so the first tick after it re-enters the inventory
	// catches up every interval that elapsed while it was gone (see the
	// catch-up loop in TickWeaponAmmoRecharge). The timer is a single deadline
	// (ammoRechargeReadyMs): armed the frame ammo drops below max, advanced
	// after each unit refills, and cleared at full — so it runs continuously
	// while below max and firing never resets an in-flight charge.
	// On each elapse it recovers one unit of ammo: a weapon that left arrows in
	// the world (the bow) auto-reclaims its oldest outstanding arrow (which
	// bumps ammo as it leaves play); a self-recharging weapon with no arrows
	// (the bomb) just regenerates ammo from nothing.
	// Destroys any owned item whose removeOnDay deadline has been reached —
	// time-limited items (e.g. the fairy corpse) that expire at the next
	// sleep-to-sunrise, wherever they sit: backpack, hotbar, or an equipped slot.
	// Collect-then-remove so
	// Inventory.Remove (which mutates slots and fires onChanged) isn't called
	// mid-enumeration.
	private void TickItemExpiry()
	{
		if (_inventory == null)
		{
			return;
		}
		int today = _world?.DayNumber ?? 0;
		System.Collections.Generic.List<ItemState> expired = null;
		foreach (ItemState item in _inventory.EnumerateAll())
		{
			// Prune spoiled food cohorts in place — a half-spoiled pile loses only
			// its old batch and survives on its fresher ones. The whole item is
			// pulled only when its last cohort is gone, or when a non-food timed
			// drop (removeOnDay lifespan, e.g. a fairy corpse) reaches its day.
			item.PruneExpired(today);
			bool emptied = item.stackCount <= 0;
			bool lifespanElapsed = item.removeOnDay != 0 && today >= item.removeOnDay;
			if (emptied || lifespanElapsed)
			{
				(expired ??= new System.Collections.Generic.List<ItemState>()).Add(item);
			}
		}
		if (expired == null)
		{
			return;
		}
		foreach (ItemState item in expired)
		{
			_inventory.Remove(item);
		}
		// An expired equipped piece leaves its slot empty — backfill weapon/armor
		// slots from the member's starting loadout so the player is never stranded
		// barehanded or unarmored at sunrise.
		RefillEmptyEquipmentFromStarting();
	}

	private void TickAmmoRecharge(ulong now)
	{
		if (_inventory == null)
		{
			return;
		}
		TickWeaponAmmoRecharge(_inventory.GetEquipped(EInventorySlot.WeaponMelee) as WeaponState, now);
		TickWeaponAmmoRecharge(_inventory.GetEquipped(EInventorySlot.WeaponRanged) as WeaponState, now);
		// Unequipped weapons keep their recharge timers running so a holstered
		// bow still reclaims its outstanding arrows (and a stashed bomb still
		// refills). Equipped weapons live in the slot pointers only — they're
		// not duplicated in the backpack — so this loop can't double-tick them.
		// Indexed access over Backpack avoids per-frame enumerator allocation.
		System.Collections.Generic.IReadOnlyList<ItemState> backpack = _inventory.Backpack;
		for (int i = 0; i < backpack.Count; i++)
		{
			TickWeaponAmmoRecharge(backpack[i] as WeaponState, now);
		}
	}

	private static void TickWeaponAmmoRecharge(WeaponState weapon, ulong now)
	{
		if (weapon?.data == null)
		{
			return;
		}
		float per = weapon.data.ammoRechargeSeconds;
		int max = weapon.data.maxAmmo;
		if (per <= 0f || max <= 0)
		{
			return;
		}
		if (weapon.ammo >= max)
		{
			// At capacity — clear the deadline so the next depletion arms a
			// fresh full interval rather than refilling instantly.
			weapon.ammoRechargeReadyMs = 0;
			return;
		}
		ulong interval = (ulong)(per * 1000f);
		if (interval == 0)
		{
			interval = 1;
		}
		if (weapon.ammoRechargeReadyMs == 0)
		{
			weapon.ammoRechargeReadyMs = now + interval;
			return;
		}
		// Catch up one unit per elapsed interval. Normally the deadline is at
		// most one interval in the past (per-frame ticking), so this loops once.
		// But a weapon that went unticked — dropped on the ground, where it
		// neither ticks nor holds any outstanding arrows — credits the whole
		// elapsed time the instant it re-enters the inventory, advancing the
		// deadline by a fixed interval each step so no time is dropped. Bounded
		// by maxAmmo iterations (ammo strictly climbs to the cap).
		while (now >= weapon.ammoRechargeReadyMs && weapon.ammo < max)
		{
			// Recover one unit. Prefer reclaiming the oldest arrow still in the
			// world (an equipped/holstered bow) — its removal routes back through
			// OnArrowRemoved and bumps ammo; otherwise regenerate directly (the
			// bomb, or a dropped bow whose arrows were forfeit). Net +1 either way.
			if (weapon.outstandingArrows.Count > 0)
			{
				weapon.RecoverOldestArrow();
			}
			else
			{
				weapon.ammo++;
			}
			weapon.ammoRechargeReadyMs += interval;
		}
		// Clear the deadline at full so the next depletion arms a fresh interval
		// and the HUD / press-gate see a stable count.
		if (weapon.ammo >= max)
		{
			weapon.ammoRechargeReadyMs = 0;
		}
	}

	// IActionActor — press-time stamina gate. Non-mutating peek. Costs of 0
	// or less always pass.
	public bool HasStamina(float amount)
	{
		if (amount <= 0f)
		{
			return true;
		}
		return _stamina >= amount;
	}

	// IActionActor — unconditional spend at EnterActive. Allowed to drive
	// stamina negative; sprint / swim gating already keys off `_stamina <= 0`
	// and the recharge tick re-fills from negative without special handling.
	// Arms the recharge delay so a heavy action doesn't begin refilling
	// immediately after firing.
	public void ConsumeStamina(float amount)
	{
		if (amount <= 0f)
		{
			return;
		}
		_stamina -= amount;
		ulong now = _world?.GameTimeMs ?? 0;
		_staminaRechargeStartMs = now + (ulong)(data.staminaRechargeDelay * 1000f);
	}

	// IActionActor — refill stamina from the stamina-on-hit weapon mod. Clamped at
	// the current cap. Unlike ConsumeStamina it leaves the recharge delay alone, so
	// a landed hit tops up without stalling the passive refill.
	public void RestoreStamina(float amount)
	{
		if (amount <= 0f)
		{
			return;
		}
		_stamina = Mathf.Min(MaxStamina, _stamina + amount);
	}

	private void TickStamina(float dt)
	{
		float max = MaxStamina;
		// A status effect with maxStaminaBonus can shrink the cap when it
		// expires (e.g. Hydrated wearing off). Clamp before the recharge
		// early-out so a higher-than-cap value comes back down to the new max.
		if (_stamina > max)
		{
			_stamina = max;
		}
		if (max <= 0f || _stamina >= max)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (now < _staminaRechargeStartMs)
		{
			return;
		}
		// staminaRechargeTime is the 0-to-full duration; convert to a flat
		// per-second rate. A partial spend then refills proportionally faster.
		float rechargeTime = data.staminaRechargeTime;
		float rate = rechargeTime > 0f ? max / rechargeTime : max;
		_stamina = Mathf.Min(max, _stamina + rate * dt);
	}

	// Swimming + active move input drains stamina at a flat per-second rate
	// and re-arms the recharge delay each tick (mirrors the dash pattern:
	// spend is unconditional, stamina is allowed to go negative, movement is
	// never gated on it).
	private void TickSwimStamina(float dt)
	{
		if (data == null || _waterState != EWaterState.Swimming)
		{
			return;
		}
		if (_inputMove.LengthSquared() <= 0.0001f)
		{
			return;
		}
		_stamina -= data.swimStaminaDrainPerSecond * dt;
		ulong now = _world?.GameTimeMs ?? 0;
		_staminaRechargeStartMs = now + (ulong)(data.staminaRechargeDelay * 1000f);
	}

	// Mirrors TickSwimStamina. Sprint drains a flat per-second amount and
	// re-arms the recharge delay each tick (stamina is allowed to go
	// negative; movement is never gated on it, but UpdateSprintState ends
	// sprint as soon as stamina hits zero).
	private void TickSprintStamina(float dt)
	{
		if (!_sprinting || data == null)
		{
			return;
		}
		_stamina -= data.sprintStaminaDrainPerSecond * dt;
		ulong now = _world?.GameTimeMs ?? 0;
		_staminaRechargeStartMs = now + (ulong)(data.staminaRechargeDelay * 1000f);
	}

	// Fires the out-of-breath one-shot on the positive→exhausted crossing.
	// Run after the stamina drains/recharge each tick so it reads the settled
	// value. The latch clears only once stamina climbs back above zero, so the
	// gasp plays once per exhaustion instead of every frame the bar is empty.
	private void TickStaminaExhaustion()
	{
		bool exhausted = _stamina <= 0f;
		if (exhausted && !_staminaExhausted)
		{
			SpawnVoiceSelf(_voice?.outOfBreath);
		}
		_staminaExhausted = exhausted;
	}
}
