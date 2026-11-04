using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// Everything one party member carries. The backpack — with the sacks in it — is
// the only storage: every owned item sits in a slot there. The equip slots and
// the belt hold nothing of their own; each POINTS at a stack on the backpack's
// top level, so an equipped or belted item shows in both places and keeps its
// pointer wherever it is moved within the backpack. Changed() re-checks every
// pointer (ReconcilePointers), so no path that takes an item out of the
// backpack has to remember to unequip it. The hotbar is the lantern slot
// followed by the belt; at most one carried light burns (LitLight): the
// equipped lantern or a belted torch. The backpack's and the belt's sizes are
// the PlayerData base plus whatever the worn armor adds, re-applied on every
// change.
public class Inventory : IItemSource
{
	// One of the member's grids — the backpack or a carried sack — as an
	// IItemGrid so ItemTransfer and the two-sided screens can move items in and out
	// of it. Every call routes back through the Inventory, so the touched / spoil
	// stamp on the way in and the pointer check on the way out can't be skipped.
	// Add and CanFullyAdd speak for the whole member — this grid first, then the
	// rest.
	public sealed class CarriedGrid : IItemGrid
	{
		public readonly Inventory owner;
		internal readonly ItemGrid grid;
		// The sack whose contents this is; null for the backpack.
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
	private readonly ItemGrid _backpackGrid;

	// Indexed by EInventorySlot; null = empty. Each points at a stack on the
	// backpack's top level (never inside a sack).
	private readonly ItemState[] _equipped = new ItemState[(int)EInventorySlot.Count];
	// The belt: `_belt[i]` points at a top-level backpack stack, or null. An item
	// is on the belt at most once.
	private ItemState[] _belt;

	// The one carried light burning, or null: the equipped lantern or a belted
	// torch. ReconcileLit puts it out whenever it stops being either.
	private ItemState _lit;

	// Hotbar index of the selection (see HotbarItemAt), or -1 when the hotbar is
	// empty. Stored as an index (not an item) so the cursor holds its place when
	// the selected stack is used up; Changed() snaps it to the next filled slot.
	private int _hotbarSelected = -1;

	// The ammo type the ranged weapon fired as of the last Changed, so a change of
	// weapon is noticed whichever path made it (ReconcileAmmo).
	private EAmmoType _rangedAmmoType;
	// Rounds the equipped ammo has left: every top-level stack of its kind, which
	// refill the slot as it empties. Refreshed on every change so the HUD and the
	// fire gates read it without walking the backpack.
	private int _ammoRounds;

	// An equip slot changed occupant (always followed by onChanged).
	public Action<EInventorySlot> onSlotChanged;
	// Generic "something in the inventory changed" pulse, including a hotbar
	// selection move, the belt being rearranged, the lantern being lit or put out,
	// and a grid resizing. UI panels that re-derive everything listen to this.
	public Action onChanged;

	// Fire onChanged from outside the Inventory class, for a change made to an
	// item without going through one of Inventory's mutation methods.
	public void NotifyChanged()
	{
		Changed();
	}

	public CarriedGrid Backpack { get; }

	// One grid per sack in the backpack, in slot order. Rebuilt by SyncSacks; a
	// sack keeps its CarriedGrid while it stays carried, so a screen's reference
	// to it stays valid across moves.
	public IReadOnlyList<CarriedGrid> Sacks => _sacks;
	private readonly List<CarriedGrid> _sacks = new();
	// The backpack, then the sacks — every grid an owned item can sit in.
	public IReadOnlyList<CarriedGrid> Carried => _carried;
	private readonly List<CarriedGrid> _carried = new();
	// Scratch for AddPreferring's placement order.
	private readonly List<CarriedGrid> _placement = new();

	public Inventory(Player owner, PlayerData data)
	{
		_owner = owner;
		_data = data;
		_backpackGrid = new ItemGrid(data.backpackCapacity);
		_belt = new ItemState[Math.Max(0, data.beltCapacity)];
		Backpack = new CarriedGrid(this, _backpackGrid);
		_carried.Add(Backpack);
	}

