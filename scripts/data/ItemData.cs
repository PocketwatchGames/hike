using Godot;

[GlobalClass]
public partial class ItemData : Resource
{
	[Export] public StringName displayName = "";
	// Inspector multiline flavor text. Shown on the inventory screen's
	// ItemInfoPanel when an item is highlighted. Plain string (not localized)
	// to match displayName's StringName convention.
	[Export(PropertyHint.MultilineText)] public string description = "";
	[Export] public int maxStack = 1;
	[Export] public Texture2D inventorySprite;

	// Smaller world-pickup icon, authored at chunky-pixel resolution
	// (16×16 by convention) so the Sprite3D on Loot renders one source pixel
	// per chunky screen pixel. Null falls back to inventorySprite, which is
	// fine for items whose inventory icon is already small but reads as huge
	// in the world for hi-res UI sprites (e.g. a 200×200 potion icon).
	[Export] public Texture2D worldSprite;

	public bool IsStackable => maxStack > 1;

	public virtual ItemState CreateState()
	{
		return new ItemState(this);
	}
}
