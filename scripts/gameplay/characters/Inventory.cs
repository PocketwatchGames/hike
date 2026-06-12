using System;
using System.Collections.Generic;
using Godot;

// Single source of truth for everything the player owns. Equip slots and
// consumable hotbar slots hold REFERENCES into _items; they don't own their
// items separately. The invariant is: every ItemState the player owns is in
// _items exactly once. Equipping points a slot field at one of those items.
public class Inventory
{
	private readonly Player _owner;
	private readonly PlayerData _data;

	// Backpack: items the player owns that are not currently in an equip or
	// consumable slot. Fixed-size sparse array — `_backpack[i]` is the item
	// at grid slot i, or null if that slot is empty. The sparse layout means
	// the player can leave gaps when reorganizing (move an item to slot 5
	// even if slot 3 is empty), which the prior List<ItemState> couldn't
	// express without auto-compacting. BackpackCount is the non-null count;
	// Backpack.Count is the array length (== backpackCapacity).
	private readonly ItemState[] _backpack;

	// Equip slot pointers — null means empty. Each pointer is also present in
	// _items via _backpack OR via _consumableSlots (for consumables) when
	// equipped. To enumerate every item the player owns, use EnumerateAll().
	private ItemState _armorHead;
	private ItemState _armorBody;
	private WeaponState _weaponLeft;
	private WeaponState _weaponRight;

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
	public int TryAdd(ItemState item)
	{
		if (item == null || item.data == null || item.stackCount <= 0)
		{
			return 0;
		}

		// Entering the inventory marks the item as touched for the rest of its
		// life (see ItemState.touched). Stamp before the merge so even units
		// that fold into an existing stack count as handled.
		item.touched = true;

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
				if (!existing.IsSameKind(item))
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
			EInventorySlot.ArmorHead => _armorHead,
			EInventorySlot.ArmorBody => _armorBody,
			EInventorySlot.WeaponLeft => _weaponLeft,
			EInventorySlot.WeaponRight => _weaponRight,
			EInventorySlot.Consumable => GetActiveConsumable(),
			_ => null,
		};
	}

	public WeaponState GetWeapon(EInventorySlot slot)
	{
		return slot switch
		{
			EInventorySlot.WeaponLeft => _weaponLeft,
			EInventorySlot.WeaponRight => _weaponRight,
			_ => null,
		};
	}

	// Equip an item that is already in the inventory into the given slot. If
	// the slot is occupied, the previous item swaps back to the backpack —
	// refused outright if the swap won't fit (no silent drop). Caller must
	// ensure the item kind matches the slot (a sword goes in a weapon slot,
	// armor in an armor slot — slot validation is up to the call site).
	public bool TryEquip(ItemState item, EInventorySlot slot)
	{
		if (item == null)
		{
			return false;
		}

		// Equipping is an entry path too — Loot grants an obvious upgrade
		// straight into an empty slot, bypassing TryAdd. Mark it touched.
		item.touched = true;

		ItemState prev = GetEquipped(slot);
		if (prev == item)
		{
			return true;
		}

		// If prev exists, it must fit somewhere when we displace it. Today
		// "somewhere" is the backpack — capacity check. The item being
		// equipped frees a backpack slot when it leaves the backpack, so the
		// net swap is feasible iff prev fits given the post-swap state.
		int sourceBackpackIndex = IndexOfInBackpack(item);
		bool itemInBackpack = sourceBackpackIndex >= 0;
		int postSwapBackpackCount = BackpackCount + (itemInBackpack ? -1 : 0) + (prev != null ? 1 : 0);
		if (postSwapBackpackCount > _data.backpackCapacity)
		{
			return false;
		}

		if (itemInBackpack)
		{
			_backpack[sourceBackpackIndex] = null;
		}

		if (prev != null)
		{
			// Land the displaced item in the slot the equipped item vacated
			// when possible so the player's grid layout stays stable across
			// a swap. If the equipped item didn't come from the backpack,
			// fall back to the first empty slot.
			if (sourceBackpackIndex >= 0)
			{
				_backpack[sourceBackpackIndex] = prev;
			}
			else
			{
				AppendToBackpack(prev);
			}
		}

		SetSlot(slot, item);
		NotifySlot(slot);
		return true;
	}

	public bool TryUnequip(EInventorySlot slot)
	{
		ItemState prev = GetEquipped(slot);
		if (prev == null)
		{
			return true;
		}
		if (!AppendToBackpack(prev))
		{
			return false;
		}
		SetSlot(slot, null);
		NotifySlot(slot);
		return true;
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
		if (a == EInventorySlot.Consumable || b == EInventorySlot.Consumable) { return false; }
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
		if (item == null || _owner == null)
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
		fresh.touched = true;
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
		if (existing.IsSameKind(fresh) && fresh.data.IsStackable)
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
			_backpack[targetIndex] = fresh;
			moved = take;
		}
		else
		{
			if (!target.IsSameKind(source)) { return 0; }
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
		incoming.touched = true;
		ItemState target = _backpack[index];
		if (target == null)
		{
			_backpack[index] = incoming;
			onChanged?.Invoke();
			return incoming.stackCount;
		}
		if (incoming.data.IsStackable && target.IsSameKind(incoming))
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

	// Move a consumable out of the hotbar back into the backpack. Mirror of
	// TryMoveToConsumableSlot — used by the inventory screen's Unequip path
	// for items that live in inactive hotbar slots (GetEquippedSlot only
	// reports the active slot). Refused outright if the backpack is full —
	// no silent drop.
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
			int destIdx = FindFirstEmptyBackpackIndex();
			if (destIdx < 0)
			{
				return false;
			}
			if (_activeConsumableIndex == i && item is ConsumableState cs)
			{
				cs.OnUnequipped(_owner);
			}
			_consumableSlots[i] = null;
			_backpack[destIdx] = item;
			if (_activeConsumableIndex == i)
			{
				_activeConsumableIndex = -1;
				onActiveConsumableChanged?.Invoke(_activeConsumableIndex);
			}
			onChanged?.Invoke();
			return true;
		}
		return false;
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
		if (item == _armorHead) { return EInventorySlot.ArmorHead; }
		if (item == _armorBody) { return EInventorySlot.ArmorBody; }
		if (item == _weaponLeft) { return EInventorySlot.WeaponLeft; }
		if (item == _weaponRight) { return EInventorySlot.WeaponRight; }
		if (item == GetActiveConsumable()) { return EInventorySlot.Consumable; }
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
		if (_armorHead != null) { yield return _armorHead; }
		if (_armorBody != null) { yield return _armorBody; }
		// Weapons live in equip-slot pointers only; they're not duplicated in the
		// backpack list, so enumerate them here too.
		if (_weaponLeft != null) { yield return _weaponLeft; }
		if (_weaponRight != null) { yield return _weaponRight; }
	}

	// Enumerate equipped armor as ArmorState (skips null slots). Used by
	// Player.TickWetEffect for the cascade contribution — only worn armor
	// transmits wetness back into the wearer's meter.
	public System.Collections.Generic.IEnumerable<ArmorState> EnumerateEquippedArmor()
	{
		if (_armorHead is ArmorState head) { yield return head; }
		if (_armorBody is ArmorState body) { yield return body; }
	}

	// Enumerate every owned armor — equipped slots plus any ArmorState held
	// in the backpack — so wetness ticking (and any future per-item upkeep)
	// applies whether the player is wearing the piece or stuffing it in the
	// pack. A wet shirt rolled up in the backpack still dries.
	public System.Collections.Generic.IEnumerable<ArmorState> EnumerateAllArmor()
	{
		if (_armorHead is ArmorState head) { yield return head; }
		if (_armorBody is ArmorState body) { yield return body; }
		for (int i = 0; i < _backpack.Length; i++)
		{
			if (_backpack[i] is ArmorState packed) { yield return packed; }
		}
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
			case EInventorySlot.ArmorHead: _armorHead = item; break;
			case EInventorySlot.ArmorBody: _armorBody = item; break;
			case EInventorySlot.WeaponLeft: _weaponLeft = item as WeaponState; break;
			case EInventorySlot.WeaponRight: _weaponRight = item as WeaponState; break;
			default: break;
		}
	}

	private void NotifySlot(EInventorySlot slot)
	{
		onSlotChanged?.Invoke(slot);
		onChanged?.Invoke();
	}
}
