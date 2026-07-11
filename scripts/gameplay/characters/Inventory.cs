using System;
using System.Collections.Generic;
using Godot;

// Everything the player carries: the singular equip slots (helmet / armor /
// melee / ranged weapon / lantern), the 3-slot Equipment hotbar (potions / food —
// EItemCategory.Equipment), and the material-only backpack. The backpack holds
// ONLY materials; weapons / armor / equipment never enter it. Displacing a piece
// out of an equip slot (equip-over) sends it to the world-scope party equipment
// stash (WorldSimState.PartyEquipmentStash), not back to the backpack. Weapons /
// armor / helmets can't be unequipped or dropped once worn — they change only by
// being replaced.
public class Inventory
{
	private readonly Player _owner;
	private readonly PlayerData _data;

	// Backpack: items the player owns that are not currently in an equip or
	// consumable slot. Fixed-size sparse array — `_backpack[i]` is the item
	// at grid slot i, or null if that slot is empty. The sparse layout means
	// the player can leave gaps when reorganizing (move an item to slot 5
	// even if slot 3 is empty). BackpackCount is the non-null count;
	// Backpack.Count is the array length (== backpackCapacity).
	private readonly ItemState[] _backpack;

	// Equip slot pointers — null means empty. Each pointer is also present in
	// _items via _backpack OR via _consumableSlots (for consumables) when
	// equipped. To enumerate every item the player owns, use EnumerateAll().
	private ItemState _helmet;
	private ItemState _armor;
	private WeaponState _weaponMelee;
	private WeaponState _weaponRanged;
	// The player's lantern — its own permanent slot, seeded at spawn and toggled
	// on/off with the Lantern input. Never enters the Equipment hotbar (lanterns
	// are refused there); enumerated in EnumerateAll so its fuel ticks / refuels
	// and its lit state lights the player like any carried torch.
	private ItemState _lantern;

	// Consumable hotbar — fixed-size, indexed by activeConsumableIndex. Empty
	// slots hold null. Sized from PlayerData.consumableSlotCount at construction.
	private readonly ItemState[] _consumableSlots;
	private int _activeConsumableIndex = -1;

	public Action<EInventorySlot> onSlotChanged;
	public Action<int> onActiveConsumableChanged;
	// Generic "something in the inventory changed" pulse. Fires for slot
	// equips, stack-count mutations from runner events (DoDecrementStack),
	// and any other path that doesn't fit the slot/consumable callbacks
	// above. UI panels that re-derive everything from a Refresh() call
	// listen to this; slot-specific consumers can keep using the typed
	// signals.
	public Action onChanged;

	// Fire onChanged from outside the Inventory class — used by the action
	// runner's DecrementStack handler when it mutates item.stackCount
	// directly without going through one of Inventory's mutation methods.
	public void NotifyChanged()
	{
		onChanged?.Invoke();
	}

	public int BackpackCapacity => _data.backpackCapacity;
	public int BackpackCount
	{
		get
		{
			int c = 0;
			for (int i = 0; i < _backpack.Length; i++)
			{
				if (_backpack[i] != null) { c++; }
			}
			return c;
		}
	}
	public int ConsumableSlotCount => _consumableSlots.Length;
	public int ActiveConsumableIndex => _activeConsumableIndex;

	public Inventory(Player owner, PlayerData data)
	{
		_owner = owner;
		_data = data;
		_backpack = new ItemState[Math.Max(0, data.backpackCapacity)];
		_consumableSlots = new ItemState[Math.Max(0, data.consumableSlotCount)];
	}

	// Find the first null backpack slot — used by Add operations to "append"
	// in the leftmost empty position. Returns -1 if the backpack is full.
	private int FindFirstEmptyBackpackIndex()
	{
		for (int i = 0; i < _backpack.Length; i++)
		{
			if (_backpack[i] == null) { return i; }
		}
		return -1;
	}

	// Linear scan for an item by reference. Returns the backpack index or -1.
	private int IndexOfInBackpack(ItemState item)
	{
		if (item == null) { return -1; }
		for (int i = 0; i < _backpack.Length; i++)
		{
			if (_backpack[i] == item) { return i; }
		}
		return -1;
	}

	private bool BackpackContains(ItemState item) => IndexOfInBackpack(item) >= 0;

	// Place `item` into the first empty slot. Returns false if every slot is
	// occupied (caller must handle the no-room case).
	private bool AppendToBackpack(ItemState item)
	{
		int idx = FindFirstEmptyBackpackIndex();
		if (idx < 0) { return false; }
		_backpack[idx] = item;
		return true;
	}

