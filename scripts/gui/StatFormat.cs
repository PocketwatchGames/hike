using Godot;

// Value-to-string formatters shared by the item info / action / context
// panels. Centralized so a "10" damage hit and a "10m" range both round
// the same way (trailing ".0" dropped when the value is integral).
public static class StatFormat
{
	public static string Number(float value)
	{
		if (Mathf.Abs(value - Mathf.Round(value)) < 0.001f)
		{
			return Mathf.RoundToInt(value).ToString();
		}
		return value.ToString("0.##");
	}

	public static string Meters(float meters)
	{
		return Number(meters) + "m";
	}

	// Returns "10m" when scale <= 1 and "10-20m" when the action's charge
	// tier ramps range. Used for hitscan / projectile reach where holding
	// the input scales the damage event's range.
	public static string MeterSpan(float baseValue, float scale)
	{
		if (scale <= 1f)
		{
			return Meters(baseValue);
		}
		return Number(baseValue) + "-" + Number(baseValue * scale) + "m";
	}

	public static string Seconds(float seconds)
	{
		return Number(seconds) + "s";
	}

	// Player-facing lifetime string for a status effect, dispatched by its
	// EDurationType: "10s" for a Timed effect, "Until sunrise" / "Until 18:00" for
	// an UntilTimeOfDay effect, empty for Persistent or a Timed effect with no fixed
	// duration (the arming system owns its lifetime — wet, etc.). Callers that
	// emit a labeled row should skip it when this returns empty.
	public static string Duration(StatusEffectData effect)
	{
		if (effect == null)
		{
			return string.Empty;
		}
		switch (effect.durationType)
		{
			case EDurationType.UntilTimeOfDay:
				return Mathf.IsZeroApprox(Mathf.PosMod(effect.expireAtTimeOfDay01, 1f))
					? Loc.Get(Loc.Keys.status_duration_until_sunrise)
					: Loc.Format(Loc.Keys.status_duration_until_time, ClockTime(effect.expireAtTimeOfDay01));
			case EDurationType.Timed:
				return effect.duration > 0f ? Seconds(effect.duration) : string.Empty;
			// Sustained (hot/cold) treats `duration` as a post-source grace window, not a
			// lifetime — read as persistent, so no numeric duration row.
			case EDurationType.Sustained:
				return string.Empty;
			default:
				return string.Empty;
		}
	}

	// Hours of the 24h clock at day-clock 0 (sunrise is 06:00).
	private const int SunriseHour = 6;
	private const int HoursPerDay = 24;
	private const int MinutesPerHour = 60;

	// A day-clock position (0 = sunrise) as a 24-hour "HH:MM".
	public static string ClockTime(float timeOfDay01)
	{
		int minutes = Mathf.RoundToInt(Mathf.PosMod(timeOfDay01, 1f) * HoursPerDay * MinutesPerHour);
		int total = (SunriseHour * MinutesPerHour + minutes) % (HoursPerDay * MinutesPerHour);
		return $"{total / MinutesPerHour:00}:{total % MinutesPerHour:00}";
	}

	public static string Percent(float fraction)
	{
		int pct = Mathf.Clamp(Mathf.RoundToInt(fraction * 100f), 0, 100);
		return pct + "%";
	}

	// Renders a multiplier as a signed delta from neutral 1.0 ("-25%" for
	// 0.75, "+50%" for 1.5). Reads cleaner than absolute scale on modifier
	// rows — "this armor reduces noise by 25%" lands faster than parsing
	// what "75%" means relative to baseline.
	public static string ScaleDelta(float multiplier)
	{
		int deltaPct = Mathf.RoundToInt((multiplier - 1f) * 100f);
		return deltaPct > 0 ? "+" + deltaPct + "%" : deltaPct + "%";
	}

	// Renders a combat scale as a leading-cross multiplier ("×4", "×2.5"). Used
	// for the level-derived outgoing damage/buildup scaling on forge upgrades.
	public static string Multiplier(float scale)
	{
		return "×" + Number(scale);
	}

	// Renders a number with an explicit '+' on positive values. Used for
	// additive bonuses (resistance / camouflage / stamina bonus) where the
	// direction (boost vs penalty) is the player-facing meaning.
	public static string SignedNumber(float value)
	{
		string body = Number(value);
		return value > 0f ? "+" + body : body;
	}
}
