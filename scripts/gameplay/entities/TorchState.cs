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
	public TorchState(TorchData d) : base(d) { }
}
