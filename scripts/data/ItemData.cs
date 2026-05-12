using Godot;

[GlobalClass]
public partial class ItemData : Resource
{
	[Export] public StringName displayName = "";
	[Export] public int maxStack = 1;
	[Export] public Texture2D inventorySprite;

	// World-pickup presentation. Scene materialises this item when it drops
	// on the ground (player drop, chest spill, world spawn); AutoPickup picks
	// between AutoLoot (walk over) and Loot (press Interact) behavior. Null
	// Scene means the item can't be physically dropped — quest-bound items
	// etc. can leave both null.
	[Export] public PackedScene Scene;
	[Export] public bool AutoPickup = true;

	public bool IsStackable => maxStack > 1;

	public virtual ItemState CreateState()
	{
		return new ItemState(this);
	}
}
