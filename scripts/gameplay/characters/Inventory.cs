using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// Everything one party member carries: two sparse grids — the belt and the
// backpack — plus the equip slots, and which ammo is loaded per type. An
// equipped item lives IN its slot and is in neither grid — equipping moves it
// out, swapping whatever held the slot back into the grid slot it left. The hotbar is the lantern slot followed by the
// belt; a lantern is lit only in the lantern slot (LitLantern). Both grids' sizes are the
// PlayerData base plus whatever the worn armor adds (ArmorData.beltSlots /
// backpackSlots), re-applied on every change.
public class Inventory : IItemSource
{
	// One of the member's grids — the belt, the backpack, or a carried sack — as an
	// IItemGrid so ItemTransfer and the two-sided screens can move items in and out
	// of it. Every call routes back through the Inventory, so the touched / spoil
	// stamp on the way in and OnLeaving on the way out can't be skipped. Add and
	// CanFullyAdd speak for the whole member — this grid first, then the rest — so
	// a stash send fills the belt once the backpack is full.
	public sealed class CarriedGrid : IItemGrid
	{
		public readonly Inventory owner;
		internal readonly ItemGrid grid;
		// The sack whose contents this is; null for the belt and the backpack.
		public readonly SackState sack;
		// Set once the sack has left the member. A screen may still hold this
		// grid; detached, it reads empty and refuses everything.
		internal bool detached;

		internal CarriedGrid(Inventory owner, ItemGrid grid, SackState sack = null)
		{
			this.owner = owner;
			this.grid = grid;
			this.sack = sack;
		}

		// Slots[i] is the stack at slot i, or null. Count is the capacity.
		public IReadOnlyList<ItemState> Slots => detached ? Array.Empty<ItemState>() : grid.Slots;
		public int Capacity => detached ? 0 : grid.Capacity;
		public ItemState At(int index) => detached ? null : grid.At(index);
		public int IndexOf(ItemState item) => detached ? -1 : grid.IndexOf(item);
		public bool Accepts(ItemData data) => !detached && data != null && (sack == null || sack.Accepts(data));
		public ItemState Take(int index, int count) => owner.Take(this, index, count);
		public ItemState PlaceAt(int index, ItemState incoming, bool allowSwap, out ItemState displaced) =>
			owner.PlaceAt(this, index, incoming, allowSwap, out displaced);
		public ItemState Add(ItemState incoming) => owner.Add(this, incoming);
		public bool CanFullyAdd(ItemData data, int count) => owner.CanFullyAdd(data, count);
		// Units of `data` this grid alone has room for.
		public int RoomFor(ItemData data) => Accepts(data) ? grid.RoomFor(data) : 0;
		public bool MoveWithin(int from, int to, int count) => owner.Move(this, from, this, to, count);
	}

	private readonly Player _owner;
	private readonly PlayerData _data;

	// `grid[i]` is the item at slot i, or null — the player can leave gaps when
	// reorganizing.
	private readonly ItemGrid _beltGrid;
	private readonly ItemGrid _backpackGrid;

	// Indexed by EInventorySlot; null = empty. INVARIANT: an item is in exactly
	// one place — a grid slot or an equip slot, never both.
	private readonly ItemState[] _equipped = new ItemState[(int)EInventorySlot.Count];

	// Whether the lantern in the Lantern slot is lit. SetSlot clears it whenever
	// that slot changes occupant, so a lantern is never lit anywhere else.
	private bool _lanternLit;

	// Hotbar index of the selection (see HotbarItemAt), or -1 when the hotbar is
	// empty. Stored as an index (not an item) so the cursor holds its place when
	// the selected stack is used up; Changed() snaps it to the next filled slot.
	private int _hotbarSelected = -1;

	// The loaded kind per EAmmoType: what weapons of that type fire, drawn from
	// every carried stack of it. ReconcileLoadedAmmo keeps it on a kind the member
	// carries: when it runs out the type's standard round takes over, else the
	// first carried kind of the type.
	private readonly AmmoData[] _loadedAmmo = new AmmoData[(int)EAmmoType.Count];
	// Carried units of each loaded kind, refreshed on every change so the HUD and
	// the fire gates read it without walking the grids.
	private readonly int[] _loadedAmmoCount = new int[(int)EAmmoType.Count];

	// An equip slot changed occupant (always followed by onChanged).
	public Action<EInventorySlot> onSlotChanged;
	// Generic "something in the inventory changed" pulse, including a hotbar
	// selection move, the lantern being lit or put out, and a grid resizing. UI
	// panels that re-derive everything listen to this.
	public Action onChanged;

	// Fire onChanged from outside the Inventory class, for a change made to an
	// item without going through one of Inventory's mutation methods.
	public void NotifyChanged()
	{
		Changed();
	}

	public CarriedGrid Belt { get; }
	public CarriedGrid Backpack { get; }

	// One grid per sack in the belt or backpack, in slot order (belt first).
	// Rebuilt by SyncSacks; a sack keeps its CarriedGrid while it stays carried,
	// so a screen's reference to it stays valid across moves.
	public IReadOnlyList<CarriedGrid> Sacks => _sacks;
	private readonly List<CarriedGrid> _sacks = new();
	// Belt, backpack, then the sacks — every grid an owned item can sit in.
	private readonly List<CarriedGrid> _carried = new();
	// Scratch for AddPreferring's placement order.
	private readonly List<CarriedGrid> _placement = new();

	public Inventory(Player owner, PlayerData data)
	{
		_owner = owner;
		_data = data;
		_beltGrid = new ItemGrid(data.beltCapacity);
		_backpackGrid = new ItemGrid(data.backpackCapacity);
		Belt = new CarriedGrid(this, _beltGrid);
		Backpack = new CarriedGrid(this, _backpackGrid);
		_carried.Add(Belt);
		_carried.Add(Backpack);
	}

	private void Changed()
	{
		ApplyCapacity();
		SyncSacks();
		ReconcileHotbarSelection();
		ReconcileLoadedAmmo();
		onChanged?.Invoke();
	}

