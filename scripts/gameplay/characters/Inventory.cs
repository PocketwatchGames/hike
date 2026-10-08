using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// Everything one party member carries: two sparse grids — the belt and the
// backpack — plus the equip slots. An equipped item lives IN its slot and is in
// neither grid — equipping moves it out, swapping whatever held the slot back
// into the grid slot it left. The belt doubles as the HUD hotbar; a lantern is
// lit only while it hangs there (LitLantern). Both grids' sizes are the
// PlayerData base plus whatever the worn armor adds (ArmorData.beltSlots /
// backpackSlots), re-applied on every change.
public class Inventory
{
	// One of the member's two grids, as an IItemGrid so ItemTransfer and the
	// two-sided screens can move items in and out of it. Every call routes back
	// through the Inventory, so the touched / spoil stamp on the way in and
	// OnLeaving on the way out can't be skipped. Add and CanFullyAdd speak for the
	// whole member — this grid first, then the other — so a stash send fills the
	// belt once the backpack is full.
	public sealed class CarriedGrid : IItemGrid
	{
		public readonly Inventory owner;
		internal readonly ItemGrid grid;

		internal CarriedGrid(Inventory owner, ItemGrid grid)
		{
			this.owner = owner;
			this.grid = grid;
		}

		// Slots[i] is the stack at slot i, or null. Count is the capacity.
		public IReadOnlyList<ItemState> Slots => grid.Slots;
		public int Capacity => grid.Capacity;
		public ItemState At(int index) => grid.At(index);
		public int IndexOf(ItemState item) => grid.IndexOf(item);
		public ItemState Take(int index, int count) => owner.Take(this, index, count);
		public ItemState PlaceAt(int index, ItemState incoming, bool allowSwap, out ItemState displaced) =>
			owner.PlaceAt(this, index, incoming, allowSwap, out displaced);
		public ItemState Add(ItemState incoming) => owner.Add(this, incoming);
		public bool CanFullyAdd(ItemData data, int count) => owner.CanFullyAdd(data, count);
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

	// The one lit lantern, or null. One field, so two can never be lit at once.
	// INVARIANT: sits on the belt — ReconcileLitLantern puts it out the moment
	// any change leaves it anywhere else.
	private LanternState _litLantern;

	// Belt index of the hotbar selection, or -1 when the hotbar is empty.
	// Stored as an index (not an item) so the cursor holds its place when the
	// selected stack is used up; Changed() snaps it to the next filled slot.
	private int _hotbarSelected = -1;

	// An equip slot changed occupant (always followed by onChanged).
	public Action<EInventorySlot> onSlotChanged;
	// Generic "something in the inventory changed" pulse, including a hotbar
	// selection move, the lantern being lit or put out, and a grid resizing. UI
	// panels that re-derive everything listen to this.
	public Action onChanged;

	// Fire onChanged from outside the Inventory class — used by the action
	// runner's DecrementStack handler when it mutates item.stackCount
	// directly without going through one of Inventory's mutation methods.
	public void NotifyChanged()
	{
		Changed();
	}

	public CarriedGrid Belt { get; }
	public CarriedGrid Backpack { get; }

	public Inventory(Player owner, PlayerData data)
	{
		_owner = owner;
		_data = data;
		_beltGrid = new ItemGrid(data.beltCapacity);
		_backpackGrid = new ItemGrid(data.backpackCapacity);
		Belt = new CarriedGrid(this, _beltGrid);
		Backpack = new CarriedGrid(this, _backpackGrid);
	}

	private void Changed()
	{
		ApplyCapacity();
		ReconcileHotbarSelection();
		ReconcileLitLantern();
		onChanged?.Invoke();
	}

	// The grid holding `item` and its slot there; false when it's in neither
	// (equipped, or not owned).
	private bool Locate(ItemState item, out ItemGrid grid, out int index)
	{
		grid = _beltGrid;
		index = _beltGrid.IndexOf(item);
		if (index < 0)
		{
			grid = _backpackGrid;
			index = _backpackGrid.IndexOf(item);
		}
		return index >= 0;
	}

