using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// Everything one party member carries: ONE sparse backpack array holding every
// owned item — materials, weapons, armor, lanterns, potions — plus which of those
// items fill the equip slots. Equipping never moves an item: an equip slot is a
// reference to an item that stays where it is in the backpack, and equipping one
// unequips whatever held that slot. The first PlayerData.hotbarSize slots double
// as the HUD hotbar.
public class Inventory : IItemGrid
{
	private readonly Player _owner;
	private readonly PlayerData _data;

	// `_backpack[i]` is the item at grid slot i, or null — the player can leave
	// gaps when reorganizing. Every owned item is in here exactly once.
	private readonly ItemGrid _backpack;

	// Indexed by EInventorySlot; null = empty. INVARIANT: every non-null entry is
	// also in _backpack — Remove and every other exit path clear the slot first.
	private readonly ItemState[] _equipped = new ItemState[(int)EInventorySlot.Count];

	// Backpack index of the hotbar selection, or -1 when the hotbar is empty.
	// Stored as an index (not an item) so the cursor holds its place when the
	// selected stack is used up; Changed() snaps it to the next filled slot.
	private int _hotbarSelected = -1;

	// An equip slot changed occupant (always followed by onChanged).
	public Action<EInventorySlot> onSlotChanged;
	// Generic "something in the inventory changed" pulse, including a hotbar
	// selection move. UI panels that re-derive everything listen to this.
	public Action onChanged;

	// Fire onChanged from outside the Inventory class — used by the action
	// runner's DecrementStack handler when it mutates item.stackCount
	// directly without going through one of Inventory's mutation methods.
	public void NotifyChanged()
	{
		Changed();
	}

	// Sparse view: Backpack[i] is the item at slot i, or null if empty. Count
	// is the capacity, NOT the occupancy.
	public IReadOnlyList<ItemState> Backpack => _backpack.Slots;

	public Inventory(Player owner, PlayerData data)
	{
		_owner = owner;
		_data = data;
		_backpack = new ItemGrid(data.backpackCapacity);
	}

	private void Changed()
	{
		ReconcileHotbarSelection();
		onChanged?.Invoke();
	}

	public int IndexOfInBackpack(ItemState item) => _backpack.IndexOf(item);

	// True if `item` is owned — i.e. in the backpack, equipped or not.
	// Non-allocating (safe to call from per-frame paths).
	public bool Contains(ItemState item) => IndexOfInBackpack(item) >= 0;

	// True if the backpack already holds a stack of exactly `data`. Non-allocating.
	public bool HoldsStackOf(ItemData data)
	{
		if (data == null || !data.IsStackable)
		{
			return false;
		}
		for (int i = 0; i < _backpack.Capacity; i++)
		{
			if (_backpack[i]?.data == data)
			{
				return true;
			}
		}
		return false;
	}

	// A material lands in the first free slot PAST the hotbar, so loot doesn't
	// crowd out what the player uses in the field; only a full pack spills it
	// into the hotbar. Anything else takes the first free slot.
	private int FirstEmptySearchStart(ItemState item)
	{
		return item?.data != null && item.data.IsMaterial ? HotbarSize : 0;
	}

	// Spend up to `count` units of a reagent from the backpack, matching by the
	// item's parent chain (a reagent naming goblin_meat draws from any goblin
	// subspecies meat). Emptied stacks free their slot. Returns how many units
	// were actually spent (may be < count if the backpack ran short — the caller
	// covers the remainder from the party stash). Used by the alchemy cast path.
	public int SpendMaterial(ItemData reagentItem, int count)
	{
		if (reagentItem == null || count <= 0)
		{
			return 0;
		}
		int spent = 0;
		for (int i = 0; i < _backpack.Capacity && spent < count; i++)
		{
			ItemState s = _backpack[i];
			if (s?.data == null || !s.data.IsMaterial || s.stackCount <= 0 || !Cooking.Satisfies(s.data, reagentItem))
			{
				continue;
			}
			int take = s.Consume(count - spent);
			spent += take;
			if (s.stackCount <= 0)
			{
				_backpack[i] = null;
			}
		}
		if (spent > 0)
		{
			Changed();
		}
		return spent;
	}

	// Null before the player has a live world (never during normal play).
	private ItemGrid PartyStash => _owner?.Sim?.WorldState?.SimState?.PartyStash;

	// Hand an item the player owns but can't carry (a starting loadout that
	// overflows the backpack) to the party stash. A weapon forfeits its
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

