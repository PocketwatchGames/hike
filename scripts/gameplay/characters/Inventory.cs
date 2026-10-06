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
public class Inventory
{
	private readonly Player _owner;
	private readonly PlayerData _data;

	// `_backpack[i]` is the item at grid slot i, or null — the player can leave
	// gaps when reorganizing. Every owned item is in here exactly once.
	private readonly ItemState[] _backpack;

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

	public int BackpackCapacity => _backpack.Length;
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

	// Sparse view: Backpack[i] is the item at slot i, or null if empty. Count
	// is the array length (backpackCapacity), NOT the non-null occupancy —
	// use BackpackCount for that.
	public IReadOnlyList<ItemState> Backpack => _backpack;

	public Inventory(Player owner, PlayerData data)
	{
		_owner = owner;
		_data = data;
		_backpack = new ItemState[Math.Max(0, data.backpackCapacity)];
	}

	private void Changed()
	{
		ReconcileHotbarSelection();
		onChanged?.Invoke();
	}

	// The first empty slot at or after `start`, wrapping round to the front.
	private int FindFirstEmptyBackpackIndex(int start)
	{
		for (int k = 0; k < _backpack.Length; k++)
		{
			int i = (start + k) % _backpack.Length;
			if (_backpack[i] == null) { return i; }
		}
		return -1;
	}

	public int IndexOfInBackpack(ItemState item)
	{
		if (item == null) { return -1; }
		for (int i = 0; i < _backpack.Length; i++)
		{
			if (_backpack[i] == item) { return i; }
		}
		return -1;
	}

	// True if `item` is owned — i.e. in the backpack, equipped or not.
	// Non-allocating (safe to call from per-frame paths).
	public bool Contains(ItemState item) => IndexOfInBackpack(item) >= 0;

