using Godot;
using System;
using System.Collections.Generic;

// Per-actor status-effect manager shared by Player and Mob. Owns the live
// list, ticks per-second damage chunks, prunes expired effects, and runs the
// per-effect fx lifecycle (start one-shot, looping middle, end one-shot) so
// both actor classes stay in sync. Player and Mob each own an instance and
// expose thin pass-through methods for their callers (HUD, hit handling,
// thermal logic, wet trigger).
public class StatusEffectController
{
	// Per-effect buildup meter. Keyed by StatusEffectData reference so multiple
	// damage sources feeding the same effect (a poison sword + a poison cloud)
	// share one pool on the receiver. Allocated lazily — entries appear when
	// the first contribution lands.
	//
	// `armedInstance` is used only by ContinuousArm effects (Wet) — the meter
	// IS the effect intensity, so the controller holds onto the live state
	// it armed so it can release it when the meter falls below disarmThreshold.
	// Null for ThresholdCross effects, which produce discrete stacked instances
	// instead of a single meter-driven one.
	private class BuildupState
	{
		public float amount;
		public ulong decayStartMs;
		public StatusEffectState armedInstance;
	}

	readonly Node3D _actor;
	readonly Sim _world;
	// (signed health delta, armor penetration in [0, 1]). ArmorPenetration is
	// meaningful only for the damage path (delta < 0): it splits the chunk
	// between armor chip and direct HP loss. Heals (delta > 0) ignore it.
	readonly Action<float, float> _applyHealthDelta;
	// Lookup callback into the owning actor's full multiplicative composition
	// across inherent data + equipped armor + this controller's own active
	// effects, for a given tag mask. The controller can't sum equipment /
	// inherent stats on its own — the actor (Player / Mob) owns that data —
	// so we route through a callback. Used by the buildup path (effect.tags)
	// and the DoT tick (effect.tags) so a Fire-resistant target shrugs off
	// a Burning DoT even though the controller never sees the hit that
	// started it.
	readonly Func<EStat, float> _composeMaskMul;
	// (max-health drain amount). Reduces the actor's MAXIMUM health, clamping
	// current health down to follow and killing the actor when max hits 0. Null
	// on actors that don't model a drainable max (items, and the player today —
	// only the Mob controller wires it). Kept separate from _applyHealthDelta so
	// max decay never routes through the damage/DoT path (no floating number).
	readonly Action<float> _applyMaxHealthDelta;
	// Evaluates an authored ETraitCondition against the owning actor's live state
	// (stamina fraction, party) so conditionalModifiers fold in only while active.
	// Null on actors that don't carry conditional traits (Mob, item-side
	// controllers) — those skip every conditional group. See Player.EvaluateTraitCondition.
	readonly Func<ETraitCondition, float, bool> _conditionActive;
	// Receiver-side per-level resistance (<=1) folded onto every combat-delivered
	// buildup, alongside the tag/fortitude resistance. Sourced from the actor's
	// defensive level — the player's Armor forge-upgrade level, a mob's difficulty
	// Level (see Player/Mob.IncomingLevelResist). Null on item-side controllers and
	// any actor with no level defense → a neutral 1. Kept as a live callback (not a
	// cached scalar) because the level can change mid-life (a forge visit swaps the
	// Armor upgrade).
	readonly Func<float> _incomingLevelResist;
	// Live current-max-health accessor, for DamageOverTimeData.fractionMaxHealthPerSecond
	// (percentage-of-max DoTs like sunburn). Null on actors that don't model it
	// (the player, items) → percent DoTs are inert there.
	readonly Func<float> _maxHealth;
	readonly List<StatusEffectState> _statusEffects = new();
	readonly Dictionary<StatusEffectData, BuildupState> _buildups = new();
	// Rendered-visibility gate for every loop fx, driven by the owning actor
	// (Mob.UpdateVisibility) so a status loop hides when the body is culled by
	// perception. Stays true for actors that never drive it (the player). Cached
	// so Add can apply it to a loop spawned while the actor is already hidden.
	bool _loopFxVisible = true;

	public IReadOnlyList<StatusEffectState> StatusEffects => _statusEffects;

	// `actor` and `applyHealthDelta` may be null for item-owned controllers
	// (ArmorState.statusEffects etc.) — items have no world position to spawn
	// fx at and no health to chip away. `world` may also be null; the meter
	// machinery falls back to a zero game-time which is fine for ContinuousArm
	// (it doesn't read time) and degrades gracefully for ThresholdCross decay.
	public StatusEffectController(Node3D actor, Sim sim, Action<float, float> applyHealthDelta, Func<EStat, float> composeMaskMul = null, Action<float> applyMaxHealthDelta = null, Func<ETraitCondition, float, bool> conditionActive = null, Func<float> incomingLevelResist = null, Func<float> maxHealth = null)
	{
		_actor = actor;
		_world = sim;
		_applyHealthDelta = applyHealthDelta;
		_composeMaskMul = composeMaskMul;
		_applyMaxHealthDelta = applyMaxHealthDelta;
		_conditionActive = conditionActive;
		_incomingLevelResist = incomingLevelResist;
		_maxHealth = maxHealth;
	}

	public bool Contains(StatusEffectState state) => state != null && _statusEffects.Contains(state);