	// Re-derive the sack grids from what the belt and backpack hold. A sack that
	// left detaches its grid; one that stayed keeps it.
	private void SyncSacks()
	{
		var kept = new List<CarriedGrid>(_sacks.Count);
		CollectSacks(_beltGrid, kept);
		CollectSacks(_backpackGrid, kept);
		foreach (CarriedGrid old in _sacks)
		{
			if (!kept.Contains(old))
			{
				old.detached = true;
			}
		}
		_sacks.Clear();
		_sacks.AddRange(kept);
		_carried.RemoveRange(2, _carried.Count - 2);
		_carried.AddRange(_sacks);
	}

	private void CollectSacks(ItemGrid grid, List<CarriedGrid> into)
	{
		for (int i = 0; i < grid.Capacity; i++)
		{
			if (grid[i] is not SackState sack)
			{
				continue;
			}
			CarriedGrid existing = null;
			foreach (CarriedGrid g in _sacks)
			{
				if (g.sack == sack)
				{
					existing = g;
					break;
				}
			}
			into.Add(existing ?? new CarriedGrid(this, sack.contents, sack));
		}
	}

	// The grid holding `item` and its slot there; false when it's in none
	// (equipped, or not owned). Non-allocating.
	private bool Locate(ItemState item, out CarriedGrid grid, out int index)
	{
		for (int g = 0; g < _carried.Count; g++)
		{
			index = _carried[g].grid.IndexOf(item);
			if (index >= 0)
			{
				grid = _carried[g];
				return true;
			}
		}
		grid = null;
		index = -1;
		return false;
	}

	// True if `item` is owned — in a grid (a sack's included) or equipped.
	// Non-allocating (safe to call from per-frame paths).
	public bool Contains(ItemState item) => Locate(item, out _, out _) || IsEquipped(item);

	// An owned item serving as a `kind` tool — equipped, or in any carried grid, a
	// sack's included — skipping broken gear. `use` is its row for that kind.
	// Non-allocating: a corpse or dig spot in reach asks every frame.
	public ItemState FindTool(EToolKind kind, out ToolUse use)
	{
		for (int i = 0; i < _equipped.Length; i++)
		{
			use = SoundToolUse(_equipped[i], kind);
			if (use != null)
			{
				return _equipped[i];
			}
		}
		for (int g = 0; g < _carried.Count; g++)
		{
			ItemGrid grid = _carried[g].grid;
			for (int i = 0; i < grid.Capacity; i++)
			{
				use = SoundToolUse(grid[i], kind);
				if (use != null)
				{
					return grid[i];
				}
			}
		}
		use = null;
		return null;
	}

	private static ToolUse SoundToolUse(ItemState item, EToolKind kind)
	{
		if (item?.data == null || item is GearState { IsBroken: true })
		{
			return null;
		}
		return item.data.FindToolUse(kind);
	}

	// True if any grid or equip slot already holds a stack of exactly `data` (an
	// equipped thrown weapon's stack counts). Non-allocating.
	public bool HoldsStackOf(ItemData data)
	{
		if (data == null || !data.IsStackable)
		{
			return false;
		}
		if (GetEquipped(data.EquipSlotKind)?.data == data)
		{
			return true;
		}
		for (int g = 0; g < _carried.Count; g++)
		{
			ItemGrid grid = _carried[g].grid;
			for (int i = 0; i < grid.Capacity; i++)
			{
				if (grid[i]?.data == data)
				{
					return true;
				}
			}
		}
		return false;
	}

	// Where `data` lands first: only what the player uses from the hotbar takes
	// the belt; materials, flasks and equippable gear go in the backpack so they
	// don't crowd it out — only a full pack spills them onto the belt.
	public CarriedGrid PreferredGrid(ItemData data)
	{
		return data != null && data.Category != EItemCategory.Usable ? Backpack : Belt;
	}

	// Merge into matching stacks across every grid that accepts it, then take the
	// first empty slot — both passes in PlacementOrder, so a sack wins over the
	// backpack but an existing stack wins over a new sack slot. Returns what
	// didn't fit. Placement only — no bookkeeping, no Changed.
	private ItemState AddPreferring(CarriedGrid first, ItemState item)
	{
		if (item?.data == null || item.stackCount <= 0)
		{
			return null;
		}
		PlacementOrder(first, item.data, _placement);
		foreach (CarriedGrid g in _placement)
		{
			g.grid.MergeIntoStacks(item);
		}
		if (item.stackCount <= 0)
		{
			return null;
		}
		foreach (CarriedGrid g in _placement)
		{
			int index = g.grid.FirstEmpty();
			if (index >= 0)
			{
				g.grid[index] = item;
				return null;
			}
		}
		return item;
	}

	// The grids `data` may land in, best first: `first` when it is a sack, every
	// other sack that accepts it, then the belt and backpack — `first` leading
	// when it is one of them, else the item's PreferredGrid.
	private void PlacementOrder(CarriedGrid first, ItemData data, List<CarriedGrid> into)
	{
		into.Clear();
		if (first.sack != null && first.Accepts(data))
		{
			into.Add(first);
		}
		foreach (CarriedGrid sack in _sacks)
		{
			if (sack != first && sack.Accepts(data))
			{
				into.Add(sack);
			}
		}
		CarriedGrid main = first.sack == null ? first : PreferredGrid(data);
		into.Add(main);
		into.Add(OtherGrid(main));
	}

	// Spend up to `count` units of a reagent, matching by the item's parent chain
	// (a reagent naming goblin_meat draws from any goblin subspecies meat) —
	// the backpack first, then the belt, each with the sacks it holds. Emptied
	// stacks free their slot. Returns
	// how many units were actually spent (may be < count if the member ran short
	// — the caller covers the remainder from the party stash). Used by the
	// alchemy cast path. `spentItems`, when given, collects the items drawn.
	public int SpendMaterial(ItemData reagentItem, int count, List<SpentItem> spentItems = null)
	{
		if (reagentItem == null || count <= 0)
		{
			return 0;
		}
		int spent = _backpackGrid.SpendMaterial(reagentItem, count, spentItems);
		spent += _beltGrid.SpendMaterial(reagentItem, count - spent, spentItems);
		if (spent > 0)
		{
			Changed();
		}
		return spent;
	}

