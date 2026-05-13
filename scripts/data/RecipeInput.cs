using Godot;

[GlobalClass]
public partial class RecipeInput : Resource
{
	[Export] public ItemData item;
	[Export] public int count = 1;
	[Export] public int minCountRange = 0;
	[Export] public int maxCountRange = 0;
}
