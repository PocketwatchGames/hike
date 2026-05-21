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

	// Renders a positive multiplier as a percent of normal ("75%" for 0.75,
	// "125%" for 1.25). Differs from Percent in not clamping to 100, since
	// status-effect scalers can exceed 1 (a buff that doubles a stat).
	public static string Scale(float multiplier)
	{
		int pct = Mathf.Max(0, Mathf.RoundToInt(multiplier * 100f));
		return pct + "%";
	}
}