	public bool HasKeyFor(LockData lockData)
	{
		return KeyFor(lockData) != null;
	}

	// Spend one key that opens `lockData` and return which kind it was; null
	// when none is carried.
	public KeyData SpendKeyFor(LockData lockData)
	{
		ItemState key = KeyFor(lockData);
		if (key == null || !Locate(key, out CarriedGrid grid, out int index))
		{
			return null;
		}
		var keyData = (KeyData)key.data;
		key.Consume(1);
		if (key.stackCount <= 0)
		{
			grid.grid[index] = null;
		}
		Changed();
		return keyData;
	}

	// The key to spend on `lockData`: one made for it before a skeleton key, so a
	// skeleton key is the last resort. Null if none.
	private ItemState KeyFor(LockData lockData)
	{
		if (lockData == null)
		{
			return null;
		}
		ItemState skeleton = null;
		for (int g = 0; g < _carried.Count; g++)
		{
			ItemState made = KeyFor(_carried[g].grid, lockData, ref skeleton);
			if (made != null)
			{
				return made;
			}
		}
		return skeleton;
	}

	private static ItemState KeyFor(ItemGrid grid, LockData lockData, ref ItemState skeleton)
	{
		for (int i = 0; i < grid.Capacity; i++)
		{
			ItemState s = grid[i];
			if (s?.data is not KeyData key || s.stackCount <= 0 || !key.Opens(lockData))
			{
				continue;
			}
			if (key.opens.Contains(lockData))
			{
				return s;
			}
			skeleton ??= s;
		}
		return null;
	}

	// Null before the player has a live world (never during normal play).
	private ItemGrid PartyStash => _owner?.Sim?.WorldState?.SimState?.PartyStash;

	// Hand an item the player owns but can't carry (a starting loadout that
	// overflows the grids) to the party stash. Returns what the stash had no room
	// for.
	public ItemState PushToStash(ItemState item)
	{
		if (item == null)
		{
			return null;
		}
		return PartyStash != null ? PartyStash.Add(item) : item;
	}

	// Add an item to the member's grids: a sack that accepts it before its
	// PreferredGrid (see AddPreferring). Stackables fill existing same-kind stacks
	// first, then take the first empty slot. Returns the number of units actually added (0 = nothing fit, less
	// than stackCount = partial). The caller's ItemState is consumed: if it was
	// stored it is the grid's reference now; if it fully merged it is spent.
	// Never equips — see Acquire for that.
	public int TryAdd(ItemState item)
	{
		if (item == null || item.data == null || item.stackCount <= 0)
		{
			return 0;
		}
		int initialStack = item.stackCount;
		ItemState leftover = Add(PreferredGrid(item.data), item);
		return initialStack - (leftover?.stackCount ?? 0);
	}

	// IItemGrid.Add for any carried grid: `first` leads the placement order (see
	// PlacementOrder). Returns the leftover, null when everything went in.
	private ItemState Add(CarriedGrid first, ItemState incoming)
	{
		if (incoming?.data == null || incoming.stackCount <= 0 || first.detached)
		{
			return incoming;
		}
		// Entering the inventory marks the item as touched for the rest of its
		// life (see ItemState.touched). Stamp before the merge so even units
		// that fold into an existing stack count as handled.
		MarkAcquired(incoming);
		int before = incoming.stackCount;
		ItemState leftover = AddPreferring(first, incoming);
		if ((leftover?.stackCount ?? 0) != before)
		{
			Changed();
		}
		return leftover;
	}

	// Like Add, but into `grid` alone — nothing merges into or spills over to the
	// other grid. Returns the leftover, null when everything went in.
	public ItemState AddOnly(CarriedGrid grid, ItemState incoming)
	{
		if (grid?.owner != this || incoming?.data == null || incoming.stackCount <= 0 || !grid.Accepts(incoming.data))
		{
			return incoming;
		}
		MarkAcquired(incoming);
		int before = incoming.stackCount;
		ItemState leftover = grid.grid.Add(incoming);
		if ((leftover?.stackCount ?? 0) != before)
		{
			Changed();
		}
		return leftover;
	}

	// True when taking `data` would put it straight into an empty equip slot.
	public bool WouldAutoEquip(ItemData data)
	{
		return data != null && data.IsEquippable && GetEquipped(data.EquipSlotKind) == null;
	}

	// True if Acquire would take all `count` units — into the equipped stack of
	// the same kind, straight into an empty equip slot, or into the grids. Mirrors
	// Acquire without mutating anything.
	public bool CanAcquire(ItemData data, int count)
	{
		int topUp = EquippedRoomFor(data);
		return topUp >= count || WouldAutoEquip(data) || CanFullyAdd(data, count - topUp);
	}

	// Take ownership of an item from the world (a pickup, the starting loadout):
	// units of a kind already equipped top up that stack (a thrown weapon picked
	// back up), gear whose equip slot is empty is equipped on the spot, anything
	// else goes into the grids as TryAdd. Returns the number of units taken.
	public int Acquire(ItemState item)
	{
		if (item?.data == null || item.stackCount <= 0)
		{
			return 0;
		}
		int taken = TopUpEquipped(item);
		if (item.stackCount <= 0)
		{
			return taken;
		}
		if (!WouldAutoEquip(item.data))
		{
			return taken + TryAdd(item);
		}
		EquipIncoming(item);
		return taken + item.stackCount;
	}

	// Units of `data` the equipped stack of the same kind has room for.
	private int EquippedRoomFor(ItemData data)
	{
		ItemState equipped = data != null && data.IsStackable ? GetEquipped(data.EquipSlotKind) : null;
		return equipped?.data == data ? equipped.RemainingStackSpace() : 0;
	}