	// Clear the slot holding `item` (by reference). Returns false if the
	// item isn't in the backpack — mirrors List<T>.Remove's signature.
	private bool RemoveFromBackpack(ItemState item)
	{
		int idx = IndexOfInBackpack(item);
		if (idx < 0) { return false; }
		_backpack[idx] = null;
		return true;
	}

	// Add an item to the player's inventory. For stackables, fills existing
	// partial stacks first, then allocates new backpack slots. Returns the
	// number of units actually added (0 = nothing fit, less than stackCount =
	// partial). The caller's ItemState reference is consumed: if fully merged
	// into existing stacks, the original is no longer used; if it's added to
	// the backpack, the original is what's stored.
	// The world-scope party equipment stash — where displaced weapons / armor /
	// equipment go when equipped over. Null before the player has a live world
	// (never during normal play).
	private List<ItemState> PartyEquipmentStash => _owner?.World?.WorldState?.SimState?.PartyEquipmentStash;

	// Hand a displaced equip-slot piece to the party equipment stash, merging
	// stackable equipment into an existing stack when possible. A weapon forfeits
	// its outstanding arrows on the way out, mirroring Remove() — it has left the
	// player's possession.
	private void PushToEquipmentStash(ItemState item)
	{
		if (item == null)
		{
			return;
		}
		if (item is WeaponState ws)
		{
			ws.DestroyOutstandingArrows();
		}
		ItemStash.Add(PartyEquipmentStash, item);
	}

