using Godot;

// Per-actor rollup for HitInfo.dot damage / heal ticks. A burn or poison
// zone fires every physics frame; without this accumulator the HUD would
// spawn a fresh floating number per frame. Add* sums the deltas; Tick
// flushes one onDamage / onHeal invocation per second carrying the
// accumulated value, then resets. Non-DoT hits go straight through
// GameClient.onDamage / onHeal and bypass this entirely.
//
// Tick returns a DotHudFlush bool pair so callers (Mob, Player) can fire
// receiver-side hit audio in sync with the flush — continuous damage has
// no per-frame fx; the once-per-second "ouch" rides on the same heartbeat
// as the floating number.
public struct DotHudFlush
{
	public bool damage;
	public bool heal;
	// Amount actually flushed this tick (matches the value pushed through
	// GameClient.onDamage / onHeal). Lets callers fire receiver-side
	// effects whose intensity scales with the rollup — e.g. the player's
	// red damage-flash, which would otherwise be invisible for DoT.
	public float damageAmount;
	public float healAmount;
}

public class DotHudAccumulator
{
	const ulong FlushIntervalMs = 1000;

	float _damage;
	float _heal;
	ulong _nextFlushMs;
	ulong _lastDamageMs;
	ulong _lastHealMs;

	public void AddDamage(float amount)
	{
		if (amount > 0f)
		{
			_damage += amount;
			_lastDamageMs = Time.GetTicksMsec();
		}
	}

	public void AddHeal(float amount)
	{
		if (amount > 0f)
		{
			_heal += amount;
			_lastHealMs = Time.GetTicksMsec();
		}
	}

	// `showHudFeedback` is sampled at flush time (once per interval): when false
	// the pending rollup is still drained (so nothing dumps over empty space when
	// the actor later comes back into view) but spawns no floating number and
	// reports no flush — so the receiver-side "ouch" / damage-flash stays silent
	// for a mob the player can't currently see. Always true for the player.
	public DotHudFlush Tick(ulong nowMs, Vector3 position, bool showHudFeedback)
	{
		DotHudFlush flush = default;
		// Arm the flush deadline on the first tick after construction (or
		// after a previous flush) so the first DoT chunk gets a full second
		// to accumulate rather than emitting on the very first physics frame.
		if (_nextFlushMs == 0)
		{
			_nextFlushMs = nowMs + FlushIntervalMs;
			return flush;
		}
		if (nowMs < _nextFlushMs) { return flush; }
		GameClient client = GameClient.Current;
		if (client != null)
		{
			// Defer sub-1 values while the dot is still ticking so tiny per-second
			// chunks accumulate into a displayable number; flush whatever's left
			// once a full interval passes with no new event on that channel.
			bool damageStale = nowMs - _lastDamageMs >= FlushIntervalMs;
			if (_damage >= 1f || (_damage > 0f && damageStale))
			{
				if (showHudFeedback)
				{
					client.onDamage?.Invoke(position, _damage, EHudTextType.DamageLight);
					flush.damageAmount = _damage;
					flush.damage = true;
				}
				_damage = 0f;
			}
			bool healStale = nowMs - _lastHealMs >= FlushIntervalMs;
			if (_heal >= 1f || (_heal > 0f && healStale))
			{
				if (showHudFeedback)
				{
					client.onHeal?.Invoke(position, _heal, EHudTextType.HealLight);
					flush.healAmount = _heal;
					flush.heal = true;
				}
				_heal = 0f;
			}
		}
		_nextFlushMs = nowMs + FlushIntervalMs;
		return flush;
	}
}
