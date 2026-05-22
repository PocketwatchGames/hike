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

	// Renders a number with an explicit '+' on positive values. Used for
	// additive bonuses (resistance / camouflage / stamina bonus) where the
	// direction (boost vs penalty) is the player-facing meaning.
	public static string SignedNumber(float value)
	{
		string body = Number(value);
		return value > 0f ? "+" + body : body;
	}
}
