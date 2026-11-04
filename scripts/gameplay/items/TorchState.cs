// One torch. Its durability is lit seconds (worn through Player.WearGear, so
// wear mods scale the burn); unlike other gear it isn't kept broken — at zero it
// is destroyed (Player.TickCarriedLight). Whether it is lit lives on the inventory
// (Inventory.LitLight).
public class TorchState : GearState
{
	public override TorchData data => _torchData;
	private readonly TorchData _torchData;

	public TorchState(TorchData d) : base(d)
	{
		_torchData = d;
	}
}
