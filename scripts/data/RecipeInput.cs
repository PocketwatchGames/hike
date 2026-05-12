using Godot;

[GlobalClass]
public partial class RecipeInput : Resource
{
	[Export] public ItemData item;
	[Export] public int count = 1;
	[Export] public int minCountRange = 1;
	[Export] public int maxCountRange = 1;
}