	private void Changed()
	{
		// Taking off armor shrinks the backpack, and what a shrink evicts can be
		// the worn armor itself, so settle the two together.
		do
		{
			SyncSacks();
			ReconcilePointers();
		}
		while (ApplyCapacity());
		ReconcileLit();
		if (ReconcileAmmo())
		{
			onSlotChanged?.Invoke(EInventorySlot.Ammo);
		}
		ReconcileHotbarSelection();
		CountAmmoRounds();
		onChanged?.Invoke();
	}

	// Re-derive the sack grids from what the backpack holds. A sack that left
	// detaches its grid; one that stayed keeps it.
	private void SyncSacks()
	{
		var kept = new List<CarriedGrid>(_sacks.Count);
		for (int i = 0; i < _backpackGrid.Capacity; i++)
		{
			if (_backpackGrid[i] is not SackState sack)
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
			kept.Add(existing ?? new CarriedGrid(this, sack.contents, sack));
		}
		foreach (CarriedGrid old in _sacks)
		{
			if (!kept.Contains(old))
			{
				old.detached = true;
			}
		}
		_sacks.Clear();
		_sacks.AddRange(kept);
		_carried.RemoveRange(1, _carried.Count - 1);
		_carried.AddRange(_sacks);
	}

	// An equip or belt slot points only at a stack on the backpack's top level.
	// One whose stack left it (spent, dropped, stashed, merged away, moved into a
	// sack) moves to another top-level stack of the same stackable kind — the
	// next potion stack, the thrown weapon picked back up — or empties.
	private void ReconcilePointers()
	{
		for (int i = (int)EInventorySlot.None + 1; i < _equipped.Length; i++)
		{
			ItemState item = _equipped[i];
			if (item == null || IsOnTop(item))
			{
				continue;
			}
			SetSlot((EInventorySlot)i, item.data != null && item.data.IsStackable ? TopStackOf(item.data) : null);
			onSlotChanged?.Invoke((EInventorySlot)i);
		}
		for (int i = 0; i < _belt.Length; i++)
		{
			ItemState item = _belt[i];
			if (item == null || IsOnTop(item))
			{
				continue;
			}
			ItemState next = item.data != null && item.data.IsStackable ? TopStackOf(item.data) : null;
			_belt[i] = next != null && BeltIndexOf(next) < 0 ? next : null;
		}
	}

	// True when `item` sits on the backpack's top level — where an equip or belt
	// slot may point.
	private bool IsOnTop(ItemState item) => item != null && _backpackGrid.IndexOf(item) >= 0;

	// The first top-level backpack stack of `data`, or null.
	private ItemState TopStackOf(ItemData data)
	{
		if (data == null)
		{
			return null;
		}
		for (int i = 0; i < _backpackGrid.Capacity; i++)
		{
			if (_backpackGrid[i]?.data == data)
			{
				return _backpackGrid[i];
			}
		}
		return null;
	}

	// The grid holding `item` and its slot there; false when it's in none (not
	// owned). Non-allocating.
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

	// True if `item` is owned — in the backpack or a sack in it. Non-allocating
	// (safe to call from per-frame paths).
	public bool Contains(ItemState item) => Locate(item, out _, out _);

