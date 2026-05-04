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
}
