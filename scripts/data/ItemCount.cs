using Godot;

[GlobalClass]
public partial class ItemCount : Resource
{
	[Export] public ItemData item;
	[Export] public int count = 1;
}
