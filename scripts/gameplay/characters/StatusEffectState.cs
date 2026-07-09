using Godot;
using System;

// Runtime instance of a status effect held by an actor (Player or Mob). One
// per AddStatusEffect call — multiple instances of the same data stack as a
// single HUD icon with a count and tick independently. Mirrors the
// ItemData → ItemState split: shared authored fields on data, mutable
// per-instance bookkeeping here.
public class StatusEffectState
{
	public readonly StatusEffectData data;

	// Upgrade tier for slotted forge upgrades (0 = unleveled / not a forge
	// upgrade). Stamped from the forge's level when applied. Intended to scale the
	// effect's magnitude, but the scaling is bespoke per effect and DEFERRED — for
	// now the level is stored + surfaced to UI only. See StatusEffectController.Add.
	public int level;

	// Game-time (ms) at which a Timed effect expires. 0 = no ms timer
	// (Persistent, TimeOfDay, or a paused situational timer — see PauseTimer).
	public ulong expireTimeMs;

	// Absolute time-of-day at which a TimeOfDay effect expires, in
	// WorldState.TimeOfDayAbsolute units (the time_scale clock, NOT GameTimeMs,
	// so "until sunrise" tracks the lighting cycle and survives fast-forward).
	// 0 = not a TimeOfDay effect.
	public double expireTimeOfDayAbsolute;

	// Span from apply to expiry for a TimeOfDay effect (in TimeOfDayAbsolute /
	// day units), captured at arm time so the HUD can render a 0..1 progress
	// bar. 0 for non-TimeOfDay effects.
	private double _timeOfDaySpan;

	// Seconds since the last per-second damage tick. Counts up to 1.0, then
	// the actor's TickStatusEffects applies one chunk of damagePerSecond and
	// subtracts 1.0. Decoupling the tick from the physics rate keeps damage
	// integer-stable even if dt jitters.
	public float tickAccumulator;

	// Loop fx parented to the actor while the effect is active. Set by the
	// actor's AddStatusEffect path when data.loopFx is set; Stop()'d and
	// nulled by the End path so the trailing audio + particles wind down.
	public Fx loopInstance;

	// Countdown to the next movement-trail drop (data.trailZoneScene), in
	// seconds. Ticked down by StatusEffectController.TickMovementTrail only
	// while the actor is moving (dashing/sprinting); a patch drops and the
	// timer re-arms to data.trailDropInterval when it reaches 0. Unused unless
	// the effect authors a trail.
	public float trailAccumulator;

	// Weapon-modifier scope, stamped when this effect is composed onto an item
	// as a weapon mod (StatusEffectController.AddWeaponMod from an
	// ItemDescriptor). Default AllAttacks so ordinary status effects (poison,
	// wet, ...) — which never set projectilePierceCount / detonate-on-contact —
	// behave as weapon-global no-ops. SpecificCharge restricts the mod to the
	// `weaponModChargeIndex` tier of the wielding weapon's ItemActionProfile.
	public EWeaponModScope weaponModScope = EWeaponModScope.AllAttacks;
	public int weaponModChargeIndex;

	public StatusEffectState(StatusEffectData data, ulong nowMs, double nowTimeOfDayAbsolute)
	{
		this.data = data;
		ArmTimer(nowMs, nowTimeOfDayAbsolute);
	}

	// True when this instance carries an active expiry timer of any kind (ms or
	// time-of-day). False for Persistent effects and paused situational timers.
	public bool IsTimed => expireTimeMs != 0 || expireTimeOfDayAbsolute != 0;

	// Whether the effect has reached its expiry on whichever clock it uses.
	public bool IsExpired(ulong nowMs, double nowTimeOfDayAbsolute)
	{
		if (expireTimeOfDayAbsolute != 0)
		{
			return nowTimeOfDayAbsolute >= expireTimeOfDayAbsolute;
		}
		return expireTimeMs != 0 && nowMs >= expireTimeMs;
	}

	// Fraction of lifetime remaining in [0, 1] for the HUD bar; 1 when the
	// effect carries no timer (persistent). Spans both clocks.
	public float RemainingProgress(ulong nowMs, double nowTimeOfDayAbsolute)
	{
		if (expireTimeOfDayAbsolute != 0)
		{
			if (_timeOfDaySpan <= 0.0)
			{
				return 0f;
			}
			double remaining = expireTimeOfDayAbsolute - nowTimeOfDayAbsolute;
			return Mathf.Clamp((float)(remaining / _timeOfDaySpan), 0f, 1f);
		}
		if (expireTimeMs != 0)
		{
			float total = (data?.duration ?? 0f) * 1000f;
			if (total <= 0f)
			{
				return 0f;
			}
			float remaining = expireTimeMs > nowMs ? expireTimeMs - nowMs : 0f;
			return Mathf.Clamp(remaining / total, 0f, 1f);
		}
		return 1f;
	}

	// Pause / resume the expiry timer for situational effects (e.g. wet pauses
	// while the player is in water and re-arms when they reach dry land).
	// Clears every clock; ArmTimer rebuilds whichever one data.durationType
	// selects, so each dry-out runs the full window even if a previous countdown
	// was partially elapsed before the player got wet again.
	public void PauseTimer()
	{
		expireTimeMs = 0;
		expireTimeOfDayAbsolute = 0;
		_timeOfDaySpan = 0.0;
	}

	// (Re)arm the expiry per data.durationType. Timed → now + duration; TimeOfDay
	// → the next crossing of data.timeOfDayTarget; Persistent (and Timed with
	// duration 0) → no timer, so the arming system or explicit Remove owns
	// lifetime.
	public void ArmTimer(ulong nowMs, double nowTimeOfDayAbsolute)
	{
		expireTimeMs = 0;
		expireTimeOfDayAbsolute = 0;
		_timeOfDaySpan = 0.0;
		if (data == null)
		{
			return;
		}
		switch (data.durationType)
		{
			case EDurationType.Timed:
				if (data.duration > 0f)
				{
					expireTimeMs = nowMs + (ulong)(data.duration * 1000f);
				}
				break;
			case EDurationType.TimeOfDay:
				double dayStart = Math.Floor(nowTimeOfDayAbsolute);
				double nowFraction = nowTimeOfDayAbsolute - dayStart;
				// Target later today, else the same time tomorrow. `==` picks
				// tomorrow so an effect armed exactly at the target time lasts a
				// full day rather than expiring instantly.
				double targetAbsolute = data.timeOfDayTarget > nowFraction
					? dayStart + data.timeOfDayTarget
					: dayStart + 1.0 + data.timeOfDayTarget;
				expireTimeOfDayAbsolute = targetAbsolute;
				_timeOfDaySpan = targetAbsolute - nowTimeOfDayAbsolute;
				break;
		}
	}
}
