// Carryable-lantern runtime state. Distinct from TorchSimState, which is the
// world-placed torch prop. Whether it is lit is not stored here: the inventory
// holds it (Inventory.LitLantern). It holds no oil — it burns the carrier's
// (Player.LanternOil).
public class LanternState : GearState
{
	public override LanternData data => _lanternData;
	private readonly LanternData _lanternData;

	public LanternState(LanternData d) : base(d)
	{
		_lanternData = d;
	}
}
