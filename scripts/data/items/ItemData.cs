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

	// Coarse classification flags driving mob taste preferences — a dog values
	// Meat, a villager dislikes Gross. An item can carry several (a roast is
	// Meat | Food). See EItemType and MobData.itemPreferences.
	[Export] public EItemType typeTags = EItemType.None;

	// Optional "is-a" link to a more general item, walked by the recipe matcher
	// (Cooking.TryMatch): a recipe input naming a parent is satisfied by any
	// descendant, while one naming the descendant stays specific. e.g.
	// forest_goblin_meat.parent = goblin_meat lets a "needs goblin meat" recipe
	// accept any goblin subspecies meat. The parent is itself a real item (a
	// plain goblin can drop goblin_meat directly). This is ONLY a recipe
	// substitution relationship — it does NOT inherit field values (sprite,
	// value, etc. are authored per item). Chains may be any depth; cycles are
	// guarded against. Keep a chain within one ItemData subclass.
	[Export] public ItemData parent;

	// Optional 3D prop shown in the player's hand while this item is the one
	// being wielded / used — the sword that pops in when you swing, the potion
	// that appears while you drink. Distinct from the 2D inventorySprite /
	// worldSprite. HeldItemVisual instances this scene under a hand-bone socket;
	// the grip is baked into the scene itself (offset the mesh so the scene
	// origin sits in the palm) so no per-item transform authoring is needed.
	// Null = the item shows no in-hand model (most loot, ammo, etc.). Armor is
	// NOT wielded — it uses its own worn-mesh path, not this field.
	[Export] public PackedScene heldModel;

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