	// Merge what fits of `item` into the equipped stack of its kind. Returns the
	// units moved.
	private int TopUpEquipped(ItemState item)
	{
		int room = EquippedRoomFor(item.data);
		if (room <= 0)
		{
			return 0;
		}
		MarkAcquired(item);
		int moved = item.TransferTo(GetEquipped(item.data.EquipSlotKind), room);
		Changed();
		return moved;
	}

	// Equip a detached item from outside the inventory (the stash). Returns what
	// it displaced — detached and out of the inventory now — or `incoming` itself
	// when it isn't equippable.
	public ItemState EquipIncoming(ItemState incoming)
	{
		EInventorySlot slot = incoming?.data?.EquipSlotKind ?? EInventorySlot.None;
		if (slot == EInventorySlot.None)
		{
			return incoming;
		}
		MarkAcquired(incoming);
		ItemState displaced = _equipped[(int)slot];
		if (displaced != null)
		{
			OnLeaving(displaced);
		}
		SetSlot(slot, incoming);
		NotifySlot(slot);
		return displaced;
	}

	// Every path an item enters the inventory through routes its touched/spoil
	// bookkeeping here: mark it handled for life (see ItemState.touched) and, if
	// it's a perishable not already dated, start its spoil clock so it expires
	// spoilDays from now wherever it comes to rest (a grid, later the stash).
	private void MarkAcquired(ItemState item)
	{
		if (item == null)
		{
			return;
		}
		item.touched = true;
		if (item.data != null && item.data.spoilDays > 0)
		{
			// Already-dated cohorts (older units merged in earlier) keep their own
			// deadline, so re-acquiring a partly-aged stack never resets it.
			item.StampSpoilClock((_owner?.Sim?.WorldClockDays ?? 0.0) + item.data.spoilDays);
		}
	}

	// True if `count` units of `data` would ALL fit in the grids right now.
	// Mirrors TryAdd's placement WITHOUT mutating anything — pickup gates use it
	// so a pickup only commits (and a loot only flies in) when the whole stack
	// lands, never leaving a partial pile bonking the player.
	public bool CanFullyAdd(ItemData data, int count)
	{
		if (data == null || count <= 0)
		{
			return false;
		}
		int room = 0;
		for (int g = 0; g < _carried.Count && room < count; g++)
		{
			room += _carried[g].RoomFor(data);
		}
		return room >= count;
	}

	public bool Holds(ItemState item) => Contains(item);

	public void Spend(ItemState item, int count)
	{
		if (count <= 0 || !Contains(item))
		{
			return;
		}
		item.Consume(count);
		if (item.stackCount > 0)
		{
			Changed();
		}
		else
		{
			Remove(item);
		}
	}

	// Removes an item from the inventory entirely (dropped, sold, stashed, spent),
	// from a grid or an equip slot. No-op for an item that isn't owned.
	public void Remove(ItemState item)
	{
		bool inGrid = Locate(item, out CarriedGrid grid, out int index);
		if (!inGrid && !IsEquipped(item))
		{
			return;
		}
		OnLeaving(item);
		if (inGrid)
		{
			grid.grid[index] = null;
		}
		Changed();
	}

	// The single chokepoint for an item leaving the inventory, by any path. An
	// equipped item's slot is emptied. A sack's contents leave with it.
	private void OnLeaving(ItemState item)
	{
		if (item is SackState sack)
		{
			foreach (ItemState inner in sack.contents.EnumerateAll())
			{
				OnLeaving(inner);
			}
		}
		EInventorySlot? equippedSlot = GetEquippedSlot(item);
		if (equippedSlot.HasValue)
		{
			SetSlot(equippedSlot.Value, null);
			onSlotChanged?.Invoke(equippedSlot.Value);
		}
	}

	public ItemState GetEquipped(EInventorySlot slot)
	{
		if (slot <= EInventorySlot.None || slot >= EInventorySlot.Count)
		{
			return null;
		}
		return _equipped[(int)slot];
	}

	public WeaponState GetWeapon(EInventorySlot slot)
	{
		return slot == EInventorySlot.WeaponLeft || slot == EInventorySlot.WeaponRight
			? _equipped[(int)slot] as WeaponState
			: null;
	}

	public bool IsEquipped(ItemState item)
	{
		return GetEquippedSlot(item).HasValue;
	}

	public EInventorySlot? GetEquippedSlot(ItemState item)
	{
		if (item == null)
		{
			return null;
		}
		for (int i = 0; i < _equipped.Length; i++)
		{
			if (_equipped[i] == item)
			{
				return (EInventorySlot)i;
			}
		}
		return null;
	}

	// Equip an item from any grid: it moves into its category's slot, and
	// whatever held that slot moves into the grid slot it left — refused when that
	// grid (a sack) wouldn't take it. False for an item that isn't in a grid or
	// isn't equippable; true (no-op) if already equipped.
	public bool Equip(ItemState item)
	{
		EInventorySlot slot = item?.data?.EquipSlotKind ?? EInventorySlot.None;
		if (slot == EInventorySlot.None)
		{
			return false;
		}
		if (_equipped[(int)slot] == item)
		{
			return true;
		}
		if (!Locate(item, out CarriedGrid grid, out int index))
		{
			return false;
		}
		ItemState displaced = _equipped[(int)slot];
		if (displaced != null && !grid.Accepts(displaced.data))
		{
			return false;
		}
		grid.grid[index] = displaced;
		SetSlot(slot, item);
		NotifySlot(slot);
		return true;
	}

	// Empty an equip slot into the first free grid slot, its PreferredGrid first.
	// False when the slot is empty or both grids are full.
	public bool Unequip(EInventorySlot slot)
	{
		ItemState item = GetEquipped(slot);
		if (item == null)
		{
			return false;
		}
		CarriedGrid first = PreferredGrid(item.data);
		CarriedGrid second = first == Belt ? Backpack : Belt;
		int index = first.grid.FirstEmpty();
		if (index >= 0)
		{
			return UnequipTo(slot, first, index);
		}
		index = second.grid.FirstEmpty();
		return index >= 0 && UnequipTo(slot, second, index);
	}