	// True iff at least one live state of `data` exists on this actor. Cheap
	// reference-equality scan; used by callers (Mob's "dizzy?" anim / behavior
	// gating) that hold an [Export] StatusEffectData ref instead of a state
	// handle.
	public bool HasActive(StatusEffectData data)
	{
		if (data == null)
		{
			return false;
		}
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			if (_statusEffects[i]?.data == data)
			{
				return true;
			}
		}
		return false;
	}

	// True if any active effect is dealing positive damage-over-time (poison,
	// burning, bleeding, ...). Heals are authored as negative damagePerSecond,
	// so they don't count. Read by NoDamagingEffectRequirement to refuse "rest"
	// actions while the actor is taking damage over time.
	public bool HasDamagingEffect
	{
		get
		{
			for (int i = 0; i < _statusEffects.Count; i++)
			{
				StatusEffectState s = _statusEffects[i];
				if (s?.data?.dot != null && (s.data.dot.damagePerSecond > 0f || s.data.dot.fractionMaxHealthPerSecond > 0f))
				{
					return true;
				}
			}
			return false;
		}
	}

	// True when any active effect's `incapacitates` flag is set. Read by Mob
	// to suppress AI / yells while in a heavy CC state (dizzy today; future
	// frozen / knocked-down compose freely without Mob needing to know about
	// each one). Walks the list on access for the same reason every other
	// accumulator does — refcounts add Add/Remove/Clear bug surface for a
	// list that's typically < 10 entries.
	public bool Incapacitated
	{
		get
		{
			for (int i = 0; i < _statusEffects.Count; i++)
			{
				if (_statusEffects[i]?.data?.incapacitates == true)
				{
					return true;
				}
			}
			return false;
		}
	}

	// --- Wielder upgrade source (forge weapon upgrades) ---
	// A wielder points its equipped weapon's controller at its own controller so the
	// WeaponMod* reads below also fold in the wielder's slot-matching forge upgrades
	// (a Flaming edge, Venomous tips). The upgrade effect lives on the WIELDER — never
	// composed onto the weapon item, which would mutate the shared inventory item and
	// persist through save/load (EntitySerializer writes an item's statusEffects). Set
	// per wield with the weapon's slot (see Player.TryStartWeaponAction); null folds in
	// nothing (mob weapons, un-wielded items). Upgrade mods aren't charge-scoped — an
	// upgrade modifies every attack the weapon makes — so ForEachWielderUpgradeMod
	// skips the ModReachesCharge gate the weapon's own mods use.
	private StatusEffectController _wielderUpgradeSource;
	private EUpgradeSlot _wielderUpgradeSlot;

	public void SetWielderUpgradeSource(StatusEffectController wielder, EUpgradeSlot slot)
	{
		_wielderUpgradeSource = wielder;
		_wielderUpgradeSlot = slot;
	}

	// Invoke `fold` for each of the wielder's active upgrade weaponMods applied to
	// this weapon's slot (appliedUpgradeSlot). No-op when no wielder source is set.
	private void ForEachWielderUpgradeMod(System.Action<WeaponModData> fold)
	{
		StatusEffectController src = _wielderUpgradeSource;
		if (src == null || _wielderUpgradeSlot == EUpgradeSlot.None)
		{
			return;
		}
		for (int i = 0; i < src._statusEffects.Count; i++)
		{
			StatusEffectState s = src._statusEffects[i];
			WeaponModData mod = s?.data?.weaponMod;
			if (mod != null && s.appliedUpgradeSlot == _wielderUpgradeSlot)
			{
				fold(mod);
			}
		}
	}

	// Invoke `fold` for every weapon mod that applies at charge tier `chargeIndex`:
	// this actor's own composed mods that reach the tier (ModReachesCharge), plus the
	// wielder's slot-matching forge upgrade mods (never charge-scoped). The single
	// walk behind every WeaponMod* read below. Pass allCharges: true to skip the tier
	// gate — the idle-fx visual rides the weapon at rest, independent of tier.
	private void ForEachWeaponMod(int chargeIndex, System.Action<WeaponModData> fold, bool allCharges = false)
	{
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			WeaponModData mod = s?.data?.weaponMod;
			if (mod == null || (!allCharges && !ModReachesCharge(s, chargeIndex)))
			{
				continue;
			}
			fold(mod);
		}
		ForEachWielderUpgradeMod(fold);
	}

	// True when any active weapon mod reaching charge tier `chargeIndex` sets
	// projectilesDetonateOnContact (the "Fragile" mod).
	public bool ProjectilesDetonateOnContact(int chargeIndex)
	{
		bool detonate = false;
		ForEachWeaponMod(chargeIndex, mod => detonate |= mod.projectilesDetonateOnContact);
		return detonate;
	}

	// Largest projectilePierceCount among active weapon mods reaching charge tier
	// `chargeIndex` (0 if none); DoProjectile maxes it against the event's base.
	public int ProjectilePierceCount(int chargeIndex)
	{
		int max = 0;
		ForEachWeaponMod(chargeIndex, mod => { if (mod.projectilePierceCount > max) { max = mod.projectilePierceCount; } });
		return max;
	}

	// Largest vampiric (lifesteal) fraction among active weapon mods reaching
	// charge tier `chargeIndex` (0 if none); the melee/hitscan handlers heal the
	// attacker by this fraction of the health damage a landed hit deals.
	public float Vampiric(int chargeIndex)
	{
		float max = 0f;
		ForEachWeaponMod(chargeIndex, mod => { if (mod.vampiric > max) { max = mod.vampiric; } });
		return max;
	}

	// Largest flat stamina-on-hit refund among active weapon mods reaching charge
	// tier `chargeIndex` (0 if none); the melee/hitscan/projectile handlers refill
	// the attacker by this many stamina points on each landed creature hit.
	public float StaminaOnHit(int chargeIndex)
	{
		float max = 0f;
		ForEachWeaponMod(chargeIndex, mod => { if (mod.staminaOnHit > max) { max = mod.staminaOnHit; } });
		return max;
	}

	// Buildup contributions every active weapon mod reaching charge tier
	// `chargeIndex` funnels into the struck target's meters (a Venomous mob's
	// Poison buildup, etc.). Returns null when no reaching mod authors any, so
	// the common no-mod hot path allocates nothing.
	public Godot.Collections.Array<StatusEffectBuildup> WeaponModOnHitBuildups(int chargeIndex)
	{
		Godot.Collections.Array<StatusEffectBuildup> result = null;
		ForEachWeaponMod(chargeIndex, mod =>
		{
			Godot.Collections.Array<StatusEffectBuildup> onHit = mod.onHitBuildups;
			if (onHit == null || onHit.Count == 0)
			{
				return;
			}
			result ??= new Godot.Collections.Array<StatusEffectBuildup>();
			for (int j = 0; j < onHit.Count; j++)
			{
				result.Add(onHit[j]);
			}
		});
		return result;
	}

	// Chain-lightning payloads on active weapon mods reaching charge tier
	// `chargeIndex` (the Shocking mod). Returns null when none reach, so the
	// common no-mod hot path allocates nothing.
	public Godot.Collections.Array<ChainLightningData> WeaponModChainLightning(int chargeIndex)
	{
		Godot.Collections.Array<ChainLightningData> result = null;
		ForEachWeaponMod(chargeIndex, mod =>
		{
			if (mod.chainLightning == null)
			{
				return;
			}
			result ??= new Godot.Collections.Array<ChainLightningData>();
			result.Add(mod.chainLightning);
		});
		return result;
	}

	// Summed extra knockback distance (m/s) from active weapon mods reaching
	// charge tier `chargeIndex` (the Knockback mod); the melee/hitscan/projectile
	// paths add it to the hit's knockbackDistance. 0 if none reach.
	public float WeaponModKnockbackBonus(int chargeIndex)
	{
		float sum = 0f;
		ForEachWeaponMod(chargeIndex, mod => sum += mod.knockbackBonus);
		return sum;
	}

	// Summed extra knockback lockout (seconds) from active weapon mods reaching
	// charge tier `chargeIndex`; added to the hit's knockbackTime. 0 if none reach.
	public float WeaponModKnockbackTimeBonus(int chargeIndex)
	{
		float sum = 0f;
		ForEachWeaponMod(chargeIndex, mod => sum += mod.knockbackTimeBonus);
		return sum;
	}

	// Idle Fx scenes from every active weapon mod (the held-weapon visual a mod
	// adds, e.g. a Flaming sword's flame). NOT charge-filtered — the idle fx
	// rides the weapon at rest, independent of which tier would fire. Returns
	// null when no mod authors one, so the common no-mod case allocates nothing.
	public Godot.Collections.Array<PackedScene> WeaponModIdleFx()
	{
		Godot.Collections.Array<PackedScene> result = null;
		ForEachWeaponMod(0, mod =>
		{
			if (mod.idleFx == null)
			{
				return;
			}
			result ??= new Godot.Collections.Array<PackedScene>();
			result.Add(mod.idleFx);
		}, allCharges: true);
		return result;
	}

	// Projectile-loop Fx scenes from active weapon mods reaching charge tier
	// `chargeIndex` (a Flaming bow's flaming arrows). Layered on top of the
	// intrinsic trail authored as a child Fx of the projectile scene. Returns null when none
	// reach, so the common no-mod hot path allocates nothing.
	public Godot.Collections.Array<PackedScene> WeaponModProjectileFx(int chargeIndex)
	{
		Godot.Collections.Array<PackedScene> result = null;
		ForEachWeaponMod(chargeIndex, mod =>
		{
			if (mod.projectileFx == null)
			{
				return;
			}
			result ??= new Godot.Collections.Array<PackedScene>();
			result.Add(mod.projectileFx);
		});
		return result;
	}

	// On-attack projectile events from active weapon mods reaching charge tier
	// `chargeIndex` whose onAttackTrigger overlaps `trigger` (a "Seeking" sword's
	// homing missiles). The Melee / Hitscan handlers pass OnSwing for every
	// attack and add OnHit when it connects, then dispatch each event through
	// DoProjectile. Returns null when none reach, so the common no-mod hot path
	// allocates nothing.
	public Godot.Collections.Array<ItemEvent> WeaponModOnAttackEvents(int chargeIndex, EWeaponModAttackTrigger trigger)
	{
		Godot.Collections.Array<ItemEvent> result = null;
		ForEachWeaponMod(chargeIndex, mod =>
		{
			if (mod.onAttackEvent == null || (mod.onAttackTrigger & trigger) == 0)
			{
				return;
			}
			result ??= new Godot.Collections.Array<ItemEvent>();
			result.Add(mod.onAttackEvent);
		});
		return result;
	}

	// On-attack projectile mods carried as BODY status effects (a Fairy boon's
	// homing missiles) whose trigger overlaps `trigger` and whose onAttackSlot
	// matches `slot` (the equipped weapon slot the firing attack came from), so a
	// melee-slot boon and a ranged-slot boon stay distinct. A mod with onAttackSlot
	// == None matches any slot. Distinct from WeaponModOnAttackEvents: body mods
	// aren't charge-scoped (they fire on every attack regardless of weapon), and
	// the caller needs the mod itself to read its intrinsic projectileDamage —
	// there's no wielding weapon to resolve the event's damageProfileKey against.
	// Returns null when none, so the common no-boon hot path allocates nothing.
	public Godot.Collections.Array<WeaponModData> BodyOnAttackMods(EWeaponModAttackTrigger trigger, EInventorySlot slot)
	{
		Godot.Collections.Array<WeaponModData> result = null;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			// Forge weapon upgrades (upgradeSlot != None) route through the wielded
			// weapon's WeaponMod* reads, folded by weapon slot — skip them on the body
			// path so a Seeking upgrade doesn't ALSO fire here (slot-agnostically), which
			// would double up its missiles and fire them with the wrong weapon.
			if (data == null || data.upgradeSlot != EUpgradeSlot.None)
			{
				continue;
			}
			WeaponModData mod = data.weaponMod;
			if (mod?.onAttackEvent == null || (mod.onAttackTrigger & trigger) == 0)
			{
				continue;
			}
			if (mod.onAttackSlot != EInventorySlot.None && mod.onAttackSlot != slot)
			{
				continue;
			}
			result ??= new Godot.Collections.Array<WeaponModData>();
			result.Add(mod);
		}
		return result;
	}

	// A composed weapon mod reaches a given firing charge tier when it's scoped
	// to every attack, or scoped to the specific tier being fired. A negative
	// chargeIndex (the weapon has no resolvable firing tier) only matches
	// AllAttacks mods.
	private static bool ModReachesCharge(StatusEffectState state, int chargeIndex)
	{
		return state.weaponModScope == EWeaponModScope.AllAttacks
			|| state.weaponModChargeIndex == chargeIndex;
	}

	// Composite vulnerability in [0, 1]. Composes per-effect contributions as
	// 1 - product(1 - v_i) so multiple vulnerabilities chain as independent
	// probabilities; any single effect with vulnerable=1 pins the result at
	// 1 (always crit on a triggered receiver). Attackers fold this into their
	// crit roll: `final = baseCrit + (1 - baseCrit) * vulnerable`.
	public float Vulnerable
	{
		get
		{
			float survival = 1f;
			for (int i = 0; i < _statusEffects.Count; i++)
			{
				StatusEffectData data = _statusEffects[i]?.data;
				if (data == null || data.vulnerable <= 0f)
				{
					continue;
				}
				survival *= 1f - Mathf.Clamp(data.vulnerable, 0f, 1f);
			}
			return 1f - survival;
		}
	}

	// Drop every active state whose data flags `incapacitates`. Called from
	// the hit pipeline as the generalized "any hit wakes a CC'd actor" rule.
	// Their buildup meters are also zeroed so the very next hit doesn't
	// immediately re-cross the threshold from residual charge.
	public void ClearIncapacitating()
	{
		for (int i = _statusEffects.Count - 1; i >= 0; i--)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data == null || !data.incapacitates)
			{
				continue;
			}
			EndFx(_statusEffects[i]);
			_statusEffects.RemoveAt(i);
			if (_buildups.TryGetValue(data, out BuildupState bs))
			{
				bs.amount = 0f;
				bs.armedInstance = null;
			}
		}
	}

	// First active effect's loopAnimOverride, or EAnimation.None when no
	// effect authors one. The actor's UpdateAnimation reads this before
	// falling through to its movement-state loop pick so Dizzy / future
	// override-bearing effects (knocked-down, frozen, etc.) hold a fixed
	// clip without each actor needing a hardcoded check. First-wins iteration
	// — effects authored with overrides should be mutually exclusive in
	// practice (Dizzy can't stack with itself, can't co-apply with another
	// override-bearer in current content), so ordering ambiguity is moot.
	public EAnimation LoopAnimOverride
	{
		get
		{
			for (int i = 0; i < _statusEffects.Count; i++)
			{
				StatusEffectData data = _statusEffects[i]?.data;
				if (data == null)
				{
					continue;
				}
				if (data.loopAnimOverride != EAnimation.None)
				{
					return data.loopAnimOverride;
				}
			}
			return EAnimation.None;
		}
	}

	// Data of the first active effect whose category overlaps `mask`, or null.
	// Used by the mob HUD to pick the elite badge's icon (the first
	// Elite-category effect on the mob). First-wins matches the rest of the
	// controller's single-pass composition; an elite carries one signature in
	// practice, so ordering ambiguity is moot.
	public StatusEffectData FirstOfCategory(EEffectCategory mask)
	{
		if (mask == EEffectCategory.None)
		{
			return null;
		}
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data != null && (data.category & mask) != 0)
			{
				return data;
			}
		}
		return null;
	}

	// Remove every active instance whose category overlaps `mask`. The category
	// axis is orthogonal to RemoveByTagMask's tag axis: tags say "what kind of
	// effect" (Poison, Fire) for cure-by-element; category says "what bucket"
	// (Transient / Permanent / Elite) so a clear can spare an elite signature
	// or a permanent quirk while wiping ordinary combat states. Matching buildup
	// meters are zeroed so a partially-charged effect doesn't immediately
	// re-apply after the clear.
	public void RemoveByCategory(EEffectCategory mask)
	{
		if (mask == EEffectCategory.None)
		{
			return;
		}
		for (int i = _statusEffects.Count - 1; i >= 0; i--)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data == null || (data.category & mask) == 0)
			{
				continue;
			}
			EndFx(_statusEffects[i]);
			_statusEffects.RemoveAt(i);
		}
		foreach (var kv in _buildups)
		{
			if (kv.Key != null && (kv.Key.category & mask) != 0 && kv.Value != null)
			{
				kv.Value.amount = 0f;
				kv.Value.armedInstance = null;
			}
		}
	}

	// Current buildup meter for `data` in [0, 1+). Returns 0 when no
	// contribution has landed (no entry allocated yet). Read-only — HUD /
	// debug overlays use this.
	public float GetBuildup(StatusEffectData data)
	{
		if (data == null)
		{
			return 0f;
		}
		return _buildups.TryGetValue(data, out BuildupState s) ? s.amount : 0f;
	}

	// Snapshot every effect with a non-zero buildup meter into `dst`. Caller
	// owns the dictionary so we don't allocate per frame; HUD reuses one
	// across UpdateStatusEffects ticks. Entries with amount == 0 are skipped
	// (a buildup can be allocated lazily by AddBuildup and then drained back
	// to zero by decay — keeping the entry in the controller's map is
	// harmless, but the HUD shouldn't render a zero-fill bar).
	public void FillBuildupSnapshot(Dictionary<StatusEffectData, float> dst)
	{
		if (dst == null)
		{
			return;
		}
		dst.Clear();
		foreach (var kv in _buildups)
		{
			if (kv.Value == null || kv.Value.amount <= 0f || kv.Key == null)
			{
				continue;
			}
			dst[kv.Key] = kv.Value.amount;
		}
	}

	// Apply every effect contribution in `hit` to this actor — immediate-apply
	// entries (applyImmediately) land the effect directly; the rest funnel into
	// the per-effect buildup meter, folding each crossed-threshold effect's
	// applyTrigger back onto the HitInfo. Receivers call this between armor
	// resolution and the hitstun/knockback reads so an OnDizzy modifier can
	// amplify those reads on the same hit that landed dizzy. Passed by ref
	// because ApplyTrigger mutates the struct in-place; by value would discard it.
	// Returns whether the hit deposited any buildup — an immediate-apply effect
	// landed, or a meter entry contributed a positive amount. Callers use this to
	// tell an inert "MISS!" hit (zero damage AND no buildup) from one that chipped
	// a status meter without visible damage.
	public bool ApplyHitBuildups(ref HitInfo hit)
	{
		if (hit.buildups == null)
		{
			return false;
		}
		bool appliedAny = false;
		for (int i = 0; i < hit.buildups.Count; i++)
		{
			StatusEffectBuildup entry = hit.buildups[i];
			if (entry == null || entry.effect == null)
			{
				continue;
			}
			// Immediate apply: land the effect now (Burning, Poison), bypassing
			// the meter, `amount`, and applyTrigger. Same Add path a meter cross
			// uses, so removesOnApply / maxStack / fx lifecycle all run.
			if (entry.applyImmediately)
			{
				Add(entry.effect);
				appliedAny = true;
				continue;
			}
			// Buildup contributions are tagged by the receiving effect, not
			// the carrier hit — kun-kun's Dizzy vulnerability lifts any
			// buildup feeding a Dizzy-tagged effect regardless of what hit
			// landed it. effect.tags == None falls through to a 1x
			// multiplier (untagged buildups take no resistance scaling).
			// Only combat-delivered buildup routes through here — ambient meters
			// (rain Wet) call AddBuildup directly and are intentionally untouched.
			float amount = entry.amount * hit.buildupAmountMultiplier;
			// A positive contribution reaches the meter (resistance may still
			// scale it down inside AddCombatBuildup, but the hit did land buildup),
			// so it's not a miss even when this tick doesn't cross the threshold.
			if (amount > 0f)
			{
				appliedAny = true;
			}
			bool applied = AddCombatBuildup(entry.effect, amount);
			if (applied && entry.effect.applyTrigger != EDamageTrigger.None)
			{
				hit.ApplyTrigger(entry.effect.applyTrigger);
			}
		}
		return appliedAny;
	}

	// Land one combat-delivered buildup contribution, folding the receiver's
	// resistance onto `amount` exactly as the ApplyHitBuildups meter path does,
	// and report whether it armed / crossed. Split out so out-of-band combat
	// sources that need the crossing result (chain lightning, which gates its
	// next hop on whether this link discharged) can share the identical scaling
	// the ref-HitInfo path can't hand back. FortitudeResistance is OR'd into the
	// tag mask so the actor's general buildup resistance (PlayerState.fortitude
	// plus any gear/status modifier) scales every combat buildup on top of its
	// per-tag resistance; levelResist (Armor upgrade / mob Level) is the buildup
	// counterpart of the incoming-damage resist in ApplyResistance.
	public bool AddCombatBuildup(StatusEffectData effect, float amount)
	{
		if (effect == null || amount == 0f)
		{
			return false;
		}
		float resistance = _composeMaskMul?.Invoke(effect.tags | EStat.FortitudeResistance) ?? 1f;
		float levelResist = _incomingLevelResist?.Invoke() ?? 1f;
		return AddBuildup(effect, amount * resistance * levelResist);
	}

	// Add a signed contribution to `data`'s buildup meter and run the per-
	// behavior arming check. Returns true when this call armed (or re-armed)
	// a fresh instance — the caller (HurtBox hit path) uses that to fire the
	// effect's applyTrigger so modifier folds (OnDizzy extra knockback, etc.)
	// land on the same hit.
	//
	// ThresholdCross — only positive contributions are accepted (auto-decay
	// in Tick handles drainage). A single fat contribution can cross 1.0
	// multiple times: the loop reapplies until the meter is under 1 (or
	// clearBuildupOnApply zeros it on the first cross).
	//
	// ContinuousArm — both signs are accepted; external code drives the meter
	// (rain adds, drying subtracts). Meter is clamped to [0, 1]. The effect
	// arms when the meter rises through armThreshold and releases when it
	// falls through disarmThreshold (hysteresis prevents flapping). The armed
	// instance's duration timer is held paused so the meter, not a countdown,
	// owns lifecycle.
	public bool AddBuildup(StatusEffectData data, float amount)
	{
		if (data == null || amount == 0f)
		{
			return false;
		}
		bool isContinuous = data.buildupBehavior == EBuildupBehavior.ContinuousArm;
		if (!isContinuous && amount <= 0f)
		{
			return false;
		}
		if (!_buildups.TryGetValue(data, out BuildupState state))
		{
			state = new BuildupState();
			_buildups[data] = state;
		}
		state.amount += amount;
		ulong now = _world?.GameTimeMs ?? 0;
		if (isContinuous)
		{
			if (state.amount < 0f) { state.amount = 0f; }
			else if (state.amount > 1f) { state.amount = 1f; }
			return UpdateContinuousArm(data, state, now);
		}
		state.decayStartMs = now + (ulong)(data.buildupRemovalDelay * 1000f);
		bool applied = false;
		while (state.amount >= 1f)
		{
			Add(data);
			applied = true;
			if (data.clearBuildupOnApply)
			{
				state.amount = 0f;
				break;
			}
			state.amount -= 1f;
		}
		return applied;
	}

	// ContinuousArm hysteresis: arm when the meter rises through armThreshold,
	// release when it falls through disarmThreshold. The armed instance's
	// duration timer is paused immediately so the meter (not a countdown)
	// controls lifecycle — a stray non-zero `data.duration` from authoring
	// won't auto-expire on us. Returns true on the arm transition; release
	// returns false (only fresh arms are meaningful to ApplyHitBuildups, and
	// ContinuousArm effects don't carry an applyTrigger in any case).
	private bool UpdateContinuousArm(StatusEffectData data, BuildupState state, ulong now)
	{
		if (state.armedInstance != null && !_statusEffects.Contains(state.armedInstance))
		{
			state.armedInstance = null;
		}
		if (state.armedInstance == null)
		{
			if (state.amount >= data.armThreshold)
			{
				state.armedInstance = Add(data);
				state.armedInstance?.PauseTimer();
				return true;
			}
			return false;
		}
		if (state.amount <= data.disarmThreshold)
		{
			Remove(state.armedInstance);
			state.armedInstance = null;
		}
		else
		{
			state.armedInstance.PauseTimer();
		}
		return false;
	}


	// Fire each active effect's attackImpact burst at `position`. Called by the
	// Melee / Hitscan handlers when an attack resolves its impact point — the
	// actor-side on-attack aura (an AreaBurstData). `attacker` scopes the
	// area-damage team / self-exclusion. (Chain lightning is NOT fired here: it's
	// a weapon-mod payload that rides the primaryItem WeaponState through
	// ItemEventHandlers.TriggerWeaponModChains, for player and elite mob alike.)
	public void TriggerAttackImpact(IActionActor attacker, Vector3 position)
	{
		if (attacker == null)
		{
			return;
		}
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data == null)
			{
				continue;
			}
			AreaBurstData burst = data.attackImpact;
			if (burst != null && (burst.damage != null || burst.fx != null))
			{
				if (burst.fx != null && _world != null)
				{
					Fx.Create(burst.fx, _world, position);
				}
				if (burst.damage != null)
				{
					ItemEventHandlers.ApplyAreaDamage(attacker, burst.damage, position, burst.radius);
				}
			}
		}
	}

	// Fire each active effect's dashBurst at the dashing actor. Called from
	// Player.ApplyMotion when a dash begins. Like TriggerAttackImpact but the area
	// damage uses radial knockback (targets shoved away from the actor).
	public void TriggerDashBurst(IActionActor attacker, Vector3 position)
	{
		if (attacker == null)
		{
			return;
		}
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			AreaBurstData burst = _statusEffects[i]?.data?.dashBurst;
			if (burst == null || (burst.damage == null && burst.fx == null))
			{
				continue;
			}
			if (burst.fx != null && _world != null)
			{
				Fx.Create(burst.fx, _world, position);
			}
			if (burst.damage != null)
			{
				ItemEventHandlers.ApplyAreaDamage(attacker, burst.damage, position, burst.radius, radialKnockback: true);
			}
		}
	}

	// Drop a trail hazard patch for each active effect with a `trail`, paced by its
	// dropInterval. Called per physics frame with `moving` = dashing OR sprinting.
	// While not moving the timer is held armed so the next step lays a patch promptly.
	public void TickMovementTrail(IActionActor actor, bool moving, Vector3 position, float dt)
	{
		if (_world == null || _statusEffects.Count == 0)
		{
			return;
		}
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState state = _statusEffects[i];
			MovementTrailData trail = state?.data?.trail;
			if (trail?.zoneScene == null)
			{
				continue;
			}
			float interval = Mathf.Max(0.01f, trail.dropInterval);
			if (!moving)
			{
				state.trailAccumulator = interval;
				continue;
			}
			state.trailAccumulator -= dt;
			if (state.trailAccumulator > 0f)
			{
				continue;
			}
			state.trailAccumulator = interval;
			Node3D patch = trail.zoneScene.Instantiate<Node3D>();
			_world.AddChild(patch);
			patch.GlobalPosition = position;
		}
	}

	// Add a status effect as a weapon mod, stamping the descriptor's scope/charge onto
	// the live state so the firing path can filter by tier. Used by ItemDescriptor.ApplyTo.
	public StatusEffectState AddWeaponMod(StatusEffectData data, EWeaponModScope scope, int chargeIndex)
	{
		StatusEffectState state = Add(data);
		if (state != null)
		{
			state.weaponModScope = scope;
			state.weaponModChargeIndex = chargeIndex;
		}
		return state;
	}

	// Drop every active state (and zero the buildup meter) listed in `data`'s
	// removesOnApply. Called by Add for lingering effects, and by the actor
	// AddStatusEffect path for `instantaneous` effects (which skip Add but still
	// need their cleanse to land). Null list = no-op.
	public void ApplyRemovesOnApply(StatusEffectData data)
	{
		if (data?.removesOnApply == null)
		{
			return;
		}
		for (int j = 0; j < data.removesOnApply.Count; j++)
		{
			StatusEffectData removed = data.removesOnApply[j];
			if (removed == null)
			{
				continue;
			}
			for (int i = _statusEffects.Count - 1; i >= 0; i--)
			{
				if (_statusEffects[i]?.data == removed)
				{
					EndFx(_statusEffects[i]);
					_statusEffects.RemoveAt(i);
				}
			}
			if (_buildups.TryGetValue(removed, out BuildupState bs))
			{
				bs.amount = 0f;
				bs.armedInstance = null;
			}
		}
	}

	// Remove any active upgrade occupying the concrete `slot` (its fx wound down).
	// No-op for EUpgradeSlot.None so ordinary effects are unaffected. Called by Add
	// before inserting a slotted upgrade, giving the swap-not-stack semantics.
	private void EvictUpgradeSlot(EUpgradeSlot slot)
	{
		if (slot == EUpgradeSlot.None)
		{
			return;
		}
		for (int i = _statusEffects.Count - 1; i >= 0; i--)
		{
			if (_statusEffects[i]?.appliedUpgradeSlot == slot)
			{
				EndFx(_statusEffects[i]);
				_statusEffects.RemoveAt(i);
			}
		}
	}

	// Currently-active upgrade occupying the concrete `slot`, or null if none.
	// Lets the forge show what a new upgrade would replace.
	public StatusEffectData ActiveUpgrade(EUpgradeSlot slot)
	{
		if (slot == EUpgradeSlot.None)
		{
			return null;
		}
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			if (_statusEffects[i]?.appliedUpgradeSlot == slot)
			{
				return _statusEffects[i].data;
			}
		}
		return null;
	}

	// Upgrade tier (StatusEffectState.level) of the upgrade occupying the concrete
	// `slot`, or 0 when none. Drives the per-level offense/defense scaling: the
	// firing weapon's slot picks the Melee/Ranged upgrade for outgoing damage +
	// buildup, and the Armor slot picks the defensive resist. Since a slot holds at
	// most one upgrade (Add evicts the prior occupant), the first match is definitive.
	public int ActiveUpgradeLevel(EUpgradeSlot slot)
	{
		if (slot == EUpgradeSlot.None)
		{
			return 0;
		}
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			if (_statusEffects[i]?.appliedUpgradeSlot == slot)
			{
				return _statusEffects[i].level;
			}
		}
		return 0;
	}

	// `level` is the upgrade tier stamped on the created instance (0 = none). Only
	// meaningful for slotted forge upgrades; ordinary callers omit it. `appliedSlot`
	// is the concrete slot a forge applies the upgrade to (None for ordinary effects
	// and weapon mods) — it drives the swap-not-stack slot exclusivity and is stamped
	// onto the instance for weapon-mod matching (see StatusEffectState.appliedUpgradeSlot).
	public StatusEffectState Add(StatusEffectData data, int level = 0, EUpgradeSlot appliedSlot = EUpgradeSlot.None)
	{
		if (data == null)
		{
			return null;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		int nowDay = _world?.DayNumber ?? 0;
		double nowTod01 = _world?.TimeOfDay01 ?? 0.0;
		// Mutual-exclusion pass — drop any active states (and their charging
		// buildup meters) listed in this effect's removesOnApply. Runs before the
		// stack-cap branch so a same-frame re-add of `data` itself can't get
		// tangled with its own removal.
		ApplyRemovesOnApply(data);
		// Slot-exclusive upgrades: applying one evicts the current occupant of the
		// same concrete slot (melee/ranged/armor/helmet), so a forge visit swaps that
		// slot rather than stacking. None-slotted effects skip this entirely.
		EvictUpgradeSlot(appliedSlot);
		// Enforce data.maxStack by refreshing the oldest still-alive instance
		// instead of appending. List order is insertion order (Tick prunes in
		// place via RemoveAt) so the first match is the oldest. ArmTimer is a
		// no-op for persistent effects (duration == 0), which is fine — the
		// stack cap still suppresses the duplicate add. Slotted forge upgrades
		// skip this: per-slot exclusivity (the eviction above) is their cap, so a
		// maxStack-1 upgrade can still sit on two different slots at once (Vampiric
		// on both the melee and ranged weapon).
		if (appliedSlot == EUpgradeSlot.None && data.maxStack > 0)
		{
			int count = 0;
			StatusEffectState oldest = null;
			for (int i = 0; i < _statusEffects.Count; i++)
			{
				if (_statusEffects[i]?.data == data)
				{
					count++;
					if (oldest == null)
					{
						oldest = _statusEffects[i];
					}
				}
			}
			if (count >= data.maxStack && oldest != null)
			{
				oldest.ArmTimer(now, nowDay, nowTod01);
				SpawnStartFx(data);
				return oldest;
			}
		}
		var state = new StatusEffectState(data, now, nowDay, nowTod01) { level = level, appliedUpgradeSlot = appliedSlot };
		_statusEffects.Add(state);
		SpawnStartFx(data);
		if (data.loopFx != null && _actor != null)
		{
			state.loopInstance = Fx.Create(data.loopFx, _actor, Vector3.Zero);
			// Match the body's current cull state so an effect applied to an
			// unseen mob doesn't pop its loop fx into view.
			if (!_loopFxVisible && state.loopInstance != null)
			{
				state.loopInstance.Visible = false;
			}
		}
		return state;
	}

	// Fx spawning is conditional on having a world + an actor with a world
	// position. Item-side controllers (the ones held by ArmorState etc.) pass
	// null actor + null world, so wet armor in the backpack doesn't try to
	// spawn the splash effect — the player's own status effect, armed via
	// the cascade contribution, is what surfaces the audiovisual cue.
	private void SpawnStartFx(StatusEffectData data)
	{
		if (data.startFx == null || _world == null || _actor == null)
		{
			return;
		}
		Fx.Create(data.startFx, _world, _actor.GlobalPosition);
	}

	// Snapshot every nonzero buildup meter for save serialization. Returns
	// (data, amount) pairs only — decayStartMs deliberately isn't surfaced
	// because it's a game-time stamp that's meaningless across a save/load
	// boundary (loaded saves should get a fresh decay window, not inherit a
	// wall-clock from the prior session). Active StatusEffectState instances
	// (the per-stack list with expiry timers, fx, etc.) are NOT covered —
	// that's separate state with its own save/restore concerns.
	public IEnumerable<(StatusEffectData data, float amount)> EnumerateBuildupsForSave()
	{
		foreach (var kv in _buildups)
		{
			if (kv.Key == null || kv.Value == null || kv.Value.amount <= 0f)
			{
				continue;
			}
			yield return (kv.Key, kv.Value.amount);
		}
	}

	// Bulk restore from save. Clears any existing buildups + active states
	// first (the assumption is a freshly-constructed actor), then seeds each
	// loaded meter and re-arms any ContinuousArm effect whose meter is at
	// or above its arm threshold so the controller and the actor's derived
	// state (modifier folds, HUD strip) agree on lifecycle. decayStartMs is
	// reset to "now + delay" so ThresholdCross decay starts fresh from load
	// time rather than inheriting a stale stamp.
	public void RestoreBuildups(IReadOnlyList<(StatusEffectData data, float amount)> entries)
	{
		Clear();
		if (entries == null)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		for (int i = 0; i < entries.Count; i++)
		{
			var (data, amount) = entries[i];
			if (data == null || amount <= 0f)
			{
				continue;
			}
			var state = new BuildupState
			{
				amount = amount,
				decayStartMs = now + (ulong)(data.buildupRemovalDelay * 1000f),
			};
			_buildups[data] = state;
		}
		foreach (var kv in _buildups)
		{
			StatusEffectData data = kv.Key;
			if (data == null || data.buildupBehavior != EBuildupBehavior.ContinuousArm)
			{
				continue;
			}
			BuildupState state = kv.Value;
			if (state.amount >= data.armThreshold)
			{
				state.armedInstance = Add(data);
				state.armedInstance?.PauseTimer();
			}
		}
	}

	public void Remove(StatusEffectState state)
	{
		if (state == null)
		{
			return;
		}
		if (_statusEffects.Remove(state))
		{
			EndFx(state);
		}
		// Drop the armed-instance handle if a ContinuousArm caller (or any
		// external code holding a state ref) removed our armed copy out from
		// under us — without this the meter would believe the effect is
		// still armed and skip the re-arm branch.
		if (state.data != null && _buildups.TryGetValue(state.data, out BuildupState bs) && bs.armedInstance == state)
		{
			bs.armedInstance = null;
		}
	}

	// Remove every active instance whose data.tags overlaps `mask`. Used by
	// cure-style consumables (cure-poison potion clears any effect tagged
	// Poison). Matching buildup meters are also zeroed so a partially-charged
	// effect doesn't immediately re-apply after the cure.
	public void RemoveByTagMask(EStat mask)
	{
		if (mask == EStat.None)
		{
			return;
		}
		for (int i = _statusEffects.Count - 1; i >= 0; i--)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data == null || (data.tags & mask) == 0)
			{
				continue;
			}
			EndFx(_statusEffects[i]);
			_statusEffects.RemoveAt(i);
		}
		foreach (var kv in _buildups)
		{
			if (kv.Key != null && (kv.Key.tags & mask) != 0 && kv.Value != null)
			{
				kv.Value.amount = 0f;
				kv.Value.armedInstance = null;
			}
		}
	}

	// Remove every active instance whose data == `data`. Used by callers that
	// only hold the StatusEffectData reference (e.g. Mob's wake-on-hit clears
	// Dizzy without tracking the state handle). Doesn't touch the buildup
	// meter — callers that want a clean slate handle the meter separately.
	public void RemoveAllOfType(StatusEffectData data)
	{
		if (data == null)
		{
			return;
		}
		for (int i = _statusEffects.Count - 1; i >= 0; i--)
		{
			if (_statusEffects[i]?.data == data)
			{
				EndFx(_statusEffects[i]);
				_statusEffects.RemoveAt(i);
			}
		}
		if (_buildups.TryGetValue(data, out BuildupState bs))
		{
			bs.armedInstance = null;
		}
	}

	// Drop every active effect, running the per-effect EndFx so loop instances
	// stop and end cues fire. Used by Player.Respawn so a cold / wet / poisoned
	// corpse comes back clean. Buildup meters are also cleared so a respawned
	// player doesn't start with charged pre-states from their last life.
	public void Clear()
	{
		for (int i = _statusEffects.Count - 1; i >= 0; i--)
		{
			EndFx(_statusEffects[i]);
		}
		_statusEffects.Clear();
		_buildups.Clear();
	}

	// Per-second damagePerSecond chunks + expiry pruning. Iterates backwards
	// so a mid-loop removal doesn't shift indices for unvisited entries.
	// Persistent effects (no timer) survive forever and rely on gameplay code to
	// call Remove explicitly. Also drains the buildup meters past their per-effect
	// decay delay so a partially-charged state empties when the source stops hitting.
	public void Tick(float dt)
	{
		ulong now = _world?.GameTimeMs ?? 0;
		int nowDay = _world?.DayNumber ?? 0;
		double nowTod01 = _world?.TimeOfDay01 ?? 0.0;
		// Buildup decay — runs even when _statusEffects is empty so a meter
		// charged by one stray hit still drains back to zero. After the
		// decay delay elapses, drop `buildupRemovalSpeed` units/sec; 0 speed
		// means the meter holds indefinitely (no decay authored).
		if (_buildups.Count > 0)
		{
			foreach (var kv in _buildups)
			{
				BuildupState bs = kv.Value;
				if (bs.amount <= 0f || now < bs.decayStartMs)
				{
					continue;
				}
				// ContinuousArm meters are externally driven (signed AddBuildup
				// deltas from rain / drying / cascade); no automatic decay
				// path. Drainage IS the caller calling AddBuildup(-rate * dt).
				if (kv.Key?.buildupBehavior == EBuildupBehavior.ContinuousArm)
				{
					continue;
				}
				float speed = kv.Key?.buildupRemovalSpeed ?? 0f;
				if (speed <= 0f)
				{
					continue;
				}
				bs.amount = Mathf.Max(0f, bs.amount - speed * dt);
			}
		}
		if (_statusEffects.Count == 0)
		{
			return;
		}
		for (int i = _statusEffects.Count - 1; i >= 0; i--)
		{
			// A DoT tick below can kill the actor, whose death cascade clears this
			// list mid-loop (Mob.Die → Clear). Re-clamp so the next read doesn't
			// run off the now-shorter list.
			if (i >= _statusEffects.Count)
			{
				continue;
			}
			StatusEffectState s = _statusEffects[i];
			if (s.data == null)
			{
				_statusEffects.RemoveAt(i);
				continue;
			}
			DamageOverTimeData dot = s.data.dot;
			if (dot == null)
			{
				if (s.IsExpired(now, nowDay, nowTod01))
				{
					_statusEffects.RemoveAt(i);
					EndFx(s);
				}
				continue;
			}
			s.tickAccumulator += dt;
			while (s.tickAccumulator >= 1f)
			{
				s.tickAccumulator -= 1f;
				if (_applyHealthDelta != null)
				{
					// Damage ticks (positive damagePerSecond) scale by the
					// actor's full resistance to the effect's tags so a
					// Fire-resistant body shrugs off a Burning burn tick.
					// Heals (negative damagePerSecond) pass through neat —
					// resistance is a damage-side concept; we don't want a
					// Magical-resistant target healing slower from a Magical-
					// tagged regen, and the explicit guard avoids a silly
					// >1 vulnerability scaling a heal up either. Item-side
					// controllers pass null _applyHealthDelta — items don't
					// take damage from their own status effects.
					float dps = dot.damagePerSecond;
					if (dps > 0f)
					{
						float resistance = _composeMaskMul?.Invoke(s.data.tags) ?? 1f;
						// Defensive-level resist (Armor upgrade / mob Level) reduces
						// incoming DoT the same as a direct hit — "damage resist"
						// covers burn/poison ticks, not just the landing blow.
						dps *= resistance * (_incomingLevelResist?.Invoke() ?? 1f);
					}
					// Percentage-of-max-health damage (sunburn), added on top and
					// deliberately UNSCALED by resistance/level so the melt time is
					// the same regardless of the health pool. armorPenetration still
					// controls the armor split.
					if (dot.fractionMaxHealthPerSecond > 0f && _maxHealth != null)
					{
						dps += _maxHealth() * dot.fractionMaxHealthPerSecond;
					}
					if (dps != 0f)
					{
						_applyHealthDelta(-dps, dot.armorPenetration);
					}
				}
				// Max-health decay (withering / summon self-expiry). Separate
				// channel from the damage path above so it surfaces no DoT
				// number; the actor's callback clamps current HP and handles
				// death at zero max.
				if (dot.maxHealthDrainPerSecond != 0f && _applyMaxHealthDelta != null)
				{
					_applyMaxHealthDelta(dot.maxHealthDrainPerSecond);
				}
			}
			if (s.IsExpired(now, nowDay, nowTod01))
			{
				// The damage tick above may have shifted or emptied the list (a
				// kill cascade), so `i` can no longer point at `s` — remove by
				// identity. -1 means a cascade already dropped it.
				int idx = _statusEffects.IndexOf(s);
				if (idx >= 0)
				{
					_statusEffects.RemoveAt(idx);
					EndFx(s);
				}
			}
		}
	}

	// Fold every active effect's StatModifier entries for a single stat into
	// the running value. Caller seeds with their inherent + equipment
	// composition (or the stat's neutral identity for a fresh compose);
	// the controller adds the status-effect layer. Multiplicative for most
	// stats, additive for the four additive ones (Camouflage / MaxStamina /
	// ColdResist / HeatResist) — the op is intrinsic to the stat and lives
	// on StatModifierUtil.IsAdditive.
	public float FoldStat(EStat stat, float running)
	{
		if (stat == EStat.None)
		{
			return running;
		}
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data == null)
			{
				continue;
			}
			running = StatModifierUtil.Fold(stat, data.modifiers, running);
			running = FoldConditional(data, running, mask: false, stat: stat, maskArg: EStat.None);
		}
		return running;
	}

	// Multiplicative fold across every active effect's StatModifier entries
	// whose single-bit stat overlaps `mask`. Used for hit-side composition
	// (a hit tagged Damage|Fire pulls in both the Damage and Fire entries)
	// at the various application sites — damage scale, armor-penetration-chance scale,
	// blunt chip scale, knockback magnitude, buildup amount. Caller seeds
	// with their inherent + equipment product; the controller adds the
	// status-effect layer.
	public float FoldMask(EStat mask, float product)
	{
		if (mask == EStat.None)
		{
			return product;
		}
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data == null)
			{
				continue;
			}
			product = StatModifierUtil.FoldMask(mask, data.modifiers, product);
			product = FoldConditional(data, product, mask: true, stat: EStat.None, maskArg: mask);
		}
		return product;
	}

	// Fold an effect's conditionalModifiers into `running` for whichever fold is in
	// flight — a single-stat Fold (mask == false, uses `stat`) or a tag-mask FoldMask
	// (mask == true, uses `maskArg`). Each group is included only when the owning
	// actor's evaluator reports its condition active; without an evaluator (mobs,
	// items) or a conditionalModifiers list, this is a no-op.
	private float FoldConditional(StatusEffectData data, float running, bool mask, EStat stat, EStat maskArg)
	{
		Godot.Collections.Array<ConditionalModifierData> groups = data.conditionalModifiers;
		if (groups == null || _conditionActive == null)
		{
			return running;
		}
		for (int g = 0; g < groups.Count; g++)
		{
			ConditionalModifierData group = groups[g];
			if (group?.modifiers == null || !_conditionActive(group.condition, group.parameter))
			{
				continue;
			}
			running = mask
				? StatModifierUtil.FoldMask(maskArg, group.modifiers, running)
				: StatModifierUtil.Fold(stat, group.modifiers, running);
		}
		return running;
	}

	// Self-apply each active effect's onDamagedEffect when a hit whose tags overlap
	// that effect's onDamagedTags lands on the wearer (Thin Skinned → its "+5% damage
	// taken" debuff on physical damage). Called from the actor's hit pipeline after
	// damage resolves. The applied effect's own maxStack governs whether repeat hits
	// stack or just refresh the timer. Snapshots the effects to apply first because
	// Add mutates _statusEffects mid-scan.
	public void TriggerOnDamaged(EStat hitTags)
	{
		List<StatusEffectData> toApply = null;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data?.onDamagedEffect == null)
			{
				continue;
			}
			if (data.onDamagedTags != EStat.None && (data.onDamagedTags & hitTags) == 0)
			{
				continue;
			}
			(toApply ??= new List<StatusEffectData>()).Add(data.onDamagedEffect);
		}
		if (toApply == null)
		{
			return;
		}
		for (int i = 0; i < toApply.Count; i++)
		{
			Add(toApply[i]);
		}
	}

	// Show / hide every active effect's loop fx, following the owning body's
	// render-visibility. The Mob calls this from UpdateVisibility so a loop fx
	// — parented to the actor root, a sibling of the mesh, not under it — hides
	// when the mob is culled by perception instead of floating as orphaned
	// particles where an unseen body stands. Audio is intentionally left running:
	// AudioStreamPlayer3D already attenuates by distance, so an out-of-sight loop
	// is quiet on its own without a second cull seam. No-op when unchanged.
	public void SetLoopFxVisible(bool visible)
	{
		if (_loopFxVisible == visible)
		{
			return;
		}
		_loopFxVisible = visible;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			Fx loop = _statusEffects[i]?.loopInstance;
			if (loop != null && GodotObject.IsInstanceValid(loop))
			{
				loop.Visible = visible;
			}
		}
	}

	// Stop the loop fx and spawn the one-shot end cue. Called from both the
	// explicit Remove path and the Tick expiry branch so end-of-effect is
	// uniform regardless of how the effect was cleared.
	private void EndFx(StatusEffectState state)
	{
		if (state.loopInstance != null && GodotObject.IsInstanceValid(state.loopInstance))
		{
			state.loopInstance.Stop();
		}
		state.loopInstance = null;
		if (state.data?.endFx != null && _world != null && _actor != null)
		{
			Fx.Create(state.data.endFx, _world, _actor.GlobalPosition);
		}
	}
}
