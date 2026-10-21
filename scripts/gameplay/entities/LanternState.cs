// Carryable-lantern runtime state. Distinct from TorchSimState, which is the
// world-placed torch prop. Whether it is lit is not stored here: the inventory
// holds it for the lantern slot (Inventory.LitLantern). The oil in it is — only
// the equipped lantern burns, and the carrier's oil flasks back it up
// (Player.LanternOil).
public class LanternState : ItemState
{
	public override LanternData data => _lanternData;
	private readonly LanternData _lanternData;

	public float oil;

	public LanternState(LanternData d) : base(d)
	{
		_lanternData = d;
		oil = d.oilCapacity;
	}

	public override void WriteSubclassState(System.IO.BinaryWriter w)
	{
		w.Write(oil);
	}

	public override void ReadSubclassState(System.IO.BinaryReader r)
	{
		oil = Godot.Mathf.Clamp(r.ReadSingle(), 0f, _lanternData.oilCapacity);
	}
}
