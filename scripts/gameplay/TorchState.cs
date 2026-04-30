// Carryable-torch runtime state. Distinct from TorchSimState, which is the
// world-placed torch prop. A TorchState lives in the player's consumable bag;
// when equipped, a hand-mounted visual is attached. Use toggles isActive
// (carrier light attach/detach) via the ToggleCarrierLight event handler.
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
			player?.SetCarrierLightActive(false);
		}
		base.OnUnequipped(player);
	}
}
