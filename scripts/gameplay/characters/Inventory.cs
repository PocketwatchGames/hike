using System;
using System.Collections.Generic;
using Godot;

// Everything the player carries: the singular equip slots (helmet / armor /
// melee / ranged weapon / lantern), the single attuned alchemy-spell slot, and
// the material-only backpack. The backpack holds
// ONLY materials; weapons / armor / equipment never enter it. Displacing a piece
// out of an equip slot (equip-over) sends it to the world-scope party equipment
// stash (SimState.PartyEquipmentStash), not back to the backpack. Weapons /
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

	// Equip slot pointers — null means empty. To enumerate every item the player
	// owns, use EnumerateAll(). (The attuned spell's cast instance is separate —
	// see _castInstance below — and is not an owned item.)
	private ItemState _helmet;
	private ItemState _armor;
	private WeaponState _weaponMelee;
	private WeaponState _weaponRanged;
	// The player's lantern — its own permanent slot, seeded at spawn and toggled
	// on/off with the Lantern input. Never enters the Equipment hotbar (lanterns
	// are refused there); enumerated in EnumerateAll so its fuel ticks / refuels
	// and its lit state lights the player like any carried torch.
	private ItemState _lantern;

	// The single attuned alchemy spell (slot identity) and its persistent cast
	// instance — a ConsumableState built from the spell via CreateState(), rebuilt
	// only when the attunement changes so toggle state (SummonedPet / isActive)
	// survives across casts. Null when nothing is attuned. The spell's "ammo" is
	// not a stackCount — it's how many casts the party reagent pool currently
	// affords (Player.GetSpellAmmo).
	private SpellData _attunedSpell;
	private ConsumableState _castInstance;

	public Action<EInventorySlot> onSlotChanged;
	public Action onConsumableChanged;
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
	// The alchemy spell currently attuned to the single consumable slot, or null.
	public SpellData AttunedSpell => _attunedSpell;

	public Inventory(Player owner, PlayerData data)
	{
		_owner = owner;
		_data = data;
		_backpack = new ItemState[Math.Max(0, data.backpackCapacity)];
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
			if (s?.data == null || s.stackCount <= 0 || !Cooking.Satisfies(s.data, reagentItem))
			{
				continue;
			}
			int take = Math.Min(count - spent, s.stackCount);
			s.stackCount -= take;
			spent += take;
			if (s.stackCount <= 0)
			{
				_backpack[i] = null;
			}
		}
		if (spent > 0)
		{
			onChanged?.Invoke();
		}
		return spent;
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
	private List<ItemState> PartyEquipmentStash => _owner?.Sim?.WorldState?.SimState?.PartyEquipmentStash;

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
			item.removeOnDay = (_owner?.Sim?.DayNumber ?? 0) + item.data.spoilDays;
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
		int freshRemoveDay = data.spoilDays > 0 ? (_owner?.Sim?.DayNumber ?? 0) + data.spoilDays : 0;
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
	// stash. The Equipment "slot" is the attuned spell — use AttuneSpell for it.
	// The caller owns the
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
	// replaced. The Equipment "slot" is the attuned spell; clearing it unattunes.
	public bool TryUnequip(EInventorySlot slot)
	{
		if (slot != EInventorySlot.Equipment)
		{
			return false;
		}
		ClearAttunement();
		return true;
	}

	// Swap items between two equip slots (e.g., WeaponLeft ↔ WeaponRight).
	// Either side may be empty. The Equipment (attuned-spell) slot is NOT
	// supported here — use AttuneSpell. Caller is responsible for
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
		_owner.Sim?.DropItem(dropped, pos, impulse, requireInteract: true);
		onChanged?.Invoke();
	}

	// The runtime cast instance of the attuned spell (the runner's primaryItem /
	// the Equipment "slot" occupant), or null when nothing is attuned. Named
	// GetActiveConsumable for continuity with the equip-slot read path.
	public ItemState GetActiveConsumable()
	{
		return _castInstance;
	}

	// Attune `spell` to the single consumable slot: build its persistent cast
	// instance (a ConsumableState via CreateState) and fire the equip hook. Passing
	// null (or ClearAttunement) unattunes and fires the outgoing spell's unequip
	// hook. Re-attuning the same spell rebuilds the instance, dropping any live
	// toggle state (e.g. desummons a pet) — call only on a genuine change.
	public void AttuneSpell(SpellData spell)
	{
		if (_castInstance is ConsumableState prevCs)
		{
			prevCs.OnUnequipped(_owner);
		}
		_attunedSpell = spell;
		_castInstance = spell?.CreateState() as ConsumableState;
		if (_castInstance != null)
		{
			_castInstance.OnEquipped(_owner);
		}
		onConsumableChanged?.Invoke();
		onChanged?.Invoke();
	}

	// Clear the attuned spell (empty the consumable slot).
	public void ClearAttunement()
	{
		if (_attunedSpell == null && _castInstance == null)
		{
			return;
		}
		AttuneSpell(null);
	}

	// Carry the attuned spell to another inventory (a deliberate campfire character
	// switch), so the quick-cast slot travels with control. The destination rebuilds
	// its own cast instance from the spell; this inventory is left unattuned. No-op
	// onto self or a null destination.
	public void TransferAttunementTo(Inventory dest)
	{
		if (dest == null || dest == this || _attunedSpell == null)
		{
			return;
		}
		SpellData spell = _attunedSpell;
		ClearAttunement();
		dest.AttuneSpell(spell);
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
		// The attuned spell's cast instance is a synthetic execution vehicle, not
		// an owned/stackable item, so it is deliberately NOT enumerated here (no
		// spoilage, wetness, or arrow-reclaim applies to it).
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
