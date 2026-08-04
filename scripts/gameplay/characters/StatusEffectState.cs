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
	// upgrade). Stamped from the forge's level when applied. Drives the shared
	// per-level power curve (SimData.LevelOutgoingScale / LevelIncomingResist):
	// an upgrade on the Melee/Ranged slot scales that weapon's outgoing damage +
	// buildups, one on the Armor slot scales incoming damage + buildup resist.
	// Read via StatusEffectController.ActiveUpgradeLevel.
	public int level;

	// Per-instance damage/heal magnitude scalar, captured at apply time from the
	// applying source's strength — a weapon/mob's OutgoingLevelScale for combat
	// DoTs, or an authored value for a source that customizes magnitude (a
	// "superior" heal = the base heal at potency 2). The DoT tick multiplies
	// dot.damagePerSecond by this, so a stronger source ticks bigger numbers as ONE
	// stack rather than needing more stacks. Stays per-instance and never merges:
	// a level-1 poison stack keeps ticking at level-1 even while a level-5 stack of
	// the same effect ticks alongside it. 1 = base authored numbers.
	public float potency = 1f;

	// Hazard that applied this instance (trap, fire column, gas cloud), stamped at
	// apply time. Non-null with a dot band replaces dot.damagePerSecond with a
	// per-second fraction of max health, so a hazard's burn stays proportionate to
	// whoever caught it long after they left the hazard. Null — every ordinary
	// weapon / consumable application — leaves the flat DoT path untouched, so the
	// same StatusEffectData ticks differently depending on what landed it.
	public HazardProfileData hazardProfile;

	// Game-time (ms) at which a Timed effect expires. 0 = no ms timer
	// (Persistent, TimeOfDay, or a paused situational timer — see PauseTimer).
	public ulong expireTimeMs;

	// Absolute time-of-day at which a TimeOfDay effect expires (= _expireDay +
	// _expireTimeOfDay01), in WorldState.TimeOfDayAbsolute units. Non-zero marks
	// a TimeOfDay effect and drives the HUD progress bar; the ACTUAL expiry test
	// uses the (_expireDay, _expireTimeOfDay01) pair below, not this sum. 0 = not
	// a TimeOfDay effect.
	public double expireTimeOfDayAbsolute;

	// Expiry as an explicit (DayNumber, time-of-day fraction) pair. Kept separate
	// from the summed absolute above because the awake clock stops at midnight
	// (tod 1.0), whose absolute (DayNumber + 1.0) numerically COLLIDES with the
	// next day's sunrise ((DayNumber+1) + 0.0). Comparing the sum would expire an
	// "until sunrise" boon at midnight — before the sleep-to-sunrise that is meant
	// to end it. IsExpired compares the day explicitly to avoid that.
	private int _expireDay;
	private double _expireTimeOfDay01;

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

	// Concrete forge upgrade slot this instance was applied to (Melee / Ranged /
	// Armor), or None for a non-forge effect. A SINGLE value — unlike
	// StatusEffectData.upgradeSlot, which is the eligibility FLAGS of which slots the
	// upgrade MAY go in: a ranged forge offering a Melee|Ranged-eligible upgrade
	// stamps Ranged here. Drives slot exclusivity (Add evicts the same-slot occupant)
	// and weapon-mod matching (a weapon folds in upgrades whose appliedUpgradeSlot
	// equals its slot).
	public EUpgradeSlot appliedUpgradeSlot = EUpgradeSlot.None;

	public StatusEffectState(StatusEffectData data, ulong nowMs, int nowDay, double nowTimeOfDay01)
	{
		this.data = data;
		ArmTimer(nowMs, nowDay, nowTimeOfDay01);
	}

	// True when this instance carries an active expiry timer of any kind (ms or
	// time-of-day). False for Persistent effects and paused situational timers.
	public bool IsTimed => expireTimeMs != 0 || expireTimeOfDayAbsolute != 0;

	// Whether the HUD should render a shrinking countdown bar. True only for a
	// plain Timed ms-expiry. A TimeOfDay ("until sunrise") effect is gated on the
	// player choosing to sleep, not on the wall clock — the awake clock freezes at
	// midnight and only the sleep-to-sunrise ends it — so a bar tracking the clock
	// would drain to empty while the effect is still active. We render those (and
	// paused/persistent effects) as persistent instead: icon only, no bar.
	public bool ShowsCountdownBar => expireTimeMs != 0;

	// Whether the effect has reached its expiry on whichever clock it uses.
	// The TimeOfDay branch compares the day explicitly so midnight of day N
	// (tod 1.0) does NOT satisfy a day-(N+1) sunrise deadline, even though the
	// two share the same summed absolute — an "until sunrise" boon must survive
	// the frozen midnight and end only once the sleep-to-sunrise rolls the day.
	public bool IsExpired(ulong nowMs, int nowDay, double nowTimeOfDay01)
	{
		if (expireTimeOfDayAbsolute != 0)
		{
			return nowDay > _expireDay || (nowDay == _expireDay && nowTimeOfDay01 >= _expireTimeOfDay01);
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
		_expireDay = 0;
		_expireTimeOfDay01 = 0.0;
		_timeOfDaySpan = 0.0;
	}

	// (Re)arm the expiry per data.durationType. Timed → now + duration; TimeOfDay
	// → the next crossing of data.timeOfDayTarget (tracked as an explicit day +
	// fraction, see the field comments); Persistent (and Timed with duration 0) →
	// no timer, so the arming system or explicit Remove owns lifetime.
	public void ArmTimer(ulong nowMs, int nowDay, double nowTimeOfDay01)
	{
		expireTimeMs = 0;
		expireTimeOfDayAbsolute = 0;
		_expireDay = 0;
		_expireTimeOfDay01 = 0.0;
		_timeOfDaySpan = 0.0;
		if (data == null)
		{
			return;
		}
		switch (data.durationType)
		{
			// Sustained arms the same grace ms-timer as Timed. While the sustaining
			// condition holds the external system (temperature) calls PauseTimer each
			// tick; this arm runs only once the condition clears, counting down the grace.
			case EDurationType.Timed:
			case EDurationType.Sustained:
				if (data.duration > 0f)
				{
					expireTimeMs = nowMs + (ulong)(data.duration * 1000f);
				}
				break;
			case EDurationType.TimeOfDay:
				_expireTimeOfDay01 = data.timeOfDayTarget;
				// Target later today, else the same time tomorrow. `>` (not `>=`)
				// sends an effect armed exactly at the target time to tomorrow so it
				// lasts a full day rather than expiring instantly.
				_expireDay = data.timeOfDayTarget > nowTimeOfDay01 ? nowDay : nowDay + 1;
				expireTimeOfDayAbsolute = _expireDay + _expireTimeOfDay01;
				_timeOfDaySpan = expireTimeOfDayAbsolute - (nowDay + nowTimeOfDay01);
				break;
		}
	}
}