	// Empty an equip slot into `grid` slot `index`. An empty slot takes it; one
	// holding gear for the same equip slot swaps with it (that gear is equipped).
	// False for anything else.
	public bool UnequipTo(EInventorySlot slot, CarriedGrid grid, int index)
	{
		ItemState item = GetEquipped(slot);
		if (item == null || grid?.owner != this || !grid.grid.InRange(index) || !grid.Accepts(item.data))
		{
			return false;
		}
		ItemState occupant = grid.grid[index];
		if (occupant != null)
		{
			return occupant.data?.EquipSlotKind == slot && Equip(occupant);
		}
		grid.grid[index] = item;
		SetSlot(slot, null);
		NotifySlot(slot);
		return true;
	}

	// Equip `item`, or unequip it if it already is. False when nothing changed.
	public bool ToggleEquip(ItemState item)
	{
		EInventorySlot? slot = GetEquippedSlot(item);
		return slot.HasValue ? Unequip(slot.Value) : Equip(item);
	}

	// Drop an item: remove from inventory, spawn a Loot in the world. Optional
	// stackCount splits a stack — when set and less than item.stackCount, the
	// original stays in inventory with reduced count and a fresh ItemState of
	// the same kind is spawned with the dropped count.
	public void Drop(ItemState item, int? stackCount = null)
	{
		if (item?.data == null || _owner == null || !Contains(item))
		{
			return;
		}
		if (IsEquipped(item))
		{
			Remove(item);
			_owner.DropAtFeet(item);
			return;
		}
		// Take carves a partial drop oldest-first with its real spoil cohorts, so
		// dropping half a stack neither resets nor starts a clock.
		int count = stackCount.HasValue ? Math.Max(1, stackCount.Value) : item.stackCount;
		CarriedGrid grid = GridOf(item);
		_owner.DropAtFeet(Take(grid, grid.IndexOf(item), count));
	}

	// ---- Capacity --------------------------------------------------------------

	// The slot counts the base data and the worn armor add up to.
	private void ComposeCapacity(out int belt, out int backpack)
	{
		belt = _data?.beltCapacity ?? 0;
		backpack = _data?.backpackCapacity ?? 0;
		foreach (ArmorState armor in EnumerateEquippedArmor())
		{
			if (armor.data is ArmorData armorData)
			{
				belt += armorData.beltSlots;
				backpack += armorData.backpackSlots;
			}
		}
		belt = Math.Max(0, belt);
		backpack = Math.Max(0, backpack);
	}

	// Resize both grids to what the worn armor allows. Run on every change, so
	// no equip path has to remember it. What sat in a lost slot is repacked into
	// free slots (its PreferredGrid first), and what doesn't fit is dropped at
	// the member's feet.
	private void ApplyCapacity()
	{
		ComposeCapacity(out int belt, out int backpack);
		if (belt == _beltGrid.Capacity && backpack == _backpackGrid.Capacity)
		{
			return;
		}
		var evicted = new List<ItemState>();
		_beltGrid.Resize(belt, evicted);
		_backpackGrid.Resize(backpack, evicted);
		// An evicted sack is in no grid now; repacking must not fill it.
		SyncSacks();
		foreach (ItemState item in evicted)
		{
			ItemState leftover = AddPreferring(PreferredGrid(item.data), item);
			if (leftover == null)
			{
				continue;
			}
			OnLeaving(leftover);
			if (_owner != null)
			{
				_owner.DropAtFeet(leftover);
			}
			else
			{
				GD.PushError($"Inventory: no room for '{leftover.data?.ResourcePath}' after a resize, and no owner to drop it — it is lost.");
			}
		}
	}

	// ---- Lantern ---------------------------------------------------------------

	// The lantern in the Lantern slot, lit or not; null when the slot is empty.
	public LanternState EquippedLantern => _equipped[(int)EInventorySlot.Lantern] as LanternState;

	public LanternState LitLantern => _lanternLit ? EquippedLantern : null;

	public bool IsLit(ItemState item) => item != null && item == LitLantern;

	// Light the lantern in the Lantern slot. False when the slot is empty. The
	// oil gate is the carrier's (Player.LightLantern).
	public bool Light()
	{
		if (EquippedLantern == null)
		{
			return false;
		}
		if (!_lanternLit)
		{
			_lanternLit = true;
			Changed();
		}
		return true;
	}

	// Put out the lit lantern. False when none was lit.
	public bool Extinguish()
	{
		if (!_lanternLit)
		{
			return false;
		}
		_lanternLit = false;
		Changed();
		return true;
	}

	// ---- Hotbar --------------------------------------------------------------

	// Hotbar index 0 is the Lantern slot — always shown, whether or not it holds
	// a lantern — and index 1 + i is belt slot i.
	public const int LanternHotbarIndex = 0;

	public int HotbarSize => 1 + _beltGrid.Capacity;

	// Hotbar index of the selected entry, or -1 when the hotbar is empty.
	public int SelectedHotbarIndex => _hotbarSelected;
	public ItemState SelectedHotbarItem => HotbarItemAt(_hotbarSelected);

	// The item at hotbar index `index`, or null (empty, or out of range).
	public ItemState HotbarItemAt(int index)
	{
		if (index == LanternHotbarIndex)
		{
			return _equipped[(int)EInventorySlot.Lantern];
		}
		return index > 0 ? _beltGrid.At(index - 1) : null;
	}

	// The hotbar entries the HUD shows, as hotbar indices in order: the lantern
	// slot always, then the FILLED belt slots. Cleared first; caller owns the list.
	public void GetHotbarEntries(List<int> into)
	{
		into.Clear();
		into.Add(LanternHotbarIndex);
		int size = HotbarSize;
		for (int i = LanternHotbarIndex + 1; i < size; i++)
		{
			if (HotbarItemAt(i) != null)
			{
				into.Add(i);
			}
		}
	}

