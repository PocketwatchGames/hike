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
	private class BuildupState
	{
		public float amount;
		public ulong decayStartMs;
	}

	readonly Node3D _actor;
	readonly World _world;
	// (signed health delta, armor pierce in [0, 1]). Pierce is meaningful only
	// for the damage path (delta < 0): it splits the chunk between armor chip
	// and direct HP loss. Heals (delta > 0) ignore pierce.
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
	readonly List<StatusEffectState> _statusEffects = new();
	readonly Dictionary<StatusEffectData, BuildupState> _buildups = new();

	public IReadOnlyList<StatusEffectState> StatusEffects => _statusEffects;

	public StatusEffectController(Node3D actor, World world, Action<float, float> applyHealthDelta, Func<EStat, float> composeMaskMul = null)
	{
		_actor = actor;
		_world = world;
		_applyHealthDelta = applyHealthDelta;
		_composeMaskMul = composeMaskMul;
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
	// the hit pipeline as the generalized "any hit wakes a CC'd actor" rule
	// — replaces the dizzy-specific RemoveAllOfType(_dizzyEffect) wake path.
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

	// Apply every buildup contribution in `hit` to this actor's per-effect
	// meters and fold each crossed-threshold effect's applyTrigger back onto
	// the HitInfo. Receivers call this between armor resolution and the
	// hitstun/knockback reads so an OnDizzy modifier can amplify those reads
	// on the same hit that landed dizzy. Passed by ref because ApplyTrigger
	// mutates the struct in-place; calling by value would discard the fold.
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
			// Buildup contributions are tagged by the receiving effect, not
			// the carrier hit — kun-kun's Dizzy vulnerability lifts any
			// buildup feeding a Dizzy-tagged effect regardless of what hit
			// landed it. effect.tags == None falls through to a 1x
			// multiplier so untagged buildups behave like the pre-resistance
			// system.
			float resistance = _composeMaskMul?.Invoke(entry.effect.tags) ?? 1f;
			bool applied = AddBuildup(entry.effect, entry.amount * hit.buildupAmountMultiplier * resistance);
			if (applied && entry.effect.applyTrigger != EDamageTrigger.None)
			{
				hit.ApplyTrigger(entry.effect.applyTrigger);
			}
		}
	}

	// Add a buildup contribution and apply the effect if the meter crossed the
	// threshold. Returns true when at least one apply fired this call — the
	// caller (HurtBox hit path) uses that to fire the effect's applyTrigger so
	// modifier folds (OnDizzy extra knockback, etc.) land on the same hit. A
	// single fat contribution can cross multiple times: the loop reapplies
	// until the meter is under 1 (or clearBuildupOnApply zeros it on the
	// first cross).
	public bool AddBuildup(StatusEffectData data, float amount)
	{
		if (data == null || amount <= 0f)
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

	public StatusEffectState Add(StatusEffectData data)
	{
		if (data == null)
		{
			return null;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		// Mutual-exclusion pass — drop any active states (and their charging
		// buildup meters) listed in this effect's removesOnApply. Wet lists
		// Burning so stepping into water clears the burn the moment the wet
		// stack lands. Runs before the stack-cap branch so a same-frame
		// re-add of `data` itself can't get tangled with its own removal.
		if (data.removesOnApply != null)
		{
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
				}
			}
		}
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
				oldest.ArmTimer(now);
				if (data.startFx != null && _world != null)
				{
					Fx.Create(data.startFx, _world, _actor.GlobalPosition);
				}
				return oldest;
			}
		}
		var state = new StatusEffectState(data, now);
		_statusEffects.Add(state);
		if (data.startFx != null && _world != null)
		{
			Fx.Create(data.startFx, _world, _actor.GlobalPosition);
		}
		if (data.loopFx != null)
		{
			state.loopInstance = Fx.Create(data.loopFx, _actor, Vector3.Zero);
		}
		return state;
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
	// Persistent effects (expireTimeMs == 0) survive forever and rely on
	// gameplay code to call Remove explicitly. Also drains the buildup meters
	// past their per-effect decay delay so a partially-charged state empties
	// when the source stops hitting.
	public void Tick(float dt)
	{
		ulong now = _world?.GameTimeMs ?? 0;
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
			StatusEffectState s = _statusEffects[i];
			if (s.data == null)
			{
				_statusEffects.RemoveAt(i);
				continue;
			}
			s.tickAccumulator += dt;
			while (s.tickAccumulator >= 1f)
			{
				s.tickAccumulator -= 1f;
				if (s.data.damagePerSecond != 0f)
				{
					// Damage ticks (positive damagePerSecond) scale by the
					// actor's full resistance to the effect's tags so a
					// Fire-resistant body shrugs off a Burning burn tick.
					// Heals (negative damagePerSecond) pass through neat —
					// resistance is a damage-side concept; we don't want a
					// Magical-resistant target healing slower from a Magical-
					// tagged regen, and the explicit guard avoids a silly
					// >1 vulnerability scaling a heal up either.
					float dps = s.data.damagePerSecond;
					if (dps > 0f)
					{
						float resistance = _composeMaskMul?.Invoke(s.data.tags) ?? 1f;
						dps *= resistance;
					}
					_applyHealthDelta(-dps, s.data.pierce);
				}
			}
			if (s.IsTimed && now >= s.expireTimeMs)
			{
				_statusEffects.RemoveAt(i);
				EndFx(s);
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
	// at the various application sites — damage scale, pierce-chance scale,
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
		if (state.data?.endFx != null && _world != null)
		{
			Fx.Create(state.data.endFx, _world, _actor.GlobalPosition);
		}
	}
}