	// An owned item serving as a `kind` tool, anywhere in the backpack — a sack's
	// included — skipping broken gear. `use` is its row for that kind.
	// Non-allocating: a corpse or dig spot in reach asks every frame.
	public ItemState FindTool(EToolKind kind, out ToolUse use)
	{
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

	// True if any grid already holds a stack of exactly `data`. Non-allocating.
	public bool HoldsStackOf(ItemData data)
	{
		if (data == null || !data.IsStackable)
		{
			return false;
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
	// other sack that accepts it, then the backpack.
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
		into.Add(Backpack);
	}

	// Spend up to `count` units of a reagent, matching by the item's parent chain
	// (a reagent naming goblin_meat draws from any goblin subspecies meat) — the
	// backpack with the sacks it holds. Emptied stacks free their slot. Returns
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
	// overflows the backpack) to the party stash. Returns what the stash had no
	// room for.
	public ItemState PushToStash(ItemState item)
	{
		if (item == null)
		{
			return null;
		}
		return PartyStash != null ? PartyStash.Add(item) : item;
	}

	// Add an item to the member's grids: a sack that accepts it before the
	// backpack (see AddPreferring). Stackables fill existing same-kind stacks
	// first, then take the first empty slot. A usable kind not on the belt yet
	// takes a free belt slot. Returns the number of units actually added (0 =
	// nothing fit, less than stackCount = partial). The caller's ItemState is
	// consumed: if it was stored it is the grid's reference now; if it fully
	// merged it is spent. Never equips — see Acquire for that.
	public int TryAdd(ItemState item)
	{
		if (item == null || item.data == null || item.stackCount <= 0)
		{
			return 0;
		}
		int initialStack = item.stackCount;
		ItemState leftover = Add(Backpack, item);
		int added = initialStack - (leftover?.stackCount ?? 0);
		if (added > 0)
		{
			AutoBelt(item.data);
		}
		return added;
	}

	// Put the top-level stack of `data` on the first free belt slot, unless the
	// belt already carries that kind or `data` doesn't belong on it.
	private void AutoBelt(ItemData data)
	{
		if (!Beltable(data) || BeltHolds(data))
		{
			return;
		}
		int free = FirstFreeBeltSlot();
		ItemState stack = TopStackOf(data);
		if (free >= 0 && stack != null)
		{
			SetBelt(free, stack);
		}
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

	// True when taking `data` would equip it on the spot: it fits (EquipFits) and
	// its slot is empty.
	public bool WouldAutoEquip(ItemData data)
	{
		return EquipFits(data) && GetEquipped(data.EquipSlotKind) == null;
	}

	// True if Acquire would take all `count` units. Mirrors Acquire without
	// mutating anything.
	public bool CanAcquire(ItemData data, int count)
	{
		return WouldAutoEquip(data) ? _backpackGrid.RoomFor(data) >= count : CanFullyAdd(data, count);
	}

	// Take ownership of an item from the world (a pickup, the starting loadout):
	// gear whose equip slot is empty lands on the backpack's top level and is
	// equipped there; anything else goes in as TryAdd. Returns the number of units
	// taken.
	public int Acquire(ItemState item)
	{
		if (item?.data == null || item.stackCount <= 0)
		{
			return 0;
		}
		if (!WouldAutoEquip(item.data))
		{
			return TryAdd(item);
		}
		int before = item.stackCount;
		ItemState leftover = EquipIncoming(item);
		return before - (leftover?.stackCount ?? 0);
	}

	// Equip an item from outside the inventory (the stash, a pickup): it lands on
	// the backpack's top level — never in a sack — and its slot points at it,
	// re-pointing from whatever held it (that stays where it is). Returns what
	// didn't fit, null when all of it went in; `incoming` itself when it doesn't
	// fit an equip slot (EquipFits).
	public ItemState EquipIncoming(ItemState incoming)
	{
		if (incoming?.data == null || incoming.stackCount <= 0 || !EquipFits(incoming.data))
		{
			return incoming;
		}
		MarkAcquired(incoming);
		int before = incoming.stackCount;
		ItemState leftover = _backpackGrid.Add(incoming);
		if ((leftover?.stackCount ?? 0) == before)
		{
			return leftover;
		}
		// Merged away whole: the stack it joined is what gets equipped.
		if (!Equip(IsOnTop(incoming) ? incoming : TopStackOf(incoming.data)))
		{
			Changed();
		}
		return leftover;
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

	// Removes an item from the inventory entirely (dropped, sold, stashed, spent).
	// Any slot pointing at it lets go (ReconcilePointers). No-op for an item that
	// isn't owned.
	public void Remove(ItemState item)
	{
		if (!Locate(item, out CarriedGrid grid, out int index))
		{
			return;
		}
		grid.grid[index] = null;
		Changed();
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

	// Point `item`'s equip slot at it. Whatever the slot pointed at stays where it
	// is, unequipped. False for an item that isn't on the backpack's top level or
	// doesn't fit (EquipFits); true (no-op) if already equipped.
	public bool Equip(ItemState item)
	{
		if (!CanEquip(item))
		{
			return false;
		}
		EInventorySlot slot = item.data.EquipSlotKind;
		if (_equipped[(int)slot] != item)
		{
			SetSlot(slot, item);
			NotifySlot(slot);
		}
		return true;
	}

	// True when Equip would take `item`: it fits and is on the backpack's top
	// level.
	public bool CanEquip(ItemState item) => item != null && EquipFits(item.data) && IsOnTop(item);

	// True when `data` may go in its equip slot right now: it is equippable, and
	// ammo only when the right-hand weapon fires its type.
	public bool EquipFits(ItemData data)
	{
		if (data == null || !data.IsEquippable)
		{
			return false;
		}
		return data is not AmmoData ammo || (ammo.ammoType != EAmmoType.None && ammo.ammoType == RangedAmmoType());
	}

	// Empty an equip slot; its item stays where it is. False when it was empty.
	public bool Unequip(EInventorySlot slot)
	{
		if (GetEquipped(slot) == null)
		{
			return false;
		}
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
	// original stays in inventory (and in any slot pointing at it) with reduced
	// count and a fresh ItemState of the same kind is spawned with the dropped
	// count.
	public void Drop(ItemState item, int? stackCount = null)
	{
		if (item?.data == null || _owner == null || !Locate(item, out CarriedGrid grid, out int index))
		{
			return;
		}
		// Take carves a partial drop oldest-first with its real spoil cohorts, so
		// dropping half a stack neither resets nor starts a clock.
		int count = stackCount.HasValue ? Math.Max(1, stackCount.Value) : item.stackCount;
		_owner.DropAtFeet(Take(grid, index, count));
	}

	// ---- Belt ------------------------------------------------------------------

	// What may go on the belt: what the player uses from the hotbar.
	public static bool Beltable(ItemData data) => data != null && data.Category == EItemCategory.Usable;

	public int BeltSize => _belt.Length;

	// The item belt slot `index` points at, or null (empty, or out of range).
	public ItemState BeltAt(int index) => index >= 0 && index < _belt.Length ? _belt[index] : null;

	// The belt slot pointing at `item`, or -1.
	public int BeltIndexOf(ItemState item) => item == null ? -1 : Array.IndexOf(_belt, item);

	public bool IsBelted(ItemState item) => BeltIndexOf(item) >= 0;

	// The first empty belt slot, or -1 when the belt is full.
	public int FirstFreeBeltSlot() => Array.IndexOf(_belt, null);

	// The belt slot by slot, null where empty.
	public IReadOnlyList<ItemState> BeltSlots => _belt;

	private bool BeltHolds(ItemData data)
	{
		foreach (ItemState item in _belt)
		{
			if (item?.data == data)
			{
				return true;
			}
		}
		return false;
	}

	// Point belt slot `index` at the top-level stack `item`; null empties it. An
	// item already on the belt moves here, trading places with what `index` held.
	// False when nothing changed or `item` can't go on the belt.
	public bool SetBelt(int index, ItemState item)
	{
		if (index < 0 || index >= _belt.Length || _belt[index] == item)
		{
			return false;
		}
		if (item != null && (!Beltable(item.data) || !IsOnTop(item)))
		{
			return false;
		}
		int from = BeltIndexOf(item);
		if (from >= 0)
		{
			_belt[from] = _belt[index];
		}
		_belt[index] = item;
		Changed();
		return true;
	}

	// Take `item` off the belt, or put it on the first free belt slot. False when
	// nothing changed.
	public bool ToggleBelt(ItemState item)
	{
		int at = BeltIndexOf(item);
		if (at >= 0)
		{
			return SetBelt(at, null);
		}
		int free = FirstFreeBeltSlot();
		return free >= 0 && SetBelt(free, item);
	}

	// True when `item` could go on the belt right now: beltable, on the
	// backpack's top level, and a belt slot is free.
	public bool CanBelt(ItemState item)
	{
		return item != null && Beltable(item.data) && IsOnTop(item) && FirstFreeBeltSlot() >= 0;
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

	// Resize the belt and the backpack to what the worn armor allows. Run on every
	// change, so no equip path has to remember it. A lost belt slot only loses its
	// pointer. What sat in a lost backpack slot is repacked into free slots, and
	// what doesn't fit is dropped at the member's feet. True when the backpack
	// changed size.
	private bool ApplyCapacity()
	{
		ComposeCapacity(out int belt, out int backpack);
		if (belt != _belt.Length)
		{
			Array.Resize(ref _belt, belt);
		}
		if (backpack == _backpackGrid.Capacity)
		{
			return false;
		}
		var evicted = new List<ItemState>();
		_backpackGrid.Resize(backpack, evicted);
		// An evicted sack is in no grid now; repacking must not fill it.
		SyncSacks();
		foreach (ItemState item in evicted)
		{
			ItemState leftover = AddPreferring(Backpack, item);
			if (leftover == null)
			{
				continue;
			}
			if (_owner != null)
			{
				_owner.DropAtFeet(leftover);
			}
			else
			{
				GD.PushError($"Inventory: no room for '{leftover.data?.ResourcePath}' after a resize, and no owner to drop it — it is lost.");
			}
		}
		return true;
	}

	// ---- Carried light ---------------------------------------------------------

	// The lantern in the Lantern slot, lit or not; null when the slot is empty.
	public LanternState EquippedLantern => _equipped[(int)EInventorySlot.Lantern] as LanternState;

	// The carried light burning — a lantern or a torch — or null.
	public ItemState LitLight => _lit;

	public LanternState LitLantern => _lit as LanternState;

	public bool IsLit(ItemState item) => item != null && item == _lit;

	// Light the lantern in the Lantern slot, putting out a lit torch. False when
	// the slot is empty. The oil gate is the carrier's (Player.LightLantern).
	public bool LightLantern()
	{
		return EquippedLantern != null && SetLit(EquippedLantern);
	}

	// Light `torch`, putting out any other light. False unless it is belted.
	public bool LightTorch(TorchState torch)
	{
		return torch != null && IsBelted(torch) && SetLit(torch);
	}

	private bool SetLit(ItemState light)
	{
		if (_lit != light)
		{
			_lit = light;
			Changed();
		}
		return true;
	}

	// Put out the carried light. False when none was lit.
	public bool Extinguish()
	{
		if (_lit == null)
		{
			return false;
		}
		_lit = null;
		Changed();
		return true;
	}

	// A light can only burn where it is lit from: the equipped lantern, a belted
	// torch. Anything else puts it out.
	private void ReconcileLit()
	{
		bool burns = _lit is LanternState
			? _lit == EquippedLantern
			: _lit is TorchState && IsBelted(_lit);
		if (!burns)
		{
			_lit = null;
		}
	}

	// ---- Hotbar --------------------------------------------------------------

	// Hotbar index 0 is the Lantern slot — always shown, whether or not it holds
	// a lantern — and index 1 + i is belt slot i.
	public const int LanternHotbarIndex = 0;

	public static int HotbarIndexOfBelt(int beltIndex) => 1 + beltIndex;

	public int HotbarSize => 1 + _belt.Length;

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
		return index > 0 ? BeltAt(index - 1) : null;
	}

	// Select hotbar index `index` — the position the HUD shows it at. False when
	// it is out of range or empty.
	public bool SelectHotbarIndex(int index)
	{
		if (index < 0 || index >= HotbarSize || HotbarItemAt(index) == null)
		{
			return false;
		}
		if (_hotbarSelected != index)
		{
			_hotbarSelected = index;
			onChanged?.Invoke();
		}
		return true;
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

	// The stack in the Ammo slot; null when it is empty.
	private ItemState EquippedAmmo => _equipped[(int)EInventorySlot.Ammo];

	private EAmmoType RangedAmmoType() => GetWeapon(EInventorySlot.WeaponRight)?.data?.ammoType ?? EAmmoType.None;

	// The equipped ammo when `weapon` fires its type, else null.
	private ItemState AmmoFor(WeaponData weapon)
	{
		ItemState ammo = EquippedAmmo;
		return weapon != null && weapon.NeedsAmmo && ammo?.data is AmmoData data && data.ammoType == weapon.ammoType
			? ammo
			: null;
	}

	// Rounds `weapon` has left to fire: the equipped ammo's (_ammoRounds) when the
	// weapon fires its type, or a thrown weapon's own stack. MaxValue for a weapon
	// that needs none.
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
		if (!data.NeedsAmmo)
		{
			return int.MaxValue;
		}
		return AmmoFor(data) != null ? _ammoRounds : 0;
	}

	// Spend up to `count` rounds for a shot of `weapon`: units of the equipped ammo
	// (the slot moving on to the next stack as one empties), or of a thrown
	// weapon's own equipped stack. `round` is the kind spent (the weapon itself
	// when thrown, null when it needs none). `piece` is the very item spent when
	// it is one of a kind — a non-stacking thrown weapon, which flies and lands as
	// itself so its wear comes back with it; null for units of a stack. Returns
	// the rounds spent — `count` for a weapon that needs none.
	public int SpendRounds(WeaponState weapon, int count, out ItemData round, out ItemState piece)
	{
		round = null;
		piece = null;
		WeaponData data = weapon?.data;
		if (data == null || count <= 0)
		{
			return 0;
		}
		if (data.thrown)
		{
			round = data;
			return SpendThrown(weapon, count, out piece);
		}
		if (!data.NeedsAmmo)
		{
			return count;
		}
		ItemState ammo = AmmoFor(data);
		round = ammo?.data;
		int spent = 0;
		bool refilled = false;
		while (ammo != null && spent < count)
		{
			spent += ammo.Consume(count - spent);
			if (ammo.stackCount > 0)
			{
				break;
			}
			int index = _backpackGrid.IndexOf(ammo);
			if (index >= 0)
			{
				_backpackGrid[index] = null;
			}
			ammo = FindAmmo(data.ammoType, (AmmoData)ammo.data);
			SetSlot(EInventorySlot.Ammo, ammo);
			refilled = true;
		}
		if (refilled)
		{
			onSlotChanged?.Invoke(EInventorySlot.Ammo);
		}
		if (spent > 0)
		{
			Changed();
		}
		return spent;
	}

	// A thrown weapon spends its own stack — or, when it doesn't stack, leaves
	// the backpack whole as `piece`. The last unit leaves, and the slot moves on to
	// another carried one of the same weapon.
	private int SpendThrown(WeaponState weapon, int count, out ItemState piece)
	{
		piece = null;
		if (!IsEquipped(weapon))
		{
			return 0;
		}
		int spent;
		if (weapon.data.IsStackable)
		{
			spent = weapon.Consume(count);
			if (weapon.stackCount > 0)
			{
				Changed();
				return spent;
			}
		}
		else
		{
			spent = 1;
			piece = weapon;
		}
		// A stackable kind's slot follows its next stack itself (ReconcilePointers);
		// a one-of-a-kind weapon's twin has to be picked out.
		Remove(weapon);
		if (!weapon.data.IsStackable)
		{
			Equip(TopStackOf(weapon.data));
		}
		return spent;
	}

	// Units of `data` across the backpack's top level.
	private int CountOnTop(ItemData data)
	{
		int count = 0;
		for (int i = 0; i < _backpackGrid.Capacity; i++)
		{
			if (_backpackGrid[i]?.data == data)
			{
				count += _backpackGrid[i].stackCount;
			}
		}
		return count;
	}

	private void CountAmmoRounds()
	{
		ItemState ammo = EquippedAmmo;
		_ammoRounds = ammo == null ? 0 : CountOnTop(ammo.data);
	}

	// A top-level backpack stack of `type` for the Ammo slot: `kind` first when
	// given, then the type's standard round, then the first kind of the type.
	// Null when none is carried.
	private ItemState FindAmmo(EAmmoType type, AmmoData kind)
	{
		ItemState standard = null;
		ItemState first = null;
		for (int i = 0; i < _backpackGrid.Capacity; i++)
		{
			if (_backpackGrid[i]?.data is not AmmoData ammo || ammo.ammoType != type)
			{
				continue;
			}
			if (ammo == kind)
			{
				return _backpackGrid[i];
			}
			if (ammo.isStandard)
			{
				standard ??= _backpackGrid[i];
			}
			first ??= _backpackGrid[i];
		}
		return standard ?? first;
	}

	// The Ammo slot only ever holds rounds the right-hand weapon fires (EquipFits).
	// When the weapon's ammo type changes, point the slot at carried ammo of the
	// new type (FindAmmo, standard first), or empty it when there is none or no
	// ranged weapon. Never acts while the type stays the same, so the player's
	// choice among kinds of that type sticks. True when the slot changed.
	private bool ReconcileAmmo()
	{
		EAmmoType type = RangedAmmoType();
		if (type == _rangedAmmoType)
		{
			return false;
		}
		_rangedAmmoType = type;
		ItemState current = EquippedAmmo;
		if (current != null && EquipFits(current.data))
		{
			return false;
		}
		ItemState next = type == EAmmoType.None ? null : FindAmmo(type, null);
		if (next == current)
		{
			return false;
		}
		SetSlot(EInventorySlot.Ammo, next);
		return true;
	}

	// ---- Grid moves (CarriedGrid)----------------------------------------------

	// Every move between grids (the stash screen, chests and merchants) and
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
		if (displaced != null || (leftover?.stackCount ?? 0) != before)
		{
			Changed();
		}
		return leftover;
	}

	// Move `count` units between two slots of this member's grids — the same grid,
	// or the backpack and a sack. Nothing enters or leaves the inventory; a slot
	// pointing at the moved stack follows it while it stays on the backpack's top
	// level. Refused when either side of a swap lands somewhere that won't take
	// it. False when nothing moved.
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
		Changed();
		return true;
	}

	// ---- Enumeration -----------------------------------------------------------

	// Every owned item: the backpack, each sack followed by its contents.
	public IEnumerable<ItemState> EnumerateAll()
	{
		return _backpackGrid.EnumerateAll();
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

	// Remove every item that isn't equipped and return them, for the death sack
	// (Sim.DropDeathSack); belted items go with the rest. Worn gear — the lantern
	// slot included, so the wake at the campfire isn't blind — stays in its slot.
	public List<ItemState> TakeDeathDrop()
	{
		var taken = new List<ItemState>();
		for (int i = 0; i < _backpackGrid.Capacity; i++)
		{
			ItemState item = _backpackGrid[i];
			if (item != null && !IsEquipped(item))
			{
				taken.Add(item);
			}
		}
		foreach (ItemState item in taken)
		{
			Remove(item);
		}
		return taken;
	}

	// ---- Save ------------------------------------------------------------------

	// Inside a shared EntitySerializer table (SaveGame). The backpack slot by
	// slot, gaps included and at its own size (the worn armor that sizes it is
	// inside it), then every pointer as a top-level backpack index: the equip
	// slots, the belt, the lit light, then the hotbar selection.
	private const int SavedNone = -1;
	private const int SavedLanternLit = -2;

	public void Serialize(BinaryWriter w)
	{
		_backpackGrid.Serialize(w);
		for (int i = (int)EInventorySlot.None + 1; i < (int)EInventorySlot.Count; i++)
		{
			w.Write(_backpackGrid.IndexOf(_equipped[i]));
		}
		w.Write(_belt.Length);
		foreach (ItemState item in _belt)
		{
			w.Write(_backpackGrid.IndexOf(item));
		}
		w.Write(_lit is LanternState ? SavedLanternLit : _lit != null ? _backpackGrid.IndexOf(_lit) : SavedNone);
		w.Write(_hotbarSelected);
	}

	// Replaces everything carried with what the save holds. Changed() then sizes
	// the backpack to the restored armor, repacking — or dropping with a warning
	// — what no longer has a slot.
	public void Restore(BinaryReader r)
	{
		for (int i = 0; i < _equipped.Length; i++)
		{
			if (_equipped[i] != null)
			{
				SetSlot((EInventorySlot)i, null);
			}
		}
		_backpackGrid.RestoreAtSavedSize(r);
		for (int i = (int)EInventorySlot.None + 1; i < (int)EInventorySlot.Count; i++)
		{
			EInventorySlot slot = (EInventorySlot)i;
			ItemState item = _backpackGrid.At(r.ReadInt32());
			if (item != null && item.data?.EquipSlotKind == slot)
			{
				SetSlot(slot, item);
			}
			onSlotChanged?.Invoke(slot);
		}
		_belt = new ItemState[r.ReadInt32()];
		for (int i = 0; i < _belt.Length; i++)
		{
			ItemState item = _backpackGrid.At(r.ReadInt32());
			_belt[i] = Beltable(item?.data) && BeltIndexOf(item) < 0 ? item : null;
		}
		int lit = r.ReadInt32();
		_lit = lit == SavedLanternLit ? EquippedLantern : _backpackGrid.At(lit) as TorchState;
		_hotbarSelected = r.ReadInt32();
		// The saved ammo slot is the player's choice, not a weapon change to react to.
		_rangedAmmoType = RangedAmmoType();
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
