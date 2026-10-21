using Godot;

// Hunger, thirst and fatigue: three DEPRIVATION values, in stages, that rise on
// the in-world clock with no ceiling (0 = fed / watered / rested), so a nap or
// the skip to sunrise raises them with no special case. One stage lasts
// PlayerData's <need>HoursPerStage. Three phases:
//   [0,1)  free.
//   [1,2)  MaxStamina penalty growing with progress through the phase.
//   [2,∞)  that penalty in full, plus MaxStamina and MaxHealth both falling
//          per stage past 2. The member dies when MaxHealth reaches 0.
//
// The controlled member settles as it ticks; an idle member does not tick, so it
// is settled from its timestamp when it is selected, sleeps or is saved — at
// base rates, since its status effects are frozen too. An idle member whose
// MaxHealth ran out dies on the first tick it is controlled.
//
// Thirst is a pool: the body's value plus every carried water flask (in hours).
// The flasks are drunk first, so the body — which the penalties and the HUD
// meter read — stays watered until they run dry.
public partial class Player
{
	private const int NeedCount = 3;
	// Phase index of the last, unbounded phase.
	public const int NeedLastPhase = 2;
	// Smallest clock span the controlled member settles over: one in-game minute,
	// which keeps the drain-rate stat folds off the per-tick path.
	private const double NeedSettleIntervalDays = 1.0 / (Sim.HoursPerDay * 60.0);

	// Stages of deprivation per need.
	private readonly float[] _needs = new float[NeedCount];
	// WorldClockDays the values were last settled to.
	private double _needsSettledClock;

	// 0 = free, 1 = the stamina phase, NeedLastPhase = the max-health phase.
	public int GetNeedPhase(ENeed need) => Mathf.Min(Mathf.FloorToInt(_needs[(int)need]), NeedLastPhase);

	// What is left of the current phase, 1..0. The last phase has no end, so 1.
	public float GetNeedPhaseRemaining(ENeed need)
	{
		float stages = _needs[(int)need];
		if (stages >= NeedLastPhase)
		{
			return 1f;
		}
		return 1f - (stages - Mathf.Floor(stages));
	}

	// Fill every carried water flask (a well; the body is RestoreNeed's).
	public void RefillWaterFlasks()
	{
		foreach (FlaskState flask in _waterFlasks)
		{
			flask.amount = flask.data.capacity;
		}
	}

	// Take `stages` of deprivation away.
	public void RestoreNeed(ENeed need, float stages)
	{
		if (stages <= 0f)
		{
			return;
		}
		SettleNeeds();
		_needs[(int)need] = Mathf.Max(0f, _needs[(int)need] - stages);
	}

	public void RestoreNeedFully(ENeed need)
	{
		SettleNeeds();
		_needs[(int)need] = 0f;
	}

	// Flat MaxStamina lost to deprivation past the first phase.
	private float NeedsStaminaPenalty()
	{
		if (data == null)
		{
			return 0f;
		}
		float penalty = 0f;
		for (int i = 0; i < NeedCount; i++)
		{
			float stages = _needs[i];
			penalty += data.needMiddleStaminaPenalty * Mathf.Clamp(stages - 1f, 0f, 1f);
			penalty += data.needLowestStaminaPenaltyPerStage * Mathf.Max(0f, stages - NeedLastPhase);
		}
		return penalty;
	}

	// Flat MaxHealth lost to deprivation into the last phase.
	private float NeedsMaxHealthPenalty()
	{
		if (data == null)
		{
			return 0f;
		}
		float stagesPast = 0f;
		for (int i = 0; i < NeedCount; i++)
		{
			stagesPast += Mathf.Max(0f, _needs[i] - NeedLastPhase);
		}
		return stagesPast * data.needMaxHealthPenaltyPerStage;
	}

	private void TickNeeds()
	{
		if (_world != null && _world.WorldClockDays - _needsSettledClock >= NeedSettleIntervalDays)
		{
			SettleNeeds();
		}
		if (IsDead || NeedsMaxHealthPenalty() <= 0f)
		{
			return;
		}
		float maxHealth = MaxHealth;
		if (maxHealth <= 0f)
		{
			Kill();
		}
		else if (_health + _drainedHealth > maxHealth)
		{
			_health = Mathf.Min(_health, maxHealth);
			_drainedHealth = Mathf.Max(0f, maxHealth - _health);
		}
	}

	// Deprive over the clock span since the values were last settled.
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
			float hoursPerStage = NeedHoursPerStage(need);
			if ((need == ENeed.Fatigue && !drainFatigue) || hoursPerStage <= 0f)
			{
				continue;
			}
			float rate = IsActive ? ComposeStat(NeedDrainStat(need)) : 1f;
			Deprive(need, (float)hours * rate);
		}
	}

	// Waking from `hours` of sleep (Sim.Sleep, which settled the values as the
	// party lay down): the span deprived hunger and thirst but not fatigue, and
	// every sleepHoursPerFatigueStage of it takes a stage of fatigue away.
	public void WakeFromSleep(double hours)
	{
		SettleNeeds(drainFatigue: false);
		float hoursPerStage = data?.sleepHoursPerFatigueStage ?? 0f;
		if (hoursPerStage > 0f)
		{
			RestoreNeed(ENeed.Fatigue, (float)(hours / hoursPerStage));
		}
	}

	// A death wake: cap every value at needStagesAfterRespawn, so the sleep to
	// sunrise that follows cannot carry it back to a death.
	private void CapNeedsForRespawn()
	{
		float cap = data?.needStagesAfterRespawn ?? 0f;
		for (int i = 0; i < NeedCount; i++)
		{
			_needs[i] = Mathf.Min(_needs[i], cap);
		}
	}

	// Every unit of stamina spent costs a little of each need.
	private void SpendNeedsForStamina(float stamina)
	{
		float cost = stamina * (data?.needCostPerStamina ?? 0f);
		if (cost <= 0f || data == null)
		{
			return;
		}
		for (int i = 0; i < NeedCount; i++)
		{
			ENeed need = (ENeed)i;
			Deprive(need, cost * NeedHoursPerStage(need));
		}
	}

	// Add `hours` of deprivation. Every thirst source goes through here so
	// carried water is drunk before the body goes thirsty.
	private void Deprive(ENeed need, float hours)
	{
		float hoursPerStage = NeedHoursPerStage(need);
		if (hours <= 0f || hoursPerStage <= 0f)
		{
			return;
		}
		if (need == ENeed.Thirst)
		{
			foreach (FlaskState flask in _waterFlasks)
			{
				if (hours <= 0f)
				{
					return;
				}
				hours = flask.Draw(hours);
			}
		}
		_needs[(int)need] += hours / hoursPerStage;
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
			_needs[i] = Mathf.Max(0f, r.ReadSingle());
		}
		_needsSettledClock = _world?.WorldClockDays ?? 0.0;
	}

	private float NeedHoursPerStage(ENeed need) => need switch
	{
		ENeed.Hunger => data.hungerHoursPerStage,
		ENeed.Thirst => data.thirstHoursPerStage,
		ENeed.Fatigue => data.fatigueHoursPerStage,
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