	// Add an item to the backpack. Stackables fill existing same-kind stacks
	// first, then take the first empty slot. Returns the number of units actually
	// added (0 = nothing fit, less than stackCount = partial). The caller's
	// ItemState is consumed: if it was stored it is the backpack's reference now;
	// if it fully merged it is spent. Never equips — that is the caller's call.
	public int TryAdd(ItemState item)
	{
		if (item == null || item.data == null || item.stackCount <= 0 || !item.data.IsCarriable)
		{
			return 0;
		}

		// Entering the inventory marks the item as touched for the rest of its
		// life (see ItemState.touched). Stamp before the merge so even units
		// that fold into an existing stack count as handled.
		MarkAcquired(item);

		int initialStack = item.stackCount;
		// Matching stacks first (oldest-first, cohorts preserved), then the first
		// empty slot, where the ref itself is stored.
		ItemState leftover = _backpack.Add(item, FirstEmptySearchStart(item));
		int totalAdded = initialStack - (leftover?.stackCount ?? 0);
		if (totalAdded > 0)
		{
			Changed();
		}
		return totalAdded;
	}

	// Every path an item enters the inventory through routes its touched/spoil
	// bookkeeping here: mark it handled for life (see ItemState.touched) and, if
	// it's a perishable not already dated, start its spoil clock so it expires
	// spoilDays from now wherever it comes to rest (backpack, later the stash).
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

	// True if `count` units of `data` would ALL fit right now. Mirrors TryAdd's
	// placement WITHOUT mutating anything — pickup gates use it so a pickup only
	// commits (and a loot only flies in) when the whole stack lands, never
	// leaving a partial pile bonking the player.
	public bool CanFullyAdd(ItemData data, int count)
	{
		return data != null && data.IsCarriable && _backpack.CanFullyAdd(data, count);
	}

	// Removes an item from the inventory entirely (dropped, sold, stashed, spent),
	// unequipping it first. No-op for an item that isn't owned.
	public void Remove(ItemState item)
	{
		int idx = IndexOfInBackpack(item);
		if (idx < 0)
		{
			return;
		}

		OnLeaving(item);
		_backpack[idx] = null;
		Changed();
	}

	// The single chokepoint for an item leaving the inventory, by any path. A
	// weapon destroys the arrows it left lying around, no refund (the recharge
	// timer keeps its deadline and refills the magazine if it is re-acquired);
	// equip / unequip / slot moves don't come through here, so an unequipped bow
	// keeps its arrows. An equipped item is unequipped first.
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

	// Equip a backpack item into its category's slot, unequipping (not removing)
	// whatever held it. False for an item that isn't owned or isn't equippable,
	// or a lantern with an empty tank — an equipped lantern is a lit one.
	public bool Equip(ItemState item)
	{
		EInventorySlot slot = item?.data?.EquipSlotKind ?? EInventorySlot.None;
		if (slot == EInventorySlot.None || !Contains(item) || item is LanternState { HasFuel: false })
		{
			return false;
		}
		if (_equipped[(int)slot] == item)
		{
			return true;
		}
		SetSlot(slot, item);
		NotifySlot(slot);
		return true;
	}

	// Empty an equip slot; its item stays where it is in the backpack.
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
	// original stays in inventory with reduced count and a fresh ItemState of
	// the same kind is spawned with the dropped count.
	public void Drop(ItemState item, int? stackCount = null)
	{
		if (item?.data == null || _owner == null || !Contains(item))
		{
			return;
		}
		// Take carves a partial drop oldest-first with its real spoil cohorts, so
		// dropping half a stack neither resets nor starts a clock.
		int count = stackCount.HasValue ? Math.Max(1, stackCount.Value) : item.stackCount;
		_owner.DropAtFeet(Take(IndexOfInBackpack(item), count));
	}

	// ---- Hotbar --------------------------------------------------------------

	// How many leading backpack slots the hotbar spans.
	public int HotbarSize => Math.Min(Math.Max(0, _data.hotbarSize), _backpack.Capacity);

	// Backpack index of the selected hotbar entry, or -1 when the hotbar is empty.
	public int SelectedHotbarIndex => _hotbarSelected;
	public ItemState SelectedHotbarItem => _hotbarSelected >= 0 ? _backpack[_hotbarSelected] : null;