	// Select the `entry`-th hotbar entry as GetHotbarEntries lists them — the
	// position the HUD shows it at. False when there is no such entry or it is
	// the empty lantern slot.
	public bool SelectHotbarEntry(int entry)
	{
		int size = HotbarSize;
		int seen = 0;
		for (int i = 0; i < size; i++)
		{
			if (i != LanternHotbarIndex && HotbarItemAt(i) == null)
			{
				continue;
			}
			if (seen++ != entry)
			{
				continue;
			}
			if (HotbarItemAt(i) == null)
			{
				return false;
			}
			if (_hotbarSelected != i)
			{
				_hotbarSelected = i;
				onChanged?.Invoke();
			}
			return true;
		}
		return false;
	}

	// Step the selection `step` filled entries along the hotbar, wrapping.
	// False when there is nothing else to move to.
	public bool CycleHotbar(int step)
	{
		int size = HotbarSize;
		if (step == 0 || _hotbarSelected < 0)
		{
			return false;
		}
		int dir = Math.Sign(step);
		int idx = _hotbarSelected;
		for (int moved = 0; moved < Math.Abs(step); moved++)
		{
			int next = NextFilledHotbarIndex(idx, dir, size);
			if (next < 0)
			{
				break;
			}
			idx = next;
		}
		if (idx == _hotbarSelected)
		{
			return false;
		}
		_hotbarSelected = idx;
		onChanged?.Invoke();
		return true;
	}

	// The next filled hotbar index after `from` in direction `dir`, wrapping, not
	// counting `from` itself. -1 when no OTHER entry is filled.
	private int NextFilledHotbarIndex(int from, int dir, int size)
	{
		for (int k = 1; k < size; k++)
		{
			int i = ((from + dir * k) % size + size) % size;
			if (HotbarItemAt(i) != null)
			{
				return i;
			}
		}
		return -1;
	}

	// Keep the selection on a filled hotbar entry: hold it while it is filled,
	// otherwise move forward to the next filled one, else -1.
	private void ReconcileHotbarSelection()
	{
		int size = HotbarSize;
		if (_hotbarSelected >= 0 && _hotbarSelected < size && HotbarItemAt(_hotbarSelected) != null)
		{
			return;
		}
		int from = _hotbarSelected >= 0 && _hotbarSelected < size ? _hotbarSelected : size - 1;
		_hotbarSelected = NextFilledHotbarIndex(from, 1, size);
		if (_hotbarSelected < 0 && HotbarItemAt(from) != null)
		{
			_hotbarSelected = from;
		}
	}

	// ---- Ammo ------------------------------------------------------------------

	public AmmoData LoadedAmmo(EAmmoType type) => IsAmmoType(type) ? _loadedAmmo[(int)type] : null;

	public int LoadedAmmoCount(EAmmoType type) => IsAmmoType(type) ? _loadedAmmoCount[(int)type] : 0;

	private static bool IsAmmoType(EAmmoType type) => type > EAmmoType.None && type < EAmmoType.Count;

	// Rounds `weapon` has left to fire: the loaded ammo of its type, or a thrown
	// weapon's own stack. MaxValue for a weapon that needs none.
	public int RoundsFor(WeaponState weapon)
	{
		WeaponData data = weapon?.data;
		if (data == null)
		{
			return 0;
		}
		if (data.thrown)
		{
			return weapon.stackCount;
		}
		return data.NeedsAmmo ? LoadedAmmoCount(data.ammoType) : int.MaxValue;
	}

	// Whether `ammo` can be loaded now: the equipped ranged weapon fires its type,
	// and some is carried.
	public bool CanLoad(AmmoData ammo)
	{
		WeaponData ranged = GetWeapon(EInventorySlot.WeaponRight)?.data;
		return ammo != null && IsAmmoType(ammo.ammoType) && ranged != null && ranged.ammoType == ammo.ammoType
			&& CountCarried(ammo) > 0;
	}

	// A carried stack of the loaded kind of `type`, to show it by; null when none.
	public ItemState LoadedAmmoStack(EAmmoType type)
	{
		AmmoData ammo = LoadedAmmo(type);
		for (int g = 0; ammo != null && g < _carried.Count; g++)
		{
			ItemGrid grid = _carried[g].grid;
			for (int i = 0; i < grid.Capacity; i++)
			{
				if (grid[i]?.data == ammo)
				{
					return grid[i];
				}
			}
		}
		return null;
	}

	public bool IsLoaded(ItemState item) => item?.data is AmmoData ammo && LoadedAmmo(ammo.ammoType) == ammo;

	// Make `ammo` what weapons of its type fire. False when it can't be (CanLoad).
	public bool Load(AmmoData ammo)
	{
		if (!CanLoad(ammo))
		{
			return false;
		}
		_loadedAmmo[(int)ammo.ammoType] = ammo;
		Changed();
		return true;
	}

	// Spend up to `count` rounds for a shot of `weapon`: units of its loaded ammo
	// from any carried stack, or of a thrown weapon's own equipped stack. `round`
	// is the kind spent (the weapon itself when thrown, null when it needs none).
	// Returns the rounds spent — `count` for a weapon that needs none.
	public int SpendRounds(WeaponState weapon, int count, out ItemData round)
	{
		round = null;
		WeaponData data = weapon?.data;
		if (data == null || count <= 0)
		{
			return 0;
		}
		if (data.thrown)
		{
			round = data;
			return SpendThrown(weapon, count);
		}
		if (!data.NeedsAmmo)
		{
			return count;
		}
		AmmoData ammo = LoadedAmmo(data.ammoType);
		round = ammo;
		int spent = 0;
		for (int g = 0; g < _carried.Count && spent < count; g++)
		{
			ItemGrid grid = _carried[g].grid;
			for (int i = 0; i < grid.Capacity && spent < count; i++)
			{
				ItemState stack = grid[i];
				if (stack == null || stack.data != ammo)
				{
					continue;
				}
				spent += stack.Consume(count - spent);
				if (stack.stackCount <= 0)
				{
					grid[i] = null;
				}
			}
		}
		if (spent > 0)
		{
			Changed();
		}
		return spent;
	}

