using Godot;

// Per-actor rollup for HitInfo.dot damage / heal ticks. A burn or poison
// zone fires every physics frame; without this accumulator the HUD would
// spawn a fresh floating number per frame. Add* sums the deltas; Tick
// flushes one onDamage / onHeal invocation per second carrying the
// accumulated value, then resets. Non-DoT hits go straight through
// GameClient.onDamage / onHeal and bypass this entirely.
public class DotHudAccumulator
{
	const ulong FlushIntervalMs = 1000;

	float _damage;
	float _heal;
	ulong _nextFlushMs;

	public void AddDamage(float amount)
	{
		if (amount > 0f) { _damage += amount; }
	}

	public void AddHeal(float amount)
	{
		if (amount > 0f) { _heal += amount; }
	}

	public void Tick(ulong nowMs, Vector3 position)
	{
		// Arm the flush deadline on the first tick after construction (or
		// after a previous flush) so the first DoT chunk gets a full second
		// to accumulate rather than emitting on the very first physics frame.
		if (_nextFlushMs == 0)
		{
			_nextFlushMs = nowMs + FlushIntervalMs;
			return;
		}
		if (nowMs < _nextFlushMs) { return; }
		GameClient client = GameClient.Current;
		if (client != null)
		{
			if (_damage > 0f)
			{
				client.onDamage?.Invoke(position, _damage, EHudTextType.DamageLight);
			}
			if (_heal > 0f)
			{
				client.onHeal?.Invoke(position, _heal, EHudTextType.HealLight);
			}
		}
		_damage = 0f;
		_heal = 0f;
		_nextFlushMs = nowMs + FlushIntervalMs;
	}
}