	// The FILLED hotbar slots, as backpack indices in slot order — what the HUD
	// shows. Cleared first; caller owns the list.
	public void GetHotbarEntries(List<int> into)
	{
		into.Clear();
		int size = HotbarSize;
		for (int i = 0; i < size; i++)
		{
			if (_backpack[i] != null)
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
			if (_backpack[i] == null)
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
			if (_backpack[i] != null)
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
		if (_hotbarSelected >= 0 && _hotbarSelected < size && _backpack[_hotbarSelected] != null)
		{
			return;
		}
		int from = _hotbarSelected >= 0 && _hotbarSelected < size ? _hotbarSelected : size - 1;
		_hotbarSelected = size > 0 ? NextFilledHotbarIndex(from, 1, size) : -1;
		if (_hotbarSelected < 0 && size > 0 && _backpack[from] != null)
		{
			_hotbarSelected = from;
		}
	}

	// ---- Backpack grid moves ---------------------------------------------------

	// IItemGrid over the backpack. Every move between grids (the stash screen,
	// later chests and merchants) and within this one comes through these, so
	// the touched / spoil stamp on the way in and OnLeaving on the way out can't
	// be skipped.

	public int Capacity => _backpack.Capacity;

	public ItemState At(int index) => _backpack.At(index);

	public ItemState Take(int index, int count)
	{
		ItemState source = _backpack.At(index);
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

	public ItemState PlaceAt(int index, ItemState incoming, bool allowSwap, out ItemState displaced)
	{
		displaced = null;
		if (incoming?.data == null || !incoming.data.IsCarriable)
		{
			return incoming;
		}
		MarkAcquired(incoming);
		int before = incoming.stackCount;
		ItemState leftover = _backpack.PlaceAt(index, incoming, allowSwap, out displaced);
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

	public ItemState Add(ItemState incoming)
	{
		if (incoming == null)
		{
			return null;
		}
		TryAdd(incoming);
		return incoming.stackCount > 0 && !Contains(incoming) ? incoming : null;
	}

	// Equipped items stay equipped when they move — an equip slot references the
	// item, not its position. A stack merged away entirely is gone, so it leaves.
	public bool MoveWithin(int from, int to, int count)
	{
		ItemState source = _backpack.At(from);
		if (!_backpack.MoveWithin(from, to, count))
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

	// Every owned item.
	public IEnumerable<ItemState> EnumerateAll()
	{
		for (int i = 0; i < _backpack.Capacity; i++)
		{
			if (_backpack[i] != null) { yield return _backpack[i]; }
		}
	}

	// Worn armor only — the pieces that transmit wetness into the wearer's meter
	// (Player.TickWetEffect) and count toward max armor.
	public IEnumerable<ArmorState> EnumerateEquippedArmor()
	{
		if (_equipped[(int)EInventorySlot.Helmet] is ArmorState head) { yield return head; }
		if (_equipped[(int)EInventorySlot.Armor] is ArmorState body) { yield return body; }
	}

	// Every owned armor piece, worn or not, for wetness ticking.
	public IEnumerable<ArmorState> EnumerateAllArmor()
	{
		for (int i = 0; i < _backpack.Capacity; i++)
		{
			if (_backpack[i] is ArmorState armor) { yield return armor; }
		}
	}

	// ---- Bulk exits ------------------------------------------------------------

	// Remove everything the member carries but the equipped lantern and return it,
	// for the death sack (Sim.DropDeathSack). The lantern stays so the wake at the
	// campfire isn't blind. Goes through Remove, so a weapon forfeits its loose
	// arrows as on any other exit.
	public List<ItemState> TakeDeathDrop()
	{
		ItemState lantern = GetEquipped(EInventorySlot.Lantern);
		var taken = new List<ItemState>();
		foreach (ItemState item in EnumerateAll())
		{
			if (item != lantern)
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

	// The equip slots a save carries, in wire order.
	private static readonly EInventorySlot[] SavedSlots =
	{
		EInventorySlot.Helmet, EInventorySlot.Armor, EInventorySlot.WeaponLeft,
		EInventorySlot.WeaponRight, EInventorySlot.Lantern,
	};

	// Inside a shared EntitySerializer table (SaveGame). The backpack is written
	// slot by slot, gaps included, so the player's layout survives; each equip
	// slot is the backpack index of its item, -1 for empty.
	public void Serialize(BinaryWriter w)
	{
		w.Write(_backpack.Capacity);
		for (int i = 0; i < _backpack.Capacity; i++)
		{
			EntitySerializer.WriteItem(w, _backpack[i]);
		}
		foreach (EInventorySlot slot in SavedSlots)
		{
			w.Write(IndexOfInBackpack(GetEquipped(slot)));
		}
		w.Write(_hotbarSelected);
	}

	// Replaces everything carried with what the save holds. An item that no
	// longer fits (the backpack shrank) is dropped with a warning.
	public void Restore(BinaryReader r)
	{
		for (int i = 0; i < _equipped.Length; i++)
		{
			if (_equipped[i] != null)
			{
				SetSlot((EInventorySlot)i, null);
			}
		}
		_backpack.Clear();
		int savedCount = r.ReadInt32();
		var saved = new ItemState[savedCount];
		var overflow = new List<ItemState>();
		for (int i = 0; i < savedCount; i++)
		{
			saved[i] = EntitySerializer.ReadItem(r);
			if (saved[i] == null)
			{
				continue;
			}
			if (i < _backpack.Capacity)
			{
				_backpack[i] = saved[i];
			}
			else
			{
				overflow.Add(saved[i]);
			}
		}
		foreach (ItemState item in overflow)
		{
			if (_backpack.Add(item, FirstEmptySearchStart(item)) != null)
			{
				GD.PushWarning($"Inventory: no backpack room for saved '{item.data?.ResourcePath}' — dropping it.");
			}
		}
		foreach (EInventorySlot slot in SavedSlots)
		{
			int idx = r.ReadInt32();
			ItemState item = idx >= 0 && idx < savedCount ? saved[idx] : null;
			if (item != null && Contains(item))
			{
				SetSlot(slot, item);
			}
			onSlotChanged?.Invoke(slot);
		}
		_hotbarSelected = r.ReadInt32();
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