	// A thrown weapon spends its own stack. The last unit leaves the slot, and
	// another carried stack of the same weapon is equipped in its place.
	private int SpendThrown(WeaponState weapon, int count)
	{
		if (!IsEquipped(weapon))
		{
			return 0;
		}
		int spent = weapon.Consume(count);
		if (weapon.stackCount > 0)
		{
			Changed();
			return spent;
		}
		Remove(weapon);
		for (int g = 0; g < _carried.Count; g++)
		{
			ItemGrid grid = _carried[g].grid;
			for (int i = 0; i < grid.Capacity; i++)
			{
				if (grid[i]?.data == weapon.data)
				{
					Equip(grid[i]);
					return spent;
				}
			}
		}
		return spent;
	}

	// Units of `ammo` across every carried grid.
	private int CountCarried(AmmoData ammo)
	{
		if (ammo == null)
		{
			return 0;
		}
		int count = 0;
		for (int g = 0; g < _carried.Count; g++)
		{
			ItemGrid grid = _carried[g].grid;
			for (int i = 0; i < grid.Capacity; i++)
			{
				if (grid[i]?.data == ammo)
				{
					count += grid[i].stackCount;
				}
			}
		}
		return count;
	}

	// Keep each type's loaded kind on one the member carries: a kind that ran out
	// (or none yet) falls back to the type's standard round, else the first
	// carried kind of the type, else nothing.
	private void ReconcileLoadedAmmo()
	{
		for (int t = (int)EAmmoType.None + 1; t < (int)EAmmoType.Count; t++)
		{
			int count = CountCarried(_loadedAmmo[t]);
			if (count <= 0)
			{
				_loadedAmmo[t] = FallbackAmmo((EAmmoType)t);
				count = CountCarried(_loadedAmmo[t]);
			}
			_loadedAmmoCount[t] = count;
		}
	}

	private AmmoData FallbackAmmo(EAmmoType type)
	{
		AmmoData first = null;
		for (int g = 0; g < _carried.Count; g++)
		{
			ItemGrid grid = _carried[g].grid;
			for (int i = 0; i < grid.Capacity; i++)
			{
				if (grid[i]?.data is not AmmoData ammo || ammo.ammoType != type)
				{
					continue;
				}
				if (ammo.isStandard)
				{
					return ammo;
				}
				first ??= ammo;
			}
		}
		return first;
	}

	// ---- Grid moves (CarriedGrid)----------------------------------------------

	// Every move between grids (the stash screen, later chests and merchants) and
	// within the member's own comes through these.

	private ItemState Take(CarriedGrid grid, int index, int count)
	{
		ItemState source = grid.At(index);
		if (source == null || count <= 0)
		{
			return null;
		}
		if (count >= source.stackCount)
		{
			Remove(source);
			return source;
		}
		ItemState split = source.SplitOff(count);
		Changed();
		return split;
	}

	private ItemState PlaceAt(CarriedGrid grid, int index, ItemState incoming, bool allowSwap, out ItemState displaced)
	{
		displaced = null;
		if (incoming?.data == null || !grid.Accepts(incoming.data))
		{
			return incoming;
		}
		MarkAcquired(incoming);
		int before = incoming.stackCount;
		ItemState leftover = grid.grid.PlaceAt(index, incoming, allowSwap, out displaced);
		if (displaced != null)
		{
			OnLeaving(displaced);
		}
		if (displaced != null || (leftover?.stackCount ?? 0) != before)
		{
			Changed();
		}
		return leftover;
	}

	// Move `count` units between two slots of this member's grids — the same grid,
	// or any two of belt, backpack and sacks. Nothing enters or leaves the
	// inventory, so no bookkeeping beyond a stack merged away entirely, which is
	// gone. Refused when either side of a swap lands somewhere that won't take it.
	// False when nothing moved.
	public bool Move(CarriedGrid from, int fromIndex, CarriedGrid to, int toIndex, int count)
	{
		if (from?.owner != this || to?.owner != this)
		{
			return false;
		}
		ItemState source = from.At(fromIndex);
		ItemState target = to.At(toIndex);
		if (source == null || !to.Accepts(source.data))
		{
			return false;
		}
		bool swaps = target != null && !ItemGrid.Merges(target, source);
		if (swaps && !from.Accepts(target.data))
		{
			return false;
		}
		if (!ItemGrid.Move(from.grid, fromIndex, to.grid, toIndex, count))
		{
			return false;
		}
		if (source != null && source.stackCount <= 0)
		{
			OnLeaving(source);
		}
		Changed();
		return true;
	}

	// Units of the carried `item` its other grid (belt <-> backpack) has room
	// for; 0 when it isn't in either grid.
	public int RoomInOtherGrid(ItemState item)
	{
		CarriedGrid from = GridOf(item);
		return from == null ? 0 : Math.Min(item.stackCount, OtherGrid(from).grid.RoomFor(item.data));
	}

	// Move the carried `item` to its other grid (belt <-> backpack): matching
	// stacks first, then the first empty slot. As much as fits moves and the rest
	// stays put. False when nothing moved.
	public bool MoveToOtherGrid(ItemState item)
	{
		int units = RoomInOtherGrid(item);
		if (units <= 0)
		{
			return false;
		}
		CarriedGrid from = GridOf(item);
		int index = from.IndexOf(item);
		ItemState moving = from.grid.Take(index, units);
		ItemState leftover = OtherGrid(from).grid.Add(moving);
		if (leftover != null)
		{
			from.grid.PlaceAt(index, leftover, allowSwap: false, out _);
		}
		Changed();
		return true;
	}

