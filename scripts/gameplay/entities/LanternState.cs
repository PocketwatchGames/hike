// Carryable-lantern runtime state. Distinct from TorchSimState, which is the
// world-placed torch prop. Whether it is lit is not stored here: the inventory
// holds it for the lantern slot (Inventory.LitLantern). Its oil is not stored here
// either — it is the carrier's (Player.LanternOil), so any lantern a member
// lights burns from the same supply.
public class LanternState : ItemState
{
	public override LanternData data => _lanternData;
	private readonly LanternData _lanternData;

	public LanternState(LanternData d) : base(d)
	{
		_lanternData = d;
	}
}