	private ItemGrid Other(ItemGrid grid) => grid == _beltGrid ? _backpackGrid : _beltGrid;

	// True if `item` is owned — in either grid or equipped.
	// Non-allocating (safe to call from per-frame paths).
	public bool Contains(ItemState item) => Locate(item, out _, out _) || IsEquipped(item);

	// True if either grid already holds a stack of exactly `data`. Non-allocating.
	public bool HoldsStackOf(ItemData data)
	{
		if (data == null || !data.IsStackable)
		{
			return false;
		}
		for (int i = 0; i < _beltGrid.Capacity; i++)
		{
			if (_beltGrid[i]?.data == data)
			{
				return true;
			}
		}
		for (int i = 0; i < _backpackGrid.Capacity; i++)
		{
			if (_backpackGrid[i]?.data == data)
			{
				return true;
			}
		}
		return false;
	}

	// Where `data` lands first: materials and equippable gear (weapons, armor,
	// shields) go in the backpack, so they don't crowd out what the player uses
	// from the belt — only a full pack spills them onto it. Anything else takes
	// the belt first.
	public CarriedGrid PreferredGrid(ItemData data)
	{
		return data != null && (data.IsMaterial || data.IsEquippable) ? Backpack : Belt;
	}

	// Merge into matching stacks in both grids, then take the first empty slot of
	// `first`, else of the other grid. Returns what didn't fit. Placement only —
	// no bookkeeping, no Changed.
	private ItemState AddPreferring(ItemGrid first, ItemState item)
	{
		if (item?.data == null || item.stackCount <= 0)
		{
			return null;
		}
		ItemGrid second = Other(first);
		first.MergeIntoStacks(item);
		second.MergeIntoStacks(item);
		if (item.stackCount <= 0)
		{
			return null;
		}
		int index = first.FirstEmpty();
		if (index >= 0)
		{
			first[index] = item;
			return null;
		}
		index = second.FirstEmpty();
		if (index >= 0)
		{
			second[index] = item;
			return null;
		}
		return item;
	}

	// Spend up to `count` units of a reagent, matching by the item's parent chain
	// (a reagent naming goblin_meat draws from any goblin subspecies meat) —
	// the backpack first, then the belt. Emptied stacks free their slot. Returns
	// how many units were actually spent (may be < count if the member ran short
	// — the caller covers the remainder from the party stash). Used by the
	// alchemy cast path. `spentItems`, when given, collects the items drawn.
	public int SpendMaterial(ItemData reagentItem, int count, List<SpentItem> spentItems = null)
	{
		if (reagentItem == null || count <= 0)
		{
			return 0;
		}
		int spent = SpendMaterial(_backpackGrid, reagentItem, count, spentItems);
		spent += SpendMaterial(_beltGrid, reagentItem, count - spent, spentItems);
		if (spent > 0)
		{
			Changed();
		}
		return spent;
	}

