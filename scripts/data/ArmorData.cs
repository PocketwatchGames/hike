using Godot;

[GlobalClass]
public partial class ArmorData : ItemData
{
	[Export] public float maxArmor = 0f;
	[Export] public EInventorySlot armorSlot = EInventorySlot.ArmorBody;

	public override ItemState CreateState()
	{
		return new ArmorState(this);
	}
}
