using Godot;

// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class ItemData : Resource
{
	[Export] public StringName displayName = "";
	// Placeholder name shown until the player identifies this item (e.g.
	// "unknown food", "unknown potion"). Empty = the item is always shown by
	// its real displayName. Identification fires the first time the player
	// uses the item (ItemEventHandlers.DoDecrementStack); the discovered set
	// lives in SimState.IdentifiedItems and is keyed by this resource,
	// so every recipe and inventory stack of the same ItemData reveals at
	// once. Items the player starts the run already knowing are seeded on
	// spawn via WorldStartData.initialKnowledge (ItemTeachable entries). See
	// SimState.GetItemDisplayName for the read-side.
	[Export] public StringName unidentifiedDisplayName = "";
	// Inspector multiline flavor text. Shown on the inventory screen's
	// ItemInfoPanel when an item is highlighted. Plain string (not localized)
	// to match displayName's StringName convention.
	[Export(PropertyHint.MultilineText)] public string description = "";
	[Export] public int maxStack = 1;

	// In-world days this item keeps before it spoils. 0 = never spoils (the
	// default). Perishables (meat, mushrooms) set this; on acquisition the deadline
	// (WorldClockDays + spoilDays) is stamped onto the acquired units as a spoil
	// cohort (ItemState.StampSpoilClock). A same-kind stack shows as ONE inventory
	// pile regardless of when its units were gathered — batches gathered at
	// different times coexist as cohorts and are consumed oldest-first — while
	// Sim.SweepDeadlines sheds only the cohorts whose deadline has passed. Dropped Loot never needs it: ResetSpawns sweeps it.
	[Export(PropertyHint.Range, "0,60,1,or_greater")] public int spoilDays;
	// Subjective worth of one unit. Mob.CalculatePersonalValue starts from this
	// and lets per-mob preferences scale it (a vegetarian villager values a
	// roast at 0, etc). Drives gift-loyalty gain on the merchant screen and is
	// the natural per-unit price knob for any future shop tier.
	[Export] public int value = 0;
	[Export] public Texture2D inventorySprite;

	// The icon an item slot draws. Every slot widget asks this rather than
	// reading inventorySprite, so an item whose look depends on being equipped
	// (a lit lantern) reads the same everywhere.
	public virtual Texture2D SlotIcon(bool active) => inventorySprite;

	// Coarse classification flags driving mob taste preferences — a dog values
	// Meat, a villager dislikes Gross. An item can carry several (a roast is
	// Meat | Food). See EItemType and MobData.itemPreferences.
	[Export] public EItemType typeTags = EItemType.None;

	// Slot classification (which equip slot / store this item belongs to). Left
	// at None it derives from the subclass (ComputeCategory); set an explicit
	// value only to reclassify a one-off item without a dedicated subclass.
	// Distinct from typeTags — this is single-valued and drives placement.
	[Export] public EItemCategory categoryOverride = EItemCategory.None;

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

	// Resolved slot classification: the explicit override when set, else the
	// per-subclass default.
	public EItemCategory Category => categoryOverride != EItemCategory.None ? categoryOverride : ComputeCategory();

	// Per-subclass default category. Base items (loot, meat, ingredients) are
	// Material; WeaponData / ArmorData / LanternData / ConsumableData /
	// ArrowLootData override.
	protected virtual EItemCategory ComputeCategory() => EItemCategory.Material;

	// This item's weight while it sits in an equipment slot. The total over
	// SimData.minWeightPenalty costs max stamina, run/swim speed and dash distance
	// (Player.PenalizedWeight). Only gear authors one — armor, shields and weapons
	// override it with their `weight` field.
	public virtual float EquipWeight => 0f;

	// A cooking / spell reagent; picked up on contact, and placed past the hotbar.
	public bool IsMaterial => Category == EItemCategory.Material;

	// Ammo never enters the inventory — a pickup reclaims it into its weapon.
	public bool IsCarriable => Category != EItemCategory.Ammo;

	// True when this item fills one of the equip slots — weapon, armor, shield,
	// lantern. Equipping moves it out of the backpack into the slot.
	public bool IsEquippable => EquipSlotKind != EInventorySlot.None;

	// The equip slot this item's category maps to, or None.
	public EInventorySlot EquipSlotKind => Category switch
	{
		EItemCategory.WeaponLeft => EInventorySlot.WeaponLeft,
		EItemCategory.WeaponRight => EInventorySlot.WeaponRight,
		EItemCategory.Armor => EInventorySlot.Armor,
		EItemCategory.Shield => EInventorySlot.Shield,
		EItemCategory.Lantern => EInventorySlot.Lantern,
		// The helmet slot is withdrawn from the inventory for now: helmets are
		// inert backpack items until it returns (map back to EInventorySlot.Helmet).
		EItemCategory.Helmet => EInventorySlot.None,
		_ => EInventorySlot.None,
	};

	public virtual ItemState CreateState()
	{
		return new ItemState(this);
	}
}