	// A material lands in the first free slot PAST the hotbar, so loot doesn't
	// crowd out what the player uses in the field; only a full pack spills it
	// into the hotbar. Anything else takes the first free slot.
	private bool AppendToBackpack(ItemState item)
	{
		int idx = item?.data != null && item.data.IsMaterial
			? FindFirstEmptyBackpackIndex(HotbarSize)
			: FindFirstEmptyBackpackIndex(0);
		if (idx < 0) { return false; }
		_backpack[idx] = item;
		return true;
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
		for (int i = 0; i < _backpack.Length && spent < count; i++)
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
	private List<ItemState> PartyStash => _owner?.Sim?.WorldState?.SimState?.PartyStash;

	// Hand an item the player owns but can't carry (a starting loadout that
	// overflows the backpack) to the party stash. A weapon forfeits its
	// outstanding arrows on the way out, mirroring Remove().
	public void PushToStash(ItemState item)
	{
		if (item == null)
		{
			return;
		}
		if (item is WeaponState ws)
		{
			ws.DestroyOutstandingArrows();
		}
		ItemStash.Add(PartyStash, item);
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

		if (item.data.IsStackable)
		{
			// TransferTo folds the incoming units into each existing stack
			// oldest-first, preserving their (already-stamped) spoil days as cohorts.
			for (int i = 0; i < _backpack.Length && item.stackCount > 0; i++)
			{
				ItemState existing = _backpack[i];
				if (existing == null || !existing.CanStackWith(item))
				{
					continue;
				}
				int space = existing.RemainingStackSpace();
				if (space > 0)
				{
					item.TransferTo(existing, space);
				}
			}
		}

		// Anything left lands in the first empty slot — the ref is stored carrying
		// its remaining cohorts, so merged + appended == initialStack.
		if (item.stackCount > 0 && AppendToBackpack(item))
		{
			Changed();
			return initialStack;
		}

		int totalAdded = initialStack - item.stackCount;
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
		if (data == null || !data.IsCarriable || count <= 0)
		{
			return false;
		}
		int remaining = count;
		int emptySlots = 0;
		for (int i = 0; i < _backpack.Length; i++)
		{
			ItemState existing = _backpack[i];
			if (existing == null)
			{
				emptySlots++;
			}
			else if (data.IsStackable && existing.data == data)
			{
				remaining -= existing.RemainingStackSpace();
			}
		}
		if (remaining <= 0)
		{
			return true;
		}
		int perSlot = data.IsStackable ? Math.Max(1, data.maxStack) : 1;
		return remaining <= emptySlots * perSlot;
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

		// The single chokepoint for "weapon leaves the inventory": destroy the
		// arrows it left lying around, no refund. The recharge timer keeps its
		// deadline and refills the magazine if the weapon is ever re-acquired.
		// Equip / unequip / slot moves don't route through here, so an unequipped
		// bow keeps its arrows.
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

		_backpack[idx] = null;
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

	// Equip a backpack item into its category's slot, unequipping (not removing)
	// whatever held it. False for an item that isn't owned or isn't equippable.
	public bool Equip(ItemState item)
	{
		EInventorySlot slot = item?.data?.EquipSlotKind ?? EInventorySlot.None;
		if (slot == EInventorySlot.None || !Contains(item))
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

		ItemState dropped;
		if (stackCount.HasValue && item.data.IsStackable && stackCount.Value < item.stackCount)
		{
			// SplitOff carves the dropped units oldest-first and carries their real
			// spoil cohorts (plus touched/level) — dropping half a stack neither
			// resets nor starts a clock.
			dropped = item.SplitOff(Math.Max(1, stackCount.Value));
			Changed();
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
		_owner.Sim?.DropItem(dropped, pos, impulse, requireInteract: true);
	}

	// ---- Hotbar --------------------------------------------------------------

	// How many leading backpack slots the hotbar spans.
	public int HotbarSize => Math.Min(Math.Max(0, _data.hotbarSize), _backpack.Length);

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

	// Swap two backpack slots. The slots may be empty (null) — moving an item
	// from slot A to empty slot B leaves A empty and B holding the item, which is
	// how the player reorganizes their grid. Equipped items stay equipped.
	public bool TrySwapInBackpack(int sourceIndex, int targetIndex)
	{
		if (sourceIndex < 0 || sourceIndex >= _backpack.Length) { return false; }
		if (targetIndex < 0 || targetIndex >= _backpack.Length) { return false; }
		if (sourceIndex == targetIndex) { return true; }
		(_backpack[sourceIndex], _backpack[targetIndex]) = (_backpack[targetIndex], _backpack[sourceIndex]);
		Changed();
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
			// Empty slot — carve off a fresh stack of up to `amount` units
			// (SplitOff shrinks the source and carries its spoil cohorts).
			int take = Math.Min(amount, source.stackCount);
			if (take <= 0) { return 0; }
			_backpack[targetIndex] = source.SplitOff(take);
			moved = take;
		}
		else
		{
			if (!target.CanStackWith(source)) { return 0; }
			int space = target.RemainingStackSpace();
			moved = source.TransferTo(target, Math.Min(space, amount));
			if (moved <= 0) { return 0; }
		}
		if (source.stackCount <= 0)
		{
			Remove(source);
		}
		else
		{
			Changed();
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
		if (incoming == null || incoming.data == null || incoming.stackCount <= 0 || !incoming.data.IsCarriable)
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
			Changed();
			return incoming.stackCount;
		}
		if (incoming.data.IsStackable && target.CanStackWith(incoming))
		{
			int space = target.RemainingStackSpace();
			int moved = incoming.TransferTo(target, space);
			if (moved <= 0)
			{
				return 0;
			}
			Changed();
			return moved;
		}
		// Different occupant — only swap on a whole-stack move; a partial swap
		// would orphan the remainder of the incoming stack.
		if (!fullMove)
		{
			return 0;
		}
		// The displaced item leaves the inventory: unequip it and forfeit a
		// weapon's loose arrows exactly as Remove() would.
		EInventorySlot? displacedSlot = GetEquippedSlot(target);
		if (displacedSlot.HasValue)
		{
			SetSlot(displacedSlot.Value, null);
			onSlotChanged?.Invoke(displacedSlot.Value);
		}
		if (target is WeaponState ws)
		{
			ws.DestroyOutstandingArrows();
		}
		displaced = target;
		_backpack[index] = incoming;
		Changed();
		return incoming.stackCount;
	}

	// ---- Enumeration -----------------------------------------------------------

	// Every owned item.
	public IEnumerable<ItemState> EnumerateAll()
	{
		for (int i = 0; i < _backpack.Length; i++)
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
		for (int i = 0; i < _backpack.Length; i++)
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
		w.Write(_backpack.Length);
		for (int i = 0; i < _backpack.Length; i++)
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
		Array.Clear(_backpack);
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
			if (i < _backpack.Length)
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
			if (!AppendToBackpack(item))
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
		if (outgoing != null && outgoing != item)
		{
			// A summoner's minions never outlive the weapon being put away.
			if (outgoing is WeaponState weapon)
			{
				weapon.DestroyMinions();
			}
			// Only the equipped lantern lights the player, so an unequipped one
			// goes out rather than burning fuel in the pack.
			if (outgoing is LanternState lantern)
			{
				lantern.isActive = false;
			}
		}
		_equipped[(int)slot] = item;
	}

	private void NotifySlot(EInventorySlot slot)
	{
		onSlotChanged?.Invoke(slot);
		Changed();
	}
}