	// The grid holding `item`, null when it is in none (equipped, or not ours).
	public CarriedGrid GridOf(ItemState item)
	{
		return Locate(item, out CarriedGrid grid, out _) ? grid : null;
	}

	// The belt for anything but the belt itself — a sack's contents included.
	private CarriedGrid OtherGrid(CarriedGrid grid) => grid == Belt ? Backpack : Belt;

	// ---- Enumeration -----------------------------------------------------------

	// Every owned item: the belt, the backpack — each sack followed by its
	// contents — then the equip slots.
	public IEnumerable<ItemState> EnumerateAll()
	{
		foreach (ItemState item in _beltGrid.EnumerateAll())
		{
			yield return item;
		}
		foreach (ItemState item in _backpackGrid.EnumerateAll())
		{
			yield return item;
		}
		for (int i = 0; i < _equipped.Length; i++)
		{
			if (_equipped[i] != null) { yield return _equipped[i]; }
		}
	}

	// Worn armor only — the pieces that transmit wetness into the wearer's meter
	// (Player.TickWetEffect), count toward max armor, and add belt / backpack slots.
	public IEnumerable<ArmorState> EnumerateEquippedArmor()
	{
		if (_equipped[(int)EInventorySlot.Helmet] is ArmorState head) { yield return head; }
		if (_equipped[(int)EInventorySlot.Armor] is ArmorState body) { yield return body; }
	}

	// Every owned item that soaks and dries (ItemData.TakesWetness), carried or worn.
	public IEnumerable<ItemState> EnumerateWettable()
	{
		foreach (ItemState item in EnumerateAll())
		{
			if (item?.data != null && item.data.TakesWetness) { yield return item; }
		}
	}

	// Every owned armor piece, worn or not, for grime and washing.
	public IEnumerable<ArmorState> EnumerateAllArmor()
	{
		foreach (ItemState item in EnumerateAll())
		{
			if (item is ArmorState armor) { yield return armor; }
		}
	}

	// ---- Bulk exits ------------------------------------------------------------

	// Remove every unequipped item and return them, for the death sack
	// (Sim.DropDeathSack). Worn gear — the lantern slot included, so the wake at
	// the campfire isn't blind — stays on the body.
	public List<ItemState> TakeDeathDrop()
	{
		var taken = new List<ItemState>();
		Collect(_beltGrid, taken);
		Collect(_backpackGrid, taken);
		foreach (ItemState item in taken)
		{
			Remove(item);
		}
		return taken;
	}

	private static void Collect(ItemGrid grid, List<ItemState> into)
	{
		for (int i = 0; i < grid.Capacity; i++)
		{
			if (grid[i] != null)
			{
				into.Add(grid[i]);
			}
		}
	}

	// ---- Save ------------------------------------------------------------------

	// Inside a shared EntitySerializer table (SaveGame). The equip slots first —
	// they decide the grids' sizes — then the belt and the backpack slot by slot,
	// gaps included, so the player's layout survives; then whether the lantern
	// slot is lit, the hotbar selection, and the loaded ammo per type.
	public void Serialize(BinaryWriter w)
	{
		for (int i = (int)EInventorySlot.None + 1; i < (int)EInventorySlot.Count; i++)
		{
			EntitySerializer.WriteItem(w, _equipped[i]);
		}
		_beltGrid.Serialize(w);
		_backpackGrid.Serialize(w);
		w.Write(_lanternLit);
		w.Write(_hotbarSelected);
		for (int t = (int)EAmmoType.None + 1; t < (int)EAmmoType.Count; t++)
		{
			EntitySerializer.WriteRef(w, _loadedAmmo[t]);
		}
	}

	// Replaces everything carried with what the save holds. An item that no
	// longer has its slot (a grid shrank) is repacked, and dropped with a warning
	// if there is no room.
	public void Restore(BinaryReader r)
	{
		for (int i = 0; i < _equipped.Length; i++)
		{
			if (_equipped[i] != null)
			{
				SetSlot((EInventorySlot)i, null);
			}
		}
		_beltGrid.Clear();
		_backpackGrid.Clear();
		for (int i = (int)EInventorySlot.None + 1; i < (int)EInventorySlot.Count; i++)
		{
			EInventorySlot slot = (EInventorySlot)i;
			ItemState item = EntitySerializer.ReadItem(r);
			if (item != null)
			{
				SetSlot(slot, item);
			}
			onSlotChanged?.Invoke(slot);
		}
		// The grids are empty, so sizing them to the restored armor evicts nothing.
		ApplyCapacity();
		List<ItemState> overflow = _beltGrid.Restore(r);
		overflow.AddRange(_backpackGrid.Restore(r));
		_lanternLit = r.ReadBoolean() && EquippedLantern != null;
		_hotbarSelected = r.ReadInt32();
		for (int t = (int)EAmmoType.None + 1; t < (int)EAmmoType.Count; t++)
		{
			_loadedAmmo[t] = EntitySerializer.ReadRef<AmmoData>(r);
		}
		SyncSacks();
		foreach (ItemState item in overflow)
		{
			if (AddPreferring(PreferredGrid(item.data), item) != null)
			{
				GD.PushWarning($"Inventory: no room for saved '{item.data?.ResourcePath}' — dropping it.");
			}
		}
		Changed();
	}

	// The single chokepoint every equip-slot write funnels through, so the
	// outgoing occupant's teardown can't be skipped by any path.
	private void SetSlot(EInventorySlot slot, ItemState item)
	{
		ItemState outgoing = _equipped[(int)slot];
		// A summoner's minions never outlive the weapon being put away.
		if (outgoing != item && outgoing is WeaponState weapon)
		{
			weapon.DestroyMinions();
		}
		// A lantern goes out when it leaves the slot, and a new one arrives unlit.
		if (outgoing != item && slot == EInventorySlot.Lantern)
		{
			_lanternLit = false;
		}
		_equipped[(int)slot] = item;
	}

	private void NotifySlot(EInventorySlot slot)
	{
		onSlotChanged?.Invoke(slot);
		Changed();
	}
}
