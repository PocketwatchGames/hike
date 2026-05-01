using Godot;

[GlobalClass]
public partial class ItemData : Resource
{
	[Export] public StringName displayName = "";
	[Export] public int maxStack = 1;
	[Export] public Texture2D inventorySprite;

	public bool IsStackable => maxStack > 1;

	public virtual ItemState CreateState()
	{
		return new ItemState(this);
	}
}