	private static int SpendMaterial(ItemGrid grid, ItemData reagentItem, int count, List<SpentItem> spentItems)
	{
		int spent = 0;
		for (int i = 0; i < grid.Capacity && spent < count; i++)
		{
			ItemState s = grid[i];
			if (s?.data == null || !s.data.IsMaterial || s.stackCount <= 0 || !Cooking.Satisfies(s.data, reagentItem))
			{
				continue;
			}
			int take = s.Consume(count - spent);
			spent += take;
			SpentItem.Add(spentItems, s.data, take);
			if (s.stackCount <= 0)
			{
				grid[i] = null;
			}
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
		if (key == null || !Locate(key, out ItemGrid grid, out int index))
		{
			return null;
		}
		var keyData = (KeyData)key.data;
		key.Consume(1);
		if (key.stackCount <= 0)
		{
			grid[index] = null;
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
		ItemState made = KeyFor(_beltGrid, lockData, ref skeleton) ?? KeyFor(_backpackGrid, lockData, ref skeleton);
		return made ?? skeleton;
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
	// overflows the grids) to the party stash. A weapon forfeits its
	// outstanding arrows on the way out, mirroring Remove(). Returns what the
	// stash had no room for.
	public ItemState PushToStash(ItemState item)
	{
		if (item == null)
		{
			return null;
		}
		if (item is WeaponState ws)
		{
			ws.DestroyOutstandingArrows();
		}
		return PartyStash != null ? PartyStash.Add(item) : item;
	}

	// Add an item to the member's grids, starting with its PreferredGrid.
	// Stackables fill existing same-kind stacks first, then take the first empty
	// slot. Returns the number of units actually added (0 = nothing fit, less
	// than stackCount = partial). The caller's ItemState is consumed: if it was
	// stored it is the grid's reference now; if it fully merged it is spent.
	// Never equips — see Acquire for that.
	public int TryAdd(ItemState item)
	{
		if (item == null || item.data == null || item.stackCount <= 0 || !item.data.IsCarriable)
		{
			return 0;
		}
		int initialStack = item.stackCount;
		ItemState leftover = Add(PreferredGrid(item.data), item);
		return initialStack - (leftover?.stackCount ?? 0);
	}

	// IItemGrid.Add for either grid: `first` is where an empty slot is looked for
	// first. Returns the leftover, null when everything went in.
	private ItemState Add(CarriedGrid first, ItemState incoming)
	{
		if (incoming?.data == null || incoming.stackCount <= 0 || !incoming.data.IsCarriable)
		{
			return incoming;
		}
		// Entering the inventory marks the item as touched for the rest of its
		// life (see ItemState.touched). Stamp before the merge so even units
		// that fold into an existing stack count as handled.
		MarkAcquired(incoming);
		int before = incoming.stackCount;
		ItemState leftover = AddPreferring(first.grid, incoming);
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

	// True if Acquire would take all `count` units — straight into an empty equip
	// slot, or into the grids. Mirrors Acquire without mutating anything.
	public bool CanAcquire(ItemData data, int count)
	{
		return WouldAutoEquip(data) || CanFullyAdd(data, count);
	}

	// Take ownership of an item from the world (a pickup, the starting loadout):
	// gear whose equip slot is empty is equipped on the spot, anything else goes
	// into the grids as TryAdd. Returns the number of units taken.
	public int Acquire(ItemState item)
	{
		if (item?.data == null || item.stackCount <= 0 || !WouldAutoEquip(item.data))
		{
			return TryAdd(item);
		}
		EquipIncoming(item);
		return item.stackCount;
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
		return data != null && count > 0 && data.IsCarriable
			&& _beltGrid.RoomFor(data) + _backpackGrid.RoomFor(data) >= count;
	}

	// Removes an item from the inventory entirely (dropped, sold, stashed, spent),
	// from a grid or an equip slot. No-op for an item that isn't owned.
	public void Remove(ItemState item)
	{
		bool inGrid = Locate(item, out ItemGrid grid, out int index);
		if (!inGrid && !IsEquipped(item))
		{
			return;
		}
		OnLeaving(item);
		if (inGrid)
		{
			grid[index] = null;
		}
		Changed();
	}

	// The single chokepoint for an item leaving the inventory, by any path. A
	// weapon destroys the arrows it left lying around, no refund (the recharge
	// timer keeps its deadline and refills the magazine if it is re-acquired);
	// equip / unequip / slot moves don't come through here, so an unequipped bow
	// keeps its arrows. An equipped item's slot is emptied.
	private void OnLeaving(ItemState item)
	{
		if (item is WeaponState ws)
		{
			ws.DestroyOutstandingArrows();
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

	// Equip an item from either grid: it moves into its category's slot, and
	// whatever held that slot moves into the grid slot it left. False for an item
	// that isn't in a grid or isn't equippable; true (no-op) if already equipped.
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
		if (!Locate(item, out ItemGrid grid, out int index))
		{
			return false;
		}
		grid[index] = _equipped[(int)slot];
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
		if (item == null || grid?.owner != this || !grid.grid.InRange(index))
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
		CarriedGrid grid = Belt.IndexOf(item) >= 0 ? Belt : Backpack;
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
		foreach (ItemState item in evicted)
		{
			ItemState leftover = AddPreferring(PreferredGrid(item.data).grid, item);
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

	public LanternState LitLantern => _litLantern;

	public bool IsLit(ItemState item) => item != null && item == _litLantern;

	// True when `item` is a lantern hanging on the belt — the only place one can
	// be lit.
	public bool IsOnBelt(ItemState item)
	{
		return _beltGrid.IndexOf(item) >= 0;
	}

	// Light a lantern on the belt, putting out whichever was lit. False when it
	// isn't on the belt or its tank is empty.
	public bool Light(LanternState lantern)
	{
		if (lantern == null || !lantern.HasFuel || !IsOnBelt(lantern))
		{
			return false;
		}
		if (_litLantern != lantern)
		{
			_litLantern = lantern;
			Changed();
		}
		return true;
	}

	// Put out the lit lantern. False when none was lit.
	public bool Extinguish()
	{
		if (_litLantern == null)
		{
			return false;
		}
		_litLantern = null;
		Changed();
		return true;
	}

	// A lantern that left the belt by any path — moved, stashed, dropped, spent,
	// a belt slot lost with the armor that gave it — goes out. Run on every
	// change, so no path has to remember it.
	private void ReconcileLitLantern()
	{
		if (_litLantern != null && !IsOnBelt(_litLantern))
		{
			_litLantern = null;
		}
	}

	// ---- Hotbar --------------------------------------------------------------

	// The hotbar is the belt, slot for slot.
	public int HotbarSize => _beltGrid.Capacity;

	// Belt index of the selected hotbar entry, or -1 when the hotbar is empty.
	public int SelectedHotbarIndex => _hotbarSelected;
	public ItemState SelectedHotbarItem => _beltGrid.At(_hotbarSelected);

	// The FILLED hotbar slots, as belt indices in slot order — what the HUD
	// shows. Cleared first; caller owns the list.
	public void GetHotbarEntries(List<int> into)
	{
		into.Clear();
		int size = HotbarSize;
		for (int i = 0; i < size; i++)
		{
			if (_beltGrid[i] != null)
			{
				into.Add(i);
			}
		}
	}

	// Select the `entry`-th FILLED hotbar slot — the position the HUD shows it at,
	// since it packs filled slots together. False when there is no such entry.
	public bool SelectHotbarEntry(int entry)
	{
		int size = HotbarSize;
		int seen = 0;
		for (int i = 0; i < size; i++)
		{
			if (_beltGrid[i] == null)
			{
				continue;
			}
			if (seen++ == entry)
			{
				if (_hotbarSelected != i)
				{
					_hotbarSelected = i;
					onChanged?.Invoke();
				}
				return true;
			}
		}
		return false;
	}

	// Step the selection `step` filled entries along the hotbar, wrapping.
	// False when there is nothing else to move to.
	public bool CycleHotbar(int step)
	{
		int size = HotbarSize;
		if (size <= 0 || step == 0 || _hotbarSelected < 0)
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

	// The next filled hotbar slot after `from` in direction `dir`, wrapping, not
	// counting `from` itself. -1 when no OTHER slot is filled.
	private int NextFilledHotbarIndex(int from, int dir, int size)
	{
		for (int k = 1; k < size; k++)
		{
			int i = ((from + dir * k) % size + size) % size;
			if (_beltGrid[i] != null)
			{
				return i;
			}
		}
		return -1;
	}

	// Keep the selection on a filled hotbar slot: hold it while its slot is
	// filled, otherwise move forward to the next filled one, else -1.
	private void ReconcileHotbarSelection()
	{
		int size = HotbarSize;
		if (_hotbarSelected >= 0 && _hotbarSelected < size && _beltGrid[_hotbarSelected] != null)
		{
			return;
		}
		int from = _hotbarSelected >= 0 && _hotbarSelected < size ? _hotbarSelected : size - 1;
		_hotbarSelected = size > 0 ? NextFilledHotbarIndex(from, 1, size) : -1;
		if (_hotbarSelected < 0 && size > 0 && _beltGrid[from] != null)
		{
			_hotbarSelected = from;
		}
	}

	// ---- Grid moves (CarriedGrid) ----------------------------------------------

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
		if (incoming?.data == null || !incoming.data.IsCarriable)
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

	// Move `count` units between two slots of this member's grids — the same grid
	// or belt <-> backpack. Nothing enters or leaves the inventory, so no
	// bookkeeping beyond a stack merged away entirely, which is gone. False when
	// nothing moved.
	public bool Move(CarriedGrid from, int fromIndex, CarriedGrid to, int toIndex, int count)
	{
		if (from?.owner != this || to?.owner != this)
		{
			return false;
		}
		ItemState source = from.At(fromIndex);
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

	// ---- Enumeration -----------------------------------------------------------

	// Every owned item: the belt, the backpack, then the equip slots.
	public IEnumerable<ItemState> EnumerateAll()
	{
		for (int i = 0; i < _beltGrid.Capacity; i++)
		{
			if (_beltGrid[i] != null) { yield return _beltGrid[i]; }
		}
		for (int i = 0; i < _backpackGrid.Capacity; i++)
		{
			if (_backpackGrid[i] != null) { yield return _backpackGrid[i]; }
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

	// Every owned armor piece, worn or not, for wetness ticking.
	public IEnumerable<ArmorState> EnumerateAllArmor()
	{
		foreach (ItemState item in EnumerateAll())
		{
			if (item is ArmorState armor) { yield return armor; }
		}
	}

	// ---- Bulk exits ------------------------------------------------------------

	// Remove every unequipped item but one lantern and return them, for the death
	// sack (Sim.DropDeathSack). Worn gear stays on the body. The kept lantern —
	// the lit one, else the first on the belt — means the wake at the campfire
	// isn't blind. Goes through Remove, so a weapon forfeits its loose arrows as
	// on any other exit.
	public List<ItemState> TakeDeathDrop()
	{
		ItemState kept = _litLantern ?? FirstBeltLantern();
		var taken = new List<ItemState>();
		CollectExcept(_beltGrid, kept, taken);
		CollectExcept(_backpackGrid, kept, taken);
		foreach (ItemState item in taken)
		{
			Remove(item);
		}
		return taken;
	}

	private static void CollectExcept(ItemGrid grid, ItemState kept, List<ItemState> into)
	{
		for (int i = 0; i < grid.Capacity; i++)
		{
			ItemState item = grid[i];
			if (item != null && item != kept)
			{
				into.Add(item);
			}
		}
	}

	private LanternState FirstBeltLantern()
	{
		for (int i = 0; i < _beltGrid.Capacity; i++)
		{
			if (_beltGrid[i] is LanternState lantern)
			{
				return lantern;
			}
		}
		return null;
	}

	// ---- Save ------------------------------------------------------------------

	// Inside a shared EntitySerializer table (SaveGame). The equip slots first —
	// they decide the grids' sizes — then the belt and the backpack slot by slot,
	// gaps included, so the player's layout survives; then the lit lantern's belt
	// index (-1 for none) and the hotbar selection.
	public void Serialize(BinaryWriter w)
	{
		for (int i = (int)EInventorySlot.None + 1; i < (int)EInventorySlot.Count; i++)
		{
			EntitySerializer.WriteItem(w, _equipped[i]);
		}
		_beltGrid.Serialize(w);
		_backpackGrid.Serialize(w);
		w.Write(_beltGrid.IndexOf(_litLantern));
		w.Write(_hotbarSelected);
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
		_litLantern = null;
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
		_litLantern = _beltGrid.At(r.ReadInt32()) as LanternState;
		_hotbarSelected = r.ReadInt32();
		foreach (ItemState item in overflow)
		{
			if (AddPreferring(PreferredGrid(item.data).grid, item) != null)
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
		_equipped[(int)slot] = item;
	}

	private void NotifySlot(EInventorySlot slot)
	{
		onSlotChanged?.Invoke(slot);
		Changed();
	}
}
