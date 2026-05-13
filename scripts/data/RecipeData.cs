using Godot;
using Godot.Collections;

[GlobalClass]
public partial class RecipeData : Resource
{
	[Export] public EForgeType forgeType;
	[Export] public ItemData outputHighQuality;
	[Export] public ItemData outputStandard;
	[Export] public Array<RecipeInput> inputs = new();
}