	public int TryAdd(ItemState item)
	{
		if (item == null || item.data == null || item.stackCount <= 0)
		{
			return 0;
		}

		// The backpack holds materials only — weapons / armor / equipment / ammo
		// are refused here (they route to equip slots or the party stashes).
		if (!item.data.IsMaterial)
		{
			return 0;
		}

		// Entering the inventory marks the item as touched for the rest of its
		// life (see ItemState.touched). Stamp before the merge so even units
		// that fold into an existing stack count as handled.
		MarkAcquired(item);

		int initialStack = item.stackCount;

		if (item.data.IsStackable)
		{
			// Fill partial stacks of the same kind anywhere in inventory.
			foreach (ItemState existing in EnumerateAll())
			{
				if (item.stackCount <= 0)
				{
					break;
				}
				if (!existing.CanStackWith(item))
				{
					continue;
				}
				int space = existing.RemainingStackSpace();
				if (space <= 0)
				{
					continue;
				}
				int moved = Math.Min(space, item.stackCount);
				existing.stackCount += moved;
				item.stackCount -= moved;
			}
		}

		// Anything left lands in the first empty backpack slot.
		if (item.stackCount > 0 && AppendToBackpack(item))
		{
			item.stackCount = initialStack;  // not actually consumed; full ref stored
			onChanged?.Invoke();
			return initialStack;
		}

		int totalAdded = initialStack - item.stackCount;
		if (totalAdded > 0)
		{
			onChanged?.Invoke();
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
		if (item.removeOnDay == 0 && item.data != null && item.data.spoilDays > 0)
		{
			item.removeOnDay = (_owner?.World?.DayNumber ?? 0) + item.data.spoilDays;
		}
	}

	// True if `count` units of material `data` would ALL fit right now. Mirrors
	// TryAdd's placement (fill same-kind partial stacks, then empty backpack
	// slots) WITHOUT mutating anything — the field auto-pickup / loot-magnet gate
	// uses it so a pickup only commits (and a loot only flies in) when the whole
	// stack lands, never leaving a partial pile bonking the player.
	public bool CanFullyAdd(ItemData data, int count)
	{
		if (data == null || !data.IsMaterial || count <= 0)
		{
			return false;
		}
		int remaining = count;
		// A fresh pickup lands with this spoil deadline; only same-deadline stacks
		// can absorb it (ItemState.CanStackWith), so partial space in a different
		// spoil cohort must not count toward "fits".
		int freshRemoveDay = data.spoilDays > 0 ? (_owner?.World?.DayNumber ?? 0) + data.spoilDays : 0;
		if (data.IsStackable)
		{
			foreach (ItemState existing in EnumerateAll())
			{
				if (existing.data != data || existing.removeOnDay != freshRemoveDay)
				{
					continue;
				}
				remaining -= existing.RemainingStackSpace();
				if (remaining <= 0)
				{
					return true;
				}
			}
		}
		int emptySlots = 0;
		for (int i = 0; i < _backpack.Length; i++)
		{
			if (_backpack[i] == null)
			{
				emptySlots++;
			}
		}
		int perSlot = data.IsStackable ? Math.Max(1, data.maxStack) : 1;
		return remaining <= emptySlots * perSlot;
	}

	// Removes an item from wherever it lives in the inventory. If it's
	// equipped, the slot is also cleared. Caller must ensure the item is
	// actually present.
	public void Remove(ItemState item)
	{
		if (item == null)
		{
			return;
		}

		// Leaving the inventory entirely (dropped, sold, stashed) forfeits a
		// weapon's in-world ammo — destroy the arrows it left lying around, no
		// refund. The recharge timer keeps its deadline and refills the magazine
		// if the weapon is ever re-acquired, so nothing is permanently lost, just
		// the loose arrows. This is the single chokepoint for "weapon leaves the
		// inventory" — equip/unequip/slot-moves don't route through Remove, so a
		// holstered or re-slotted bow keeps its arrows. No-op for non-weapons and
		// for a weapon with nothing outstanding.
		if (item is WeaponState ws)
		{
			ws.DestroyOutstandingArrows();
		}

		EInventorySlot? equippedSlot = GetEquippedSlot(item);
		if (equippedSlot.HasValue)
		{
			SetSlot(equippedSlot.Value, null);
			NotifySlot(equippedSlot.Value);
		}

		for (int i = 0; i < _consumableSlots.Length; i++)
		{
			if (_consumableSlots[i] == item)
			{
				if (_activeConsumableIndex == i && item is ConsumableState cs)
				{
					cs.OnUnequipped(_owner);
				}
				_consumableSlots[i] = null;
				if (_activeConsumableIndex == i)
				{
					_activeConsumableIndex = -1;
					onActiveConsumableChanged?.Invoke(_activeConsumableIndex);
				}
			}
		}

		RemoveFromBackpack(item);
		onChanged?.Invoke();
	}

	public ItemState GetEquipped(EInventorySlot slot)
	{
		return slot switch
		{
			EInventorySlot.Helmet => _helmet,
			EInventorySlot.Armor => _armor,
			EInventorySlot.WeaponMelee => _weaponMelee,
			EInventorySlot.WeaponRanged => _weaponRanged,
			EInventorySlot.Equipment => GetActiveConsumable(),
			EInventorySlot.Lantern => _lantern,
			_ => null,
		};
	}

	public WeaponState GetWeapon(EInventorySlot slot)
	{
		return slot switch
		{
			EInventorySlot.WeaponMelee => _weaponMelee,
			EInventorySlot.WeaponRanged => _weaponRanged,
			_ => null,
		};
	}

	// Equip `item` into one of the SINGULAR slots (helmet / armor / melee /
	// ranged weapon). Any current occupant is displaced to the party equipment
	// stash. The Equipment hotbar is
	// index-addressed — use TryEquipToConsumableSlot for it. The caller owns the
	// source: if `item` came from the equipment stash, remove it there on success.
	// Caller must ensure the item's category matches the slot.
	public bool TryEquip(ItemState item, EInventorySlot slot)
	{
		if (item == null || slot == EInventorySlot.None || slot == EInventorySlot.Equipment)
		{
			return false;
		}

		MarkAcquired(item);

		ItemState prev = GetEquipped(slot);
		if (prev == item)
		{
			return true;
		}

		// Displaced piece returns to the party equipment stash (PushToEquipmentStash
		// forfeits an outgoing weapon's outstanding arrows on the way out). SetSlot
		// fires the outgoing weapon's minion cleanup.
		if (prev != null)
		{
			PushToEquipmentStash(prev);
		}

		SetSlot(slot, item);
		NotifySlot(slot);
		return true;
	}

	// Weapons / armor / helmets can't be unequipped — they only change by being
	// replaced. Equipment returns the ACTIVE hotbar item to the party equipment
	// stash. (To pull a specific hotbar item, use TryRemoveFromConsumableSlot.)
	public bool TryUnequip(EInventorySlot slot)
	{
		if (slot != EInventorySlot.Equipment)
		{
			return false;
		}
		ItemState prev = GetActiveConsumable();
		if (prev == null)
		{
			return true;
		}
		return TryRemoveFromConsumableSlot(prev);
	}

	// Swap items between two equip slots (e.g., WeaponLeft ↔ WeaponRight).
	// Either side may be empty. Consumable slots are NOT supported here — use
	// TryMoveToConsumableSlot which is index-aware. Caller is responsible for
	// kind compatibility (matching TryEquip's convention) — armor head ↔ body
	// is meaningless because armor pieces are tied to one armorSlot, but
	// weapons fit either WeaponLeft or WeaponRight.
	public bool TrySwapEquipSlots(EInventorySlot a, EInventorySlot b)
	{
		if (a == b) { return true; }
		if (a == EInventorySlot.None || b == EInventorySlot.None) { return false; }
		if (a == EInventorySlot.Equipment || b == EInventorySlot.Equipment) { return false; }
		ItemState itemA = GetEquipped(a);
		ItemState itemB = GetEquipped(b);
		SetSlot(a, itemB);
		SetSlot(b, itemA);
		// Two slot signals + one onChanged pulse so the UI rebinds both
		// affected panels.
		onSlotChanged?.Invoke(a);
		onSlotChanged?.Invoke(b);
		onChanged?.Invoke();
		return true;
	}

	// Drop an item: remove from inventory, spawn a Loot in the world. Optional
	// stackCount splits a stack — when set and less than item.stackCount, the
	// original stays in inventory with reduced count and a fresh ItemState of
	// the same kind is spawned with the dropped count.
	public void Drop(ItemState item, int? stackCount = null)
	{
		if (item == null || _owner == null || item.data == null)
		{
			return;
		}

		// Only materials and equipment leave the player by dropping — weapons,
		// armor, and helmets are permanent until replaced, and ammo never lives
		// in the inventory to begin with.
		if (!item.data.IsMaterial && item.data.Category != EItemCategory.Equipment)
		{
			return;
		}

		ItemState dropped;
		if (stackCount.HasValue && item.data != null && item.data.IsStackable && stackCount.Value < item.stackCount)
		{
			int amount = Math.Max(1, stackCount.Value);
			item.stackCount -= amount;
			dropped = new ItemState(item.data);
			dropped.stackCount = amount;
			dropped.touched = item.touched;
			// A split keeps the parent's spoil deadline — dropping half a stack
			// must not reset (or start) its clock.
			dropped.removeOnDay = item.removeOnDay;
		}
		else
		{
			// Dropping is an explicit "set it down" — extinguish any carried-
			// active state (e.g. a lit torch) before Remove() fires onChanged.
			// That way the dropped Loot lands on the ground unlit, the player's
			// carried light reconciles to off if this was their only lit torch,
			// and picking the pile up later doesn't auto-light it again.
			if (item is ConsumableState cs)
			{
				cs.isActive = false;
			}
			Remove(item);
			dropped = item;
		}

		Vector3 pos = _owner.GlobalPosition + Vector3.Up * 0.5f;
		Vector3 forward = -_owner.GlobalTransform.Basis.Z;
		Vector3 impulse = forward * 2f + Vector3.Up * 1.5f;
		// Player drops latch into interact-only mode so the loot doesn't
		// immediately auto-pickup back into the inventory the next time the
		// player steps on it.
		_owner.World?.DropItem(dropped, pos, impulse, requireInteract: true);
		onChanged?.Invoke();
	}

	public ItemState GetActiveConsumable()
	{
		if (_activeConsumableIndex < 0 || _activeConsumableIndex >= _consumableSlots.Length)
		{
			return null;
		}
		return _consumableSlots[_activeConsumableIndex];
	}

	// Move a consumable from the backpack into the first empty consumable slot.
	// Returns false if no empty slot or the item isn't in the backpack.
	public bool TryMoveToConsumableSlot(ItemState item)
	{
		if (item == null || !(item is ConsumableState))
		{
			return false;
		}
		int srcIdx = IndexOfInBackpack(item);
		if (srcIdx < 0)
		{
			return false;
		}
		for (int i = 0; i < _consumableSlots.Length; i++)
		{
			if (_consumableSlots[i] == null)
			{
				_backpack[srcIdx] = null;
				_consumableSlots[i] = item;
				if (_activeConsumableIndex == -1)
				{
					SetActiveConsumableIndex(i);
				}
				onChanged?.Invoke();
				return true;
			}
		}
		return false;
	}

	// Move a consumable into a specific hotbar slot. Item may live in the
	// backpack OR in a different consumable slot. If the target slot is
	// occupied, the previous occupant goes to where `item` came from (a clean
	// swap with no backpack detour for slot ↔ slot moves; backpack ↔ slot
	// puts the displaced item back into the backpack). Used by select-mode
	// UIs that target specific hotbar positions.
	public bool TryMoveToConsumableSlot(ItemState item, int targetIndex)
	{
		if (item == null || !(item is ConsumableState))
		{
			return false;
		}
		if (targetIndex < 0 || targetIndex >= _consumableSlots.Length)
		{
			return false;
		}
		int sourceIndex = -1;
		for (int i = 0; i < _consumableSlots.Length; i++)
		{
			if (_consumableSlots[i] == item)
			{
				sourceIndex = i;
				break;
			}
		}
		int sourceBackpackIndex = sourceIndex < 0 ? IndexOfInBackpack(item) : -1;
		bool fromBackpack = sourceBackpackIndex >= 0;
		if (!fromBackpack && sourceIndex < 0)
		{
			return false;
		}
		if (sourceIndex == targetIndex)
		{
			return true;
		}
		ItemState prev = _consumableSlots[targetIndex];
		// Source-to-destination swap. Active-consumable index follows the
		// item that was active so the hotbar selection feels stable across
		// reorders.
		bool itemWasActive = sourceIndex >= 0 && _activeConsumableIndex == sourceIndex;
		bool prevWasActive = _activeConsumableIndex == targetIndex;
		if (fromBackpack)
		{
			_backpack[sourceBackpackIndex] = null;
			_consumableSlots[targetIndex] = item;
			if (prev != null)
			{
				// Displaced consumable lands in the slot the moved item
				// vacated, preserving the player's grid layout.
				_backpack[sourceBackpackIndex] = prev;
				if (prevWasActive && prev is ConsumableState prevCs)
				{
					prevCs.OnUnequipped(_owner);
				}
			}
			if (_activeConsumableIndex == targetIndex && prev != null)
			{
				_activeConsumableIndex = -1;
				onActiveConsumableChanged?.Invoke(_activeConsumableIndex);
			}
			if (_activeConsumableIndex == -1)
			{
				SetActiveConsumableIndex(targetIndex);
			}
		}
		else
		{
			_consumableSlots[targetIndex] = item;
			_consumableSlots[sourceIndex] = prev;
			if (itemWasActive)
			{
				// Active follows the item that the player was wielding.
				_activeConsumableIndex = targetIndex;
				onActiveConsumableChanged?.Invoke(_activeConsumableIndex);
			}
			else if (prevWasActive)
			{
				_activeConsumableIndex = sourceIndex;
				onActiveConsumableChanged?.Invoke(_activeConsumableIndex);
			}
		}
		onChanged?.Invoke();
		return true;
	}

	// Place a fresh ItemState (typically split off another stack via partial-
	// stack move) into the consumable slot at `targetIndex`. If the slot already
	// holds a same-kind stack, merges into it up to maxStackSize and returns
	// the units placed; otherwise requires the slot to be empty and places the
	// fresh stack. Returns the number of units actually placed (0 = refused).
	public int TryAddToConsumableSlot(ItemState fresh, int targetIndex)
	{
		if (fresh == null || fresh.data == null || fresh.stackCount <= 0)
		{
			return 0;
		}
		if (!(fresh is ConsumableState))
		{
			return 0;
		}
		MarkAcquired(fresh);
		if (targetIndex < 0 || targetIndex >= _consumableSlots.Length)
		{
			return 0;
		}
		ItemState existing = _consumableSlots[targetIndex];
		if (existing == null)
		{
			_consumableSlots[targetIndex] = fresh;
			if (_activeConsumableIndex == -1)
			{
				SetActiveConsumableIndex(targetIndex);
			}
			onChanged?.Invoke();
			return fresh.stackCount;
		}
		if (existing.CanStackWith(fresh) && fresh.data.IsStackable)
		{
			int space = existing.RemainingStackSpace();
			int moved = Math.Min(space, fresh.stackCount);
			if (moved <= 0)
			{
				return 0;
			}
			existing.stackCount += moved;
			onChanged?.Invoke();
			return moved;
		}
		return 0;
	}

	// Swap two backpack slots. The slots may be empty (null) — moving an
	// item from slot A to empty slot B leaves A empty and B holding the item,
	// which is how the player reorganizes their grid. Indices must be in
	// [0, BackpackCapacity).
	public bool TrySwapInBackpack(int sourceIndex, int targetIndex)
	{
		if (sourceIndex < 0 || sourceIndex >= _backpack.Length) { return false; }
		if (targetIndex < 0 || targetIndex >= _backpack.Length) { return false; }
		if (sourceIndex == targetIndex) { return true; }
		(_backpack[sourceIndex], _backpack[targetIndex]) = (_backpack[targetIndex], _backpack[sourceIndex]);
		onChanged?.Invoke();
		return true;
	}

	// Move `amount` units from `source` into the backpack slot at `targetIndex`.
	// If the slot holds a same-kind stackable, merges into it; if empty, places
	// a fresh stack of `amount` units there. Different-kind occupancy is
	// refused. Decrements source.stackCount and removes the source from its
	// container if it hits zero. Returns units actually placed.
	public int TrySplitMergeInBackpack(ItemState source, int amount, int targetIndex)
	{
		if (source == null || source.data == null || amount <= 0) { return 0; }
		if (targetIndex < 0 || targetIndex >= _backpack.Length) { return 0; }
		if (!source.data.IsStackable) { return 0; }
		ItemState target = _backpack[targetIndex];
		int moved = 0;
		if (target == null)
		{
			// Empty slot — place a fresh stack of up to `amount` units.
			int take = Math.Min(amount, source.stackCount);
			if (take <= 0) { return 0; }
			ItemState fresh = source.data.CreateState();
			fresh.stackCount = take;
			fresh.touched = source.touched;
			fresh.removeOnDay = source.removeOnDay;
			_backpack[targetIndex] = fresh;
			moved = take;
		}
		else
		{
			if (!target.CanStackWith(source)) { return 0; }
			int space = target.RemainingStackSpace();
			moved = Math.Min(space, Math.Min(amount, source.stackCount));
			if (moved <= 0) { return 0; }
			target.stackCount += moved;
		}
		source.stackCount -= moved;
		if (source.stackCount <= 0)
		{
			Remove(source);
		}
		else
		{
			onChanged?.Invoke();
		}
		return moved;
	}

	// Move an externally-sourced stack (e.g. from a chest) onto a SPECIFIC
	// backpack slot — the drag-onto-slot gesture. `incoming` is a fresh,
	// caller-owned stack:
	//   - empty target       -> placed directly (the `incoming` ref is stored)
	//   - same-kind stackable -> merged up to capacity (caller discards `incoming`)
	//   - different occupant   -> swapped, but only on a full-stack move so the
	//                            incoming stack isn't split; the displaced item is
	//                            returned via `displaced` for the caller to hand
	//                            back to the source container.
	// Returns units consumed from `incoming` (0 = the slot refused it; the caller
	// should fall back to a first-empty placement). `displaced` is non-null only
	// on a swap.
	public int TryAddExternalToBackpackSlot(ItemState incoming, bool fullMove, int index, out ItemState displaced)
	{
		displaced = null;
		if (incoming == null || incoming.data == null || incoming.stackCount <= 0)
		{
			return 0;
		}
		if (index < 0 || index >= _backpack.Length)
		{
			return 0;
		}
		MarkAcquired(incoming);
		ItemState target = _backpack[index];
		if (target == null)
		{
			_backpack[index] = incoming;
			onChanged?.Invoke();
			return incoming.stackCount;
		}
		if (incoming.data.IsStackable && target.CanStackWith(incoming))
		{
			int space = target.RemainingStackSpace();
			int moved = Math.Min(space, incoming.stackCount);
			if (moved <= 0)
			{
				return 0;
			}
			target.stackCount += moved;
			onChanged?.Invoke();
			return moved;
		}
		// Different occupant — only swap on a whole-stack move; a partial swap
		// would orphan the remainder of the incoming stack.
		if (!fullMove)
		{
			return 0;
		}
		// The displaced item leaves the inventory for the source container; forfeit
		// a weapon's loose arrows exactly as Remove() would (the one other place a
		// weapon exits the pack).
		if (target is WeaponState ws)
		{
			ws.DestroyOutstandingArrows();
		}
		displaced = target;
		_backpack[index] = incoming;
		onChanged?.Invoke();
		return incoming.stackCount;
	}

	// Pull an equipment item out of the hotbar and return it to the party
	// equipment stash (equipment can't go to the material backpack). Used by the
	// stash screen's unequip path.
	public bool TryRemoveFromConsumableSlot(ItemState item)
	{
		if (item == null)
		{
			return false;
		}
		for (int i = 0; i < _consumableSlots.Length; i++)
		{
			if (_consumableSlots[i] != item)
			{
				continue;
			}
			if (_activeConsumableIndex == i && item is ConsumableState cs)
			{
				cs.OnUnequipped(_owner);
			}
			_consumableSlots[i] = null;
			if (_activeConsumableIndex == i)
			{
				_activeConsumableIndex = -1;
				onActiveConsumableChanged?.Invoke(_activeConsumableIndex);
			}
			PushToEquipmentStash(item);
			onChanged?.Invoke();
			return true;
		}
		return false;
	}

	// Equip an externally-sourced equipment item (from the party equipment stash)
	// into hotbar slot `index`. Any current occupant is displaced to the stash.
	// The caller removes `item` from the stash
	// list on success. Returns false if the item isn't equipment or the index is
	// out of range.
	public bool TryEquipToConsumableSlot(ItemState item, int index)
	{
		if (item is not ConsumableState || index < 0 || index >= _consumableSlots.Length)
		{
			return false;
		}
		MarkAcquired(item);
		ItemState prev = _consumableSlots[index];
		if (prev == item)
		{
			return true;
		}
		bool prevWasActive = _activeConsumableIndex == index;
		if (prev != null)
		{
			if (prevWasActive && prev is ConsumableState prevCs)
			{
				prevCs.OnUnequipped(_owner);
			}
			PushToEquipmentStash(prev);
		}
		_consumableSlots[index] = item;
		if (_activeConsumableIndex == -1)
		{
			SetActiveConsumableIndex(index);
		}
		else if (prevWasActive && item is ConsumableState newCs)
		{
			// The active slot's occupant changed in place — fire the incoming
			// item's equip hook (SetActiveConsumableIndex no-ops on same index).
			newCs.OnEquipped(_owner);
		}
		onChanged?.Invoke();
		return true;
	}

	// Place an equipment item into the first empty hotbar slot without displacing
	// anything. Returns false if every slot is occupied. Used for starting-loadout
	// seeding and cooking-output delivery (equipment can't sit in the backpack).
	public bool TryAddEquipmentToHotbar(ItemState item)
	{
		if (item is not ConsumableState)
		{
			return false;
		}
		for (int i = 0; i < _consumableSlots.Length; i++)
		{
			if (_consumableSlots[i] == null)
			{
				return TryEquipToConsumableSlot(item, i);
			}
		}
		return false;
	}

	// Move the whole consumable belt (potions / food / torches) into another
	// inventory, preserving slot positions. Any item already on the destination
	// belt is displaced to the party equipment stash (the normal equip-over path).
	// Used when the player deliberately switches which party member they control
	// at a campfire, so the quick-use belt travels with them. Ephemeral items keep
	// their expiry. No-op onto self or a null destination.
	public void TransferBeltTo(Inventory dest)
	{
		if (dest == null || dest == this)
		{
			return;
		}
		bool moved = false;
		for (int i = 0; i < _consumableSlots.Length; i++)
		{
			ItemState item = _consumableSlots[i];
			if (item == null)
			{
				continue;
			}
			// Detach from this belt WITHOUT routing to the stash (it's re-placed on
			// the destination belt below). Unequip the active occupant so its hook
			// fires on the character losing it.
			if (_activeConsumableIndex == i && item is ConsumableState cs)
			{
				cs.OnUnequipped(_owner);
			}
			_consumableSlots[i] = null;
			// Same slot on the destination; its prior occupant (the incoming
			// character's own belt item) is displaced to the equipment stash. If the
			// destination can't take it (mismatched slot count), stash it rather than
			// orphan it.
			if (!dest.TryEquipToConsumableSlot(item, i))
			{
				PushToEquipmentStash(item);
			}
			moved = true;
		}
		if (moved)
		{
			_activeConsumableIndex = -1;
			onActiveConsumableChanged?.Invoke(_activeConsumableIndex);
			onChanged?.Invoke();
		}
	}

	// Directly select a consumable slot by index. Empty slots and out-of-range
	// indices are no-ops so a hotbar key bound past the configured slot count
	// just does nothing rather than wiping the active selection.
	public void SelectConsumable(int index)
	{
		if (index < 0 || index >= _consumableSlots.Length)
		{
			return;
		}
		if (_consumableSlots[index] == null)
		{
			return;
		}
		SetActiveConsumableIndex(index);
	}

	// Cycle active consumable left/right with wrapping. Skips empty slots.
	// No-op if no consumables exist at all.
	public void CycleConsumable(int direction)
	{
		if (_consumableSlots.Length == 0)
		{
			return;
		}
		if (direction == 0)
		{
			return;
		}

		int n = _consumableSlots.Length;
		int start = _activeConsumableIndex < 0 ? -1 : _activeConsumableIndex;
		for (int step = 1; step <= n; step++)
		{
			int candidate = ((start + direction * step) % n + n) % n;
			if (_consumableSlots[candidate] != null)
			{
				SetActiveConsumableIndex(candidate);
				return;
			}
		}
		// All slots empty — make sure we end up in -1.
		SetActiveConsumableIndex(-1);
	}

	private void SetActiveConsumableIndex(int newIndex)
	{
		if (newIndex == _activeConsumableIndex)
		{
			return;
		}
		ItemState prev = GetActiveConsumable();
		if (prev is ConsumableState prevCs)
		{
			prevCs.OnUnequipped(_owner);
		}
		_activeConsumableIndex = newIndex;
		ItemState next = GetActiveConsumable();
		if (next is ConsumableState nextCs)
		{
			nextCs.OnEquipped(_owner);
		}
		onActiveConsumableChanged?.Invoke(_activeConsumableIndex);
	}

	public bool IsEquipped(ItemState item)
	{
		return GetEquippedSlot(item).HasValue;
	}

	// True if `item` is owned anywhere — an equip/consumable slot OR the
	// backpack. Broader than IsEquipped: used for the arrow-pickup gate so a
	// bow stashed in the backpack still reclaims its arrows. Non-allocating
	// (safe to call from per-frame paths).
	public bool Contains(ItemState item)
	{
		if (item == null)
		{
			return false;
		}
		if (IsEquipped(item))
		{
			return true;
		}
		for (int i = 0; i < _backpack.Length; i++)
		{
			if (_backpack[i] == item)
			{
				return true;
			}
		}
		return false;
	}

	public EInventorySlot? GetEquippedSlot(ItemState item)
	{
		if (item == null)
		{
			return null;
		}
		if (item == _helmet) { return EInventorySlot.Helmet; }
		if (item == _armor) { return EInventorySlot.Armor; }
		if (item == _weaponMelee) { return EInventorySlot.WeaponMelee; }
		if (item == _weaponRanged) { return EInventorySlot.WeaponRanged; }
		if (item == _lantern) { return EInventorySlot.Lantern; }
		if (item == GetActiveConsumable()) { return EInventorySlot.Equipment; }
		return null;
	}

	public IEnumerable<ItemState> EnumerateAll()
	{
		for (int i = 0; i < _backpack.Length; i++)
		{
			if (_backpack[i] != null) { yield return _backpack[i]; }
		}
		for (int i = 0; i < _consumableSlots.Length; i++)
		{
			if (_consumableSlots[i] != null)
			{
				yield return _consumableSlots[i];
			}
		}
		if (_helmet != null) { yield return _helmet; }
		if (_armor != null) { yield return _armor; }
		// Weapons live in equip-slot pointers only; they're not duplicated in the
		// backpack list, so enumerate them here too.
		if (_weaponMelee != null) { yield return _weaponMelee; }
		if (_weaponRanged != null) { yield return _weaponRanged; }
		if (_lantern != null) { yield return _lantern; }
	}

	// Enumerate equipped armor as ArmorState (skips null slots). Used by
	// Player.TickWetEffect for the cascade contribution — only worn armor
	// transmits wetness back into the wearer's meter.
	public System.Collections.Generic.IEnumerable<ArmorState> EnumerateEquippedArmor()
	{
		if (_helmet is ArmorState head) { yield return head; }
		if (_armor is ArmorState body) { yield return body; }
	}

	// Every owned armor piece for wetness ticking (and any future per-item
	// upkeep). Armor now lives only in the two equip slots — the backpack is
	// materials-only — so this is just the worn set.
	public System.Collections.Generic.IEnumerable<ArmorState> EnumerateAllArmor()
	{
		if (_helmet is ArmorState head) { yield return head; }
		if (_armor is ArmorState body) { yield return body; }
	}

	// Remove every material from the backpack and return them, leaving it empty.
	// Used on camping to drain the controlled member's carried materials into the
	// party material stash. Fires a single onChanged pulse.
	public List<ItemState> DrainBackpack()
	{
		var drained = new List<ItemState>();
		bool any = false;
		for (int i = 0; i < _backpack.Length; i++)
		{
			if (_backpack[i] != null)
			{
				drained.Add(_backpack[i]);
				_backpack[i] = null;
				any = true;
			}
		}
		if (any)
		{
			onChanged?.Invoke();
		}
		return drained;
	}

	// Sparse view: Backpack[i] is the item at slot i, or null if empty. Count
	// is the array length (backpackCapacity), NOT the non-null occupancy —
	// use BackpackCount for that.
	public IReadOnlyList<ItemState> Backpack => _backpack;
	public IReadOnlyList<ItemState> ConsumableSlots => _consumableSlots;

	private void SetSlot(EInventorySlot slot, ItemState item)
	{
		// A weapon leaving an equip slot — unequipped to backpack, swapped out,
		// or removed while equipped (Remove routes through here) — destroys any
		// minions it summoned, so a summoner's minions never outlive the weapon
		// being put away. Fire only on a genuine occupant change so re-setting
		// the same item in place is a no-op. This is the single chokepoint all
		// equip-slot writes funnel through (TryEquip / TryUnequip / Remove /
		// TrySwapEquipSlots).
		if (GetEquipped(slot) is WeaponState outgoing && outgoing != item)
		{
			outgoing.DestroyMinions();
		}
		switch (slot)
		{
			case EInventorySlot.Helmet: _helmet = item; break;
			case EInventorySlot.Armor: _armor = item; break;
			case EInventorySlot.WeaponMelee: _weaponMelee = item as WeaponState; break;
			case EInventorySlot.WeaponRanged: _weaponRanged = item as WeaponState; break;
			case EInventorySlot.Lantern: _lantern = item; break;
			default: break;
		}
	}

	private void NotifySlot(EInventorySlot slot)
	{
		onSlotChanged?.Invoke(slot);
		onChanged?.Invoke();
	}
}
