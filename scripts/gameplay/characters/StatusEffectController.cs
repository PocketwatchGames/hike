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
	readonly World _world;
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
	public StatusEffectController(Node3D actor, World world, Action<float, float> applyHealthDelta, Func<EStat, float> composeMaskMul = null, Action<float> applyMaxHealthDelta = null)
	{
		_actor = actor;
		_world = world;
		_applyHealthDelta = applyHealthDelta;
		_composeMaskMul = composeMaskMul;
		_applyMaxHealthDelta = applyMaxHealthDelta;
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
				if (s?.data?.dot != null && s.data.dot.damagePerSecond > 0f)
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

	// True when any active weapon mod reaching charge tier `chargeIndex` sets
	// projectilesDetonateOnContact (the "Fragile" mod).
	public bool ProjectilesDetonateOnContact(int chargeIndex)
	{
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			if (s?.data?.weaponMod?.projectilesDetonateOnContact == true && ModReachesCharge(s, chargeIndex))
			{
				return true;
			}
		}
		return false;
	}

	// Largest projectilePierceCount among active weapon mods reaching charge tier
	// `chargeIndex` (0 if none); DoProjectile maxes it against the event's base.
	public int ProjectilePierceCount(int chargeIndex)
	{
		int max = 0;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			WeaponModData mod = s?.data?.weaponMod;
			if (mod != null && mod.projectilePierceCount > max && ModReachesCharge(s, chargeIndex))
			{
				max = mod.projectilePierceCount;
			}
		}
		return max;
	}

	// Largest vampiric (lifesteal) fraction among active weapon mods reaching
	// charge tier `chargeIndex` (0 if none); the melee/hitscan handlers heal the
	// attacker by this fraction of the health damage a landed hit deals.
	public float Vampiric(int chargeIndex)
	{
		float max = 0f;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			WeaponModData mod = s?.data?.weaponMod;
			if (mod != null && mod.vampiric > max && ModReachesCharge(s, chargeIndex))
			{
				max = mod.vampiric;
			}
		}
		return max;
	}

	// Buildup contributions every active weapon mod reaching charge tier
	// `chargeIndex` funnels into the struck target's meters (a Venomous mob's
	// Poison buildup, etc.). Returns null when no reaching mod authors any, so
	// the common no-mod hot path allocates nothing.
	public Godot.Collections.Array<StatusEffectBuildup> WeaponModOnHitBuildups(int chargeIndex)
	{
		Godot.Collections.Array<StatusEffectBuildup> result = null;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			Godot.Collections.Array<StatusEffectBuildup> onHit = s?.data?.weaponMod?.onHitBuildups;
			if (onHit == null || onHit.Count == 0 || !ModReachesCharge(s, chargeIndex))
			{
				continue;
			}
			result ??= new Godot.Collections.Array<StatusEffectBuildup>();
			for (int j = 0; j < onHit.Count; j++)
			{
				result.Add(onHit[j]);
			}
		}
		return result;
	}

	// Chain-lightning payloads on active weapon mods reaching charge tier
	// `chargeIndex` (the Shocking mod). Returns null when none reach, so the
	// common no-mod hot path allocates nothing.
	public Godot.Collections.Array<ChainLightningData> WeaponModChainLightning(int chargeIndex)
	{
		Godot.Collections.Array<ChainLightningData> result = null;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			ChainLightningData chain = s?.data?.weaponMod?.chainLightning;
			if (chain == null || !ModReachesCharge(s, chargeIndex))
			{
				continue;
			}
			result ??= new Godot.Collections.Array<ChainLightningData>();
			result.Add(chain);
		}
		return result;
	}

	// Summed extra knockback distance (m/s) from active weapon mods reaching
	// charge tier `chargeIndex` (the Knockback mod); the melee/hitscan/projectile
	// paths add it to the hit's knockbackDistance. 0 if none reach.
	public float WeaponModKnockbackBonus(int chargeIndex)
	{
		float sum = 0f;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			WeaponModData mod = s?.data?.weaponMod;
			if (mod != null && mod.knockbackBonus != 0f && ModReachesCharge(s, chargeIndex))
			{
				sum += mod.knockbackBonus;
			}
		}
		return sum;
	}

	// Summed extra knockback lockout (seconds) from active weapon mods reaching
	// charge tier `chargeIndex`; added to the hit's knockbackTime. 0 if none reach.
	public float WeaponModKnockbackTimeBonus(int chargeIndex)
	{
		float sum = 0f;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			WeaponModData mod = s?.data?.weaponMod;
			if (mod != null && mod.knockbackTimeBonus != 0f && ModReachesCharge(s, chargeIndex))
			{
				sum += mod.knockbackTimeBonus;
			}
		}
		return sum;
	}

	// Idle Fx scenes from every active weapon mod (the held-weapon visual a mod
	// adds, e.g. a Flaming sword's flame). NOT charge-filtered — the idle fx
	// rides the weapon at rest, independent of which tier would fire. Returns
	// null when no mod authors one, so the common no-mod case allocates nothing.
	public Godot.Collections.Array<PackedScene> WeaponModIdleFx()
	{
		Godot.Collections.Array<PackedScene> result = null;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			PackedScene fx = _statusEffects[i]?.data?.weaponMod?.idleFx;
			if (fx == null)
			{
				continue;
			}
			result ??= new Godot.Collections.Array<PackedScene>();
			result.Add(fx);
		}
		return result;
	}

	// Projectile-loop Fx scenes from active weapon mods reaching charge tier
	// `chargeIndex` (a Flaming bow's flaming arrows). Layered on top of the
	// intrinsic trail authored as a child Fx of the projectile scene. Returns null when none
	// reach, so the common no-mod hot path allocates nothing.
	public Godot.Collections.Array<PackedScene> WeaponModProjectileFx(int chargeIndex)
	{
		Godot.Collections.Array<PackedScene> result = null;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			PackedScene fx = s?.data?.weaponMod?.projectileFx;
			if (fx == null || !ModReachesCharge(s, chargeIndex))
			{
				continue;
			}
			result ??= new Godot.Collections.Array<PackedScene>();
			result.Add(fx);
		}
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
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectState s = _statusEffects[i];
			WeaponModData mod = s?.data?.weaponMod;
			if (mod?.onAttackEvent == null || (mod.onAttackTrigger & trigger) == 0 || !ModReachesCharge(s, chargeIndex))
			{
				continue;
			}
			result ??= new Godot.Collections.Array<ItemEvent>();
			result.Add(mod.onAttackEvent);
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
	public void ApplyHitBuildups(ref HitInfo hit)
	{
		if (hit.buildups == null)
		{
			return;
		}
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
				continue;
			}
			// Buildup contributions are tagged by the receiving effect, not
			// the carrier hit — kun-kun's Dizzy vulnerability lifts any
			// buildup feeding a Dizzy-tagged effect regardless of what hit
			// landed it. effect.tags == None falls through to a 1x
			// multiplier (untagged buildups take no resistance scaling).
			float resistance = _composeMaskMul?.Invoke(entry.effect.tags) ?? 1f;
			bool applied = AddBuildup(entry.effect, entry.amount * hit.buildupAmountMultiplier * resistance);
			if (applied && entry.effect.applyTrigger != EDamageTrigger.None)
			{
				hit.ApplyTrigger(entry.effect.applyTrigger);
			}
		}
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

	public StatusEffectState Add(StatusEffectData data)
	{
		if (data == null)
		{
			return null;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		double nowTod = _world?.TimeOfDayAbsolute ?? 0.0;
		// Mutual-exclusion pass — drop any active states (and their charging
		// buildup meters) listed in this effect's removesOnApply. Runs before the
		// stack-cap branch so a same-frame re-add of `data` itself can't get
		// tangled with its own removal.
		ApplyRemovesOnApply(data);
		// Enforce data.maxStack by refreshing the oldest still-alive instance
		// instead of appending. List order is insertion order (Tick prunes in
		// place via RemoveAt) so the first match is the oldest. ArmTimer is a
		// no-op for persistent effects (duration == 0), which is fine — the
		// stack cap still suppresses the duplicate add.
		if (data.maxStack > 0)
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
				oldest.ArmTimer(now, nowTod);
				SpawnStartFx(data);
				return oldest;
			}
		}
		var state = new StatusEffectState(data, now, nowTod);
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
		double nowTod = _world?.TimeOfDayAbsolute ?? 0.0;
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
				if (s.IsExpired(now, nowTod))
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
				if (dot.damagePerSecond != 0f && _applyHealthDelta != null)
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
						dps *= resistance;
					}
					_applyHealthDelta(-dps, dot.armorPenetration);
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
			if (s.IsExpired(now, nowTod))
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
		}
		return product;
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
