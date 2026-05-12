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
	// consumable slot. Capped at PlayerData.backpackCapacity.
	private readonly List<ItemState> _backpack = new();

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
	public int BackpackCount => _backpack.Count;
	public int ConsumableSlotCount => _consumableSlots.Length;
	public int ActiveConsumableIndex => _activeConsumableIndex;

	public Inventory(Player owner, PlayerData data)
	{
		_owner = owner;
		_data = data;
		_consumableSlots = new ItemState[Math.Max(0, data.consumableSlotCount)];
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

		// Anything left lands in the backpack as a new entry, capacity permitting.
		if (item.stackCount > 0 && _backpack.Count < _data.backpackCapacity)
		{
			_backpack.Add(item);
			int added = item.stackCount;
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

		_backpack.Remove(item);
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

		ItemState prev = GetEquipped(slot);
		if (prev == item)
		{
			return true;
		}

		// If prev exists, it must fit somewhere when we displace it. Today
		// "somewhere" is the backpack — capacity check. The item being
		// equipped frees a backpack slot when it leaves the backpack, so the
		// net swap is feasible iff prev fits given the post-swap state.
		bool itemInBackpack = _backpack.Contains(item);
		int postSwapBackpackCount = _backpack.Count + (itemInBackpack ? -1 : 0) + (prev != null ? 1 : 0);
		if (postSwapBackpackCount > _data.backpackCapacity)
		{
			return false;
		}

		if (itemInBackpack)
		{
			_backpack.Remove(item);
		}

		if (prev != null)
		{
			_backpack.Add(prev);
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
		if (_backpack.Count >= _data.backpackCapacity)
		{
			return false;
		}
		SetSlot(slot, null);
		_backpack.Add(prev);
		NotifySlot(slot);
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
		}
		else
		{
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
		if (!_backpack.Contains(item))
		{
			return false;
		}
		for (int i = 0; i < _consumableSlots.Length; i++)
		{
			if (_consumableSlots[i] == null)
			{
				_backpack.Remove(item);
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
			if (_backpack.Count >= _data.backpackCapacity)
			{
				return false;
			}
			if (_activeConsumableIndex == i && item is ConsumableState cs)
			{
				cs.OnUnequipped(_owner);
			}
			_consumableSlots[i] = null;
			_backpack.Add(item);
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
		foreach (ItemState s in _backpack)
		{
			yield return s;
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

	public IReadOnlyList<ItemState> Backpack => _backpack;
	public IReadOnlyList<ItemState> ConsumableSlots => _consumableSlots;

	private void SetSlot(EInventorySlot slot, ItemState item)
	{
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
