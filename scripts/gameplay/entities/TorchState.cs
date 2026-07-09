// Carryable-torch runtime state. Distinct from TorchSimState, which is the
// world-placed torch prop. A TorchState lives in the player's consumable bag;
// when any carried torch's isActive is true, the player emits a MovingLight
// regardless of which consumable slot it sits in (active hotbar slot or
// otherwise). Only the explicit ToggleMovingLight event handler — bound to
// the torch's Use action — changes isActive. Slot switches, drops, and
// pickups leave it untouched, so a lit torch stays lit until the player
// turns it off.
public class TorchState : ConsumableState
{
	private readonly TorchData _torchData;

	// Remaining burn budget, in sim-ms. Counts down only while lit
	// (Player.TickTorchFuel) and is refilled to full when the player camps at a
	// campfire (Refuel). Ignored entirely when the torch has unlimited fuel.
	public long FuelRemainingMs;

	public TorchState(TorchData d) : base(d)
	{
		_torchData = d;
		FuelRemainingMs = d.BurnTimeMs;
	}

	// Whether the torch can be lit / stay lit: either it burns forever or it
	// still has fuel left. The relight gate reads this.
	public bool HasFuel => !_torchData.HasLimitedFuel || FuelRemainingMs > 0;

	// Recharge to a full tank — the campfire refuel.
	public void Refuel()
	{
		FuelRemainingMs = _torchData.BurnTimeMs;
	}

	// Spend `elapsedMs` of the fuel budget while lit. Returns true on the tick
	// the tank runs dry, so the caller can extinguish the flame. No-op (returns
	// false) for unlimited torches or ones already empty.
	public bool BurnFuel(long elapsedMs)
	{
		if (!_torchData.HasLimitedFuel || FuelRemainingMs <= 0)
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
}
