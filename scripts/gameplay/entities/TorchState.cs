// Carryable-torch runtime state. Distinct from TorchSimState, which is the
// world-placed torch prop. A TorchState lives in the player's consumable bag;
// when equipped, a hand-mounted visual is attached. Use toggles isActive
// (moving light attach/detach) via the ToggleMovingLight event handler.
// Unequipping (or fully consuming) extinguishes the light unconditionally —
// can't carry a lit torch in your bag.
public class TorchState : ConsumableState
{
	public TorchState(TorchData d) : base(d) { }

	public override void OnUnequipped(Player player)
	{
		if (isActive)
		{
			isActive = false;
			player?.SetMovingLightActive(false);
		}
		base.OnUnequipped(player);
	}
}
