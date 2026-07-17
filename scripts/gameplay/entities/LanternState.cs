// Carryable-lantern runtime state. Distinct from TorchSimState, which is the
// world-placed torch prop. A LanternState lives in the player's consumable bag;
// when the carried lantern's isActive is true, the player emits a MovingLight
// regardless of which consumable slot it sits in (active hotbar slot or
// otherwise). Only the explicit ToggleMovingLight event handler — bound to
// the lantern's Use action — changes isActive. Slot switches, drops, and
// pickups leave it untouched, so a lit lantern stays lit until the player
// turns it off.
public class LanternState : ConsumableState
{
	private readonly LanternData _lanternData;

	// Remaining burn budget, in sim-ms. Counts down only while lit
	// (Player.TickLanternFuel) and is refilled to full at each sunrise, on respawn,
	// or at a fountain (Refuel). Ignored entirely when the lantern has unlimited fuel.
	public long FuelRemainingMs;

	public LanternState(LanternData d) : base(d)
	{
		_lanternData = d;
		FuelRemainingMs = d.BurnTimeMs;
	}

	// Whether the lantern can be lit / stay lit: either it burns forever or it
	// still has fuel left. The relight gate reads this.
	public bool HasFuel => !_lanternData.HasLimitedFuel || FuelRemainingMs > 0;

	// Recharge to a full tank — the sunrise / respawn / fountain refuel.
	public void Refuel()
	{
		FuelRemainingMs = _lanternData.BurnTimeMs;
	}

	// Spend `elapsedMs` of the fuel budget while lit. Returns true on the tick
	// the tank runs dry, so the caller can extinguish the flame. No-op (returns
	// false) for unlimited lanterns or ones already empty.
	public bool BurnFuel(long elapsedMs)
	{
		if (!_lanternData.HasLimitedFuel || FuelRemainingMs <= 0)
		{
			return false;
		}
		FuelRemainingMs -= elapsedMs;
		if (FuelRemainingMs <= 0)
		{
			FuelRemainingMs = 0;
			return true;
		}
		return false;
	}

	// Discrete one-shot spend for a fuel-costed action (a lantern spell cast),
	// as opposed to BurnFuel's continuous while-lit drain. Spends up to `ms`,
	// clamping the tank at 0 — a near-empty lantern still pays what it can and
	// bottoms out rather than going negative. No-op for unlimited lanterns.
	public void SpendFuel(long ms)
	{
		if (!_lanternData.HasLimitedFuel)
		{
			return;
		}
		FuelRemainingMs = System.Math.Max(0, FuelRemainingMs - ms);
	}
}
