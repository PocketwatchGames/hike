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
	readonly Node3D _actor;
	readonly World _world;
	readonly Action<float> _applyHealthDelta;
	readonly List<StatusEffectState> _statusEffects = new();

	public IReadOnlyList<StatusEffectState> StatusEffects => _statusEffects;

	public StatusEffectController(Node3D actor, World world, Action<float> applyHealthDelta)
	{
		_actor = actor;
		_world = world;
		_applyHealthDelta = applyHealthDelta;
	}

	public bool Contains(StatusEffectState state) => state != null && _statusEffects.Contains(state);

	public StatusEffectState Add(StatusEffectData data)
	{
		if (data == null)
		{
			return null;
		}
		ulong now = _world?.GameTimeMs ?? 0;
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

	// Drop every active effect, running the per-effect EndFx so loop instances
	// stop and end cues fire. Used by Player.Respawn so a cold / wet / poisoned
	// corpse comes back clean.
	public void Clear()
	{
		for (int i = _statusEffects.Count - 1; i >= 0; i--)
		{
			EndFx(_statusEffects[i]);
		}
		_statusEffects.Clear();
	}

	// Per-second damagePerSecond chunks + expiry pruning. Iterates backwards
	// so a mid-loop removal doesn't shift indices for unvisited entries.
	// Persistent effects (expireTimeMs == 0) survive forever and rely on
	// gameplay code to call Remove explicitly.
	public void Tick(float dt)
	{
		if (_statusEffects.Count == 0)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
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
					_applyHealthDelta(-s.data.damagePerSecond);
				}
			}
			if (s.IsTimed && now >= s.expireTimeMs)
			{
				_statusEffects.RemoveAt(i);
				EndFx(s);
			}
		}
	}

	// Sums the per-effect footprint contributions so the actor's footprint
	// emitter can scale the per-ground FootprintData at spawn. Multiplicative
	// composition: two stacked Wet states double-multiply alpha and duration.
	public void GetFootprintMultipliers(out float alphaMultiplier, out float durationMultiplier)
	{
		alphaMultiplier = 1f;
		durationMultiplier = 1f;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data == null)
			{
				continue;
			}
			alphaMultiplier *= data.footprintAlphaMultiplier;
			durationMultiplier *= data.footprintDurationMultiplier;
		}
	}

	// Sums the per-effect resistance contributions. Player.cs uses these to
	// shift the thermal trigger thresholds; Mob doesn't call it. Lives on the
	// controller because the data lives on StatusEffectData and the iteration
	// shape is identical to GetFootprintMultipliers.
	public void GetThermalResistances(out float coldResistance, out float heatResistance)
	{
		coldResistance = 0f;
		heatResistance = 0f;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data == null)
			{
				continue;
			}
			coldResistance += data.coldResistance;
			heatResistance += data.heatResistance;
		}
	}

	// Sums the per-effect motion contributions. Both products start at 1 and
	// multiply each active effect's value, so two stacked Cold states slow
	// movement and animation by 0.75 * 0.75. Player and Mob call this each
	// physics tick to scale move speed and the sprite animator's playback.
	public void GetMovementMultipliers(out float movementMultiplier, out float animationSpeedMultiplier)
	{
		movementMultiplier = 1f;
		animationSpeedMultiplier = 1f;
		for (int i = 0; i < _statusEffects.Count; i++)
		{
			StatusEffectData data = _statusEffects[i]?.data;
			if (data == null)
			{
				continue;
			}
			movementMultiplier *= data.movementMultiplier;
			animationSpeedMultiplier *= data.animationSpeedMultiplier;
		}
	}

	// Product of every active effect's damageMultiplier. Player.OnHurtBoxHit
	// scales incoming healthDamage by this; an authored 0.0 multiplier on a
	// dash i-frame status reduces the product to 0, which the damage path
	// treats as "no hit landed" so impact effects and interrupts are skipped.
	public float DamageMultiplier
	{
		get
		{
			float product = 1f;
			for (int i = 0; i < _statusEffects.Count; i++)
			{
				StatusEffectData data = _statusEffects[i]?.data;
				if (data == null)
				{
					continue;
				}
				product *= data.damageMultiplier;
			}
			return product;
		}
	}

	// Sum of every active effect's maxStaminaBonus. Player.MaxStamina folds
	// this in so a Hydrated player gets +50 to their cap for the duration.
	public float MaxStaminaBonus
	{
		get
		{
			float sum = 0f;
			for (int i = 0; i < _statusEffects.Count; i++)
			{
				StatusEffectData data = _statusEffects[i]?.data;
				if (data == null)
				{
					continue;
				}
				sum += data.maxStaminaBonus;
			}
			return sum;
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
		if (state.data?.endFx != null && _world != null)
		{
			Fx.Create(state.data.endFx, _world, _actor.GlobalPosition);
		}
	}
}
