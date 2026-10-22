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

	// --- On the ground (the shared Loot scene) ---
	// Lifetime of the world pickup in milliseconds; 0 = never expires. Raw meat
	// sets it so it rots off the ground if left. Once crossed the Loot plays its
	// removeFX and despawns.
	[Export] public int removeTimeMs = 0;

	// 3D model shown in place of the flat world sprite while on the ground, for an
	// item whose identity is a mesh rather than an icon (the crown reuses the
	// elite halo). The model supplies its own material and idle animation; the
	// inventorySprite is still used in the backpack.
	[Export] public PackedScene worldModel;

	// Hidden and non-interactive outside an authored time-of-day window, emerging
	// from the ground during it (night-only forage). Null = always present.
	[Export] public TimedEmergenceData timedEmergence;

	// What one unit breaks down into on Salvage (ItemTransfer.Salvage). Empty =
	// the item can't be salvaged.
	[Export] public Godot.Collections.Array<MaterialCount> salvage = new();
	// The tool a salvage needs carried, worn once per unit (a carcass needs a
	// Blade). None = bare hands.
	[Export] public EToolKind salvageTool = EToolKind.None;

	// What else this item serves as while carried (a knife is a Blade, a shovel a
	// Digging tool). Read through FindToolUse.
	[Export] public Godot.Collections.Array<ToolUse> toolUses = new();

	public bool IsStackable => maxStack > 1;

	public virtual bool IsSalvageable => salvage != null && salvage.Count > 0;

	// Whether an owned unit soaks and dries with its carrier (Player.TickWetEffect)
	// and so can carry the item-side Wet status: clothes, and guns' powder.
	public virtual bool TakesWetness => false;
	// Scales how fast this item dries; 1 = the environment's own rate.
	public virtual float WetnessDryMultiplier() => 1f;

	// Whether `salvager` can break one unit of this down right now. Tool work is
	// not done in combat (CombatTracker), the same rule as butchering and digging.
	public bool CanSalvage(Player salvager)
	{
		if (!IsSalvageable)
		{
			return false;
		}
		return salvageTool == EToolKind.None
			|| (salvager?.Inventory?.FindTool(salvageTool, out _) != null && !(GameClient.Current?.InCombat ?? false));
	}

	// Break one unit down into `into`, wearing the salvage tool. Nothing when the
	// tool has gone.
	public void Salvage(Player salvager, System.Collections.Generic.List<ItemState> into)
	{
		if (salvageTool != EToolKind.None && !salvager.UseTool(salvageTool))
		{
			return;
		}
		RollSalvageYields(into);
	}

	// The `salvage` rows: one stack per stackable yield, one state per unit
	// otherwise. A subclass may yield from elsewhere (CarcassData rolls its harvest).
	protected virtual void RollSalvageYields(System.Collections.Generic.List<ItemState> into)
	{
		foreach (MaterialCount yield in salvage)
		{
			if (yield?.item == null || yield.count <= 0)
			{
				continue;
			}
			int states = yield.item.IsStackable ? 1 : yield.count;
			for (int i = 0; i < states; i++)
			{
				ItemState state = yield.item.CreateState();
				if (yield.item.IsStackable)
				{
					state.SetCount(yield.count);
				}
				into.Add(state);
			}
		}
	}

	// The row saying this item serves as `kind`, or null. Read every frame while a
	// corpse or a dig spot is in reach, so it walks a managed mirror of toolUses.
	public ToolUse FindToolUse(EToolKind kind)
	{
		_toolUsesFlat ??= System.Linq.Enumerable.ToArray(toolUses ?? new Godot.Collections.Array<ToolUse>());
		for (int i = 0; i < _toolUsesFlat.Length; i++)
		{
			if (_toolUsesFlat[i] != null && _toolUsesFlat[i].kind == kind)
			{
				return _toolUsesFlat[i];
			}
		}
		return null;
	}
	private ToolUse[] _toolUsesFlat;

	// Resolved slot classification: the explicit override when set, else the
	// per-subclass default.
	public EItemCategory Category => categoryOverride != EItemCategory.None ? categoryOverride : ComputeCategory();

	// Per-subclass default category. Base items (loot, meat, ingredients) are
	// Material; WeaponData / ArmorData / LanternData / ConsumableData /
	// AmmoData override.
	protected virtual EItemCategory ComputeCategory() => EItemCategory.Material;

	// A cooking / spell reagent; picked up on contact, and placed past the hotbar.
	public bool IsMaterial => Category == EItemCategory.Material;

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
