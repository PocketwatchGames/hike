using Godot;

[GlobalClass]
public partial class ItemData : Resource
{
	[Export] public StringName displayName = "";
	// Placeholder name shown until the player identifies this item (e.g.
	// "unknown food", "unknown potion"). Empty = the item is always shown by
	// its real displayName. Identification fires the first time the player
	// uses the item (ItemEventHandlers.DoDecrementStack); the discovered set
	// lives in WorldSimState.IdentifiedItems and is keyed by this resource,
	// so every recipe and inventory stack of the same ItemData reveals at
	// once. Items the player starts the run already knowing are listed on
	// PlayerSpawnData.initiallyIdentifiedItems and seeded into the set on
	// spawn. See WorldSimState.GetItemDisplayName for the read-side.
	[Export] public StringName unidentifiedDisplayName = "";
	// Inspector multiline flavor text. Shown on the inventory screen's
	// ItemInfoPanel when an item is highlighted. Plain string (not localized)
	// to match displayName's StringName convention.
	[Export(PropertyHint.MultilineText)] public string description = "";
	[Export] public int maxStack = 1;
	// Subjective worth of one unit. Mob.CalculatePersonalValue starts from this
	// and lets per-mob preferences scale it (a vegetarian villager values a
	// roast at 0, etc). Drives gift-loyalty gain on the merchant screen and is
	// the natural per-unit price knob for any future shop tier.
	[Export] public int value = 0;
	[Export] public Texture2D inventorySprite;

	// Smaller world-pickup icon, authored at chunky-pixel resolution
	// (16×16 by convention) so the Sprite3D on Loot renders one source pixel
	// per chunky screen pixel. Null falls back to inventorySprite, which is
	// fine for items whose inventory icon is already small but reads as huge
	// in the world for hi-res UI sprites (e.g. a 200×200 potion icon).
	[Export] public Texture2D worldSprite;

	public bool IsStackable => maxStack > 1;

	// Item-leveling cap. 0 = does not level (consumables, loot). Weapons and
	// armor override this with an exported value. WeaponState.AddExp /
	// ArmorState.AddExp walk SimData.ExpPerLevel up to this many entries.
	public virtual int maxLevel { get; set; } = 0;

	public virtual ItemState CreateState()
	{
		return new ItemState(this);
	}
}
