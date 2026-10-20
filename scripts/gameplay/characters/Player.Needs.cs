using Godot;

// Hunger, thirst and fatigue: three 0..1 meters (1 = fed / watered / rested)
// that drain on the in-world clock, so a nap or the skip to sunrise drains them
// with no special case. The controlled member settles as it ticks; an idle
// member does not tick, so it is settled from its timestamp when it is selected,
// sleeps or is saved — at base rates, since its status effects are frozen too.
public partial class Player
{
	private const int NeedCount = 3;
	// Smallest clock span the controlled member settles over: one in-game minute,
	// which keeps the drain-rate stat folds off the per-tick path.
	private const double NeedSettleIntervalDays = 1.0 / (Sim.HoursPerDay * 60.0);

	private readonly float[] _needs = { 1f, 1f, 1f };
	// WorldClockDays the meters were last settled to.
	private double _needsSettledClock;

	public float GetNeed(ENeed need) => _needs[(int)need];

	public void RestoreNeed(ENeed need, float amount)
	{
		if (amount <= 0f)
		{
			return;
		}
		SettleNeeds();
		_needs[(int)need] = Mathf.Min(1f, _needs[(int)need] + amount);
	}

	// Flat MaxStamina from how full the meters are (PlayerData.maxStaminaPerNeed each).
	private float NeedsStaminaBonus()
	{
		return (data?.maxStaminaPerNeed ?? 0f) * (_needs[0] + _needs[1] + _needs[2]);
	}

	private void TickNeeds()
	{
		if (_world != null && _world.WorldClockDays - _needsSettledClock >= NeedSettleIntervalDays)
		{
			SettleNeeds();
		}
	}

	// Drain the meters over the clock span since they were last settled.
	// drainFatigue = false when that span was spent asleep.
	public void SettleNeeds(bool drainFatigue = true)
	{
		if (_world == null || data == null)
		{
			return;
		}
		double now = _world.WorldClockDays;
		double hours = (now - _needsSettledClock) * Sim.HoursPerDay;
		_needsSettledClock = now;
		if (hours <= 0.0)
		{
			return;
		}
		for (int i = 0; i < NeedCount; i++)
		{
			ENeed need = (ENeed)i;
			float hoursToEmpty = NeedHoursToEmpty(need);
			if ((need == ENeed.Fatigue && !drainFatigue) || hoursToEmpty <= 0f)
			{
				continue;
			}
			float rate = IsActive ? ComposeStat(NeedDrainStat(need)) : 1f;
			_needs[i] = Mathf.Max(0f, _needs[i] - (float)(hours / hoursToEmpty) * rate);
		}
	}

	// Waking from `hours` of sleep (Sim.Sleep, which settled the meters as the
	// party lay down): the span drained hunger and thirst but not fatigue, and
	// sleepHoursForFullRest of it refills fatigue from empty.
	public void WakeFromSleep(double hours)
	{
		SettleNeeds(drainFatigue: false);
		float hoursForFull = data?.sleepHoursForFullRest ?? 0f;
		if (hoursForFull > 0f)
		{
			RestoreNeed(ENeed.Fatigue, (float)(hours / hoursForFull));
		}
	}

	// Every unit of stamina spent costs a little of each meter.
	private void SpendNeedsForStamina(float stamina)
	{
		float cost = stamina * (data?.needCostPerStamina ?? 0f);
		if (cost <= 0f)
		{
			return;
		}
		for (int i = 0; i < NeedCount; i++)
		{
			_needs[i] = Mathf.Max(0f, _needs[i] - cost);
		}
	}

	private void WriteNeeds(System.IO.BinaryWriter w)
	{
		SettleNeeds();
		for (int i = 0; i < NeedCount; i++)
		{
			w.Write(_needs[i]);
		}
	}

	// A save is written at a wake, so the restored clock is when they were settled.
	private void ReadNeeds(System.IO.BinaryReader r)
	{
		for (int i = 0; i < NeedCount; i++)
		{
			_needs[i] = Mathf.Clamp(r.ReadSingle(), 0f, 1f);
		}
		_needsSettledClock = _world?.WorldClockDays ?? 0.0;
	}

	private float NeedHoursToEmpty(ENeed need) => need switch
	{
		ENeed.Hunger => data.hungerHoursToEmpty,
		ENeed.Thirst => data.thirstHoursToEmpty,
		ENeed.Fatigue => data.fatigueHoursToEmpty,
		_ => 0f,
	};

	private static EStat NeedDrainStat(ENeed need) => need switch
	{
		ENeed.Hunger => EStat.HungerDrainRate,
		ENeed.Thirst => EStat.ThirstDrainRate,
		ENeed.Fatigue => EStat.FatigueDrainRate,
		_ => EStat.None,
	};
}
