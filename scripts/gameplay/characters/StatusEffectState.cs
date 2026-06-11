// Runtime instance of a status effect held by an actor (Player or Mob). One
// per AddStatusEffect call — multiple instances of the same data stack as a
// single HUD icon with a count and tick independently. Mirrors the
// ItemData → ItemState split: shared authored fields on data, mutable
// per-instance bookkeeping here.
public class StatusEffectState
{
	public readonly StatusEffectData data;

	// Game-time at which this effect expires. 0 = persistent (no timer);
	// gameplay code (e.g. the wet-after-swim trigger) arms a timer later by
	// writing here directly when the situational condition clears.
	public ulong expireTimeMs;

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

	public StatusEffectState(StatusEffectData data, ulong nowMs)
	{
		this.data = data;
		if (data != null && data.duration > 0f)
		{
			expireTimeMs = nowMs + (ulong)(data.duration * 1000f);
		}
	}

	public bool IsTimed => expireTimeMs != 0;

	public ulong RemainingMs(ulong nowMs) => expireTimeMs > nowMs ? expireTimeMs - nowMs : 0;

	// Pause / resume the expiry timer for situational effects (e.g. wet pauses
	// while the player is in water and re-arms when they reach dry land).
	// Pausing sets expireTimeMs to 0 (the same sentinel the constructor uses
	// for `data.duration == 0`); arming writes a fresh now+duration so each
	// dry-out runs the full window even if a previous countdown was partially
	// elapsed before the player got wet again.
	public void PauseTimer()
	{
		expireTimeMs = 0;
	}

	public void ArmTimer(ulong nowMs)
	{
		if (data == null || data.duration <= 0f)
		{
			return;
		}
		expireTimeMs = nowMs + (ulong)(data.duration * 1000f);
	}
}
