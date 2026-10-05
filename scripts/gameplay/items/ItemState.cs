using System.Collections.Generic;
using System.IO;

public class ItemState
{
	public virtual ItemData data => _data;
	private readonly ItemData _data;

	// Spoil cohorts: this stack's units grouped by when they expire. One
	// ItemState shows as ONE inventory stack regardless of when its units were
	// acquired — perishables picked up at different times coexist as separate
	// cohorts here (a fresh batch merges with an older one instead of splitting the
	// inventory) and are always consumed oldest-first. spoilAtClock is a
	// WorldState.WorldClockDays deadline; 0 means "never spoils"; a kind either
	// spoils or it doesn't, so multiple cohorts only ever
	// arise for perishables. stackCount is the summed total: NEVER mutate the count
	// directly — go through the helpers below (SetCount / AddUnits / Consume /
	// TransferTo / SplitOff / PruneExpired) so the ledger stays the single source of
	// truth. That's why stackCount is read-only.
	private struct SpoilCohort
	{
		public int count;
		public double spoilAtClock;
	}
	private readonly List<SpoilCohort> _cohorts = new List<SpoilCohort>();

	public ulong cooldownExpireMs;
	public ulong cooldownDurationMs;

	// Power tier, composed onto the state at construction (ItemDescriptor.level),
	// NOT earned through use. 0 = base. WeaponState scales outgoing damage by
	// 2^level and ArmorState scales its armor points by 2^level; harmless (unused)
	// on other item kinds.
	public int level;

	// Set once this item has ever entered the player's inventory (picked up,
	// bought, cooked, withdrawn from a chest, starting gear — every Inventory
	// acquisition path stamps it). Travels with the object like `statusEffects`
	// — stays true after the item is dropped back into the world, so a
	// re-encountered drop reads as "already handled" rather than pristine. Split
	// stacks inherit it from their source. Never cleared.
	public bool touched;

	// Per-item status effects (wetness on a garment, a timed enchantment on
	// a sword, etc.). Lives on the item so it travels with the object: a wet
	// shirt unequipped into the backpack stays wet; an enchanted sword
	// dropped into a chest keeps the enchant. Constructed with null actor /
	// world / damage callback because items have no world position to spawn
	// fx at and no HP to chip — the controller's null-safe paths skip those
	// branches. Audiovisual cues for item-side effects ride on the wearer's
	// own status (e.g. wet armor cascading into the player's Wet meter, which
	// arms the player-side effect and surfaces the splash + loop fx there).
	public readonly StatusEffectController statusEffects = new StatusEffectController(null, null, null);

	// Menu of boons this specific item instance can bestow when used. Composed
	// onto the state at drop/creation time from the loot source (e.g. a fairy
	// corpse's possible boons) rather than baked into the shared ItemData, so
	// the set is per-instance and is narrowed to the one the player chooses. An
	// ApplyStatusEffect event with no fixed statusEffect applies one entry from
	// this list (see ApplyStatusEffect.Apply). Empty for ordinary items, whose
	// use-effects are authored directly on their events.
	public readonly List<BoonData> possibleBoons = new List<BoonData>();

	// Subclass runtime state, carried by EntitySerializer's item format after the
	// base fields (length-prefixed there, so a reader that can't rebuild the item
	// still skips it). Override in a subclass that has any.
	public virtual void WriteSubclassState(BinaryWriter w)
	{
	}

	public virtual void ReadSubclassState(BinaryReader r)
	{
	}

	public ItemState(ItemData d)
	{
		_data = d;
		_cohorts.Add(new SpoilCohort { count = 1, spoilAtClock = 0 });
	}

	// Total units across all spoil cohorts. Read-only — see the ledger note above.
	public int stackCount
	{
		get
		{
			int n = 0;
			for (int i = 0; i < _cohorts.Count; i++)
			{
				n += _cohorts[i].count;
			}
			return n;
		}
	}

	public bool IsSameKind(ItemState other)
	{
		return other != null && other.data == _data;
	}

	// Same-kind items always stack now: differing spoil deadlines coexist as
	// cohorts inside one stack, so a fresh batch merges with an older one (and is
	// consumed oldest-first) rather than occupying a separate inventory slot.
	public bool CanStackWith(ItemState other)
	{
		return IsSameKind(other);
	}

	public int RemainingStackSpace()
	{
		if (_data == null)
		{
			return 0;
		}
		return _data.maxStack - stackCount;
	}

	// The soonest real spoil deadline across cohorts (a WorldClockDays value), or
	// 0 if nothing here spoils.
	public double SoonestSpoilClock
	{
		get
		{
			double soonest = 0.0;
			for (int i = 0; i < _cohorts.Count; i++)
			{
				double at = _cohorts[i].spoilAtClock;
				if (at != 0.0 && (soonest == 0.0 || at < soonest))
				{
					soonest = at;
				}
			}
			return soonest;
		}
	}

	// Replace the whole ledger with `count` never-spoiling units. For fresh
	// construction and the spoil-agnostic staging UIs (merchant / cooking panels)
	// that set an absolute count; a perishable gets its real deadline later, on
	// acquisition, via StampSpoilClock. count <= 0 empties the stack.
	public void SetCount(int count)
	{
		_cohorts.Clear();
		if (count > 0)
		{
			_cohorts.Add(new SpoilCohort { count = count, spoilAtClock = 0 });
		}
	}

	// Add `count` units expiring at clock `spoilAtClock` (0 = never), merging into
	// the cohort with that exact deadline when one exists. The one way to grow a
	// stack while preserving spoil bookkeeping.
	public void AddUnits(int count, double spoilAtClock)
	{
		if (count <= 0)
		{
			return;
		}
		for (int i = 0; i < _cohorts.Count; i++)
		{
			if (_cohorts[i].spoilAtClock == spoilAtClock)
			{
				SpoilCohort c = _cohorts[i];
				c.count += count;
				_cohorts[i] = c;
				return;
			}
		}
		_cohorts.Add(new SpoilCohort { count = count, spoilAtClock = spoilAtClock });
	}

	// Stamp not-yet-dated units (spoilAtClock == 0) with deadline `spoilAtClock`
	// — called on acquisition so a fresh perishable pickup's spoil clock starts.
	// Already-dated cohorts (older units merged earlier) keep their own deadline.
	// No-op for a non-deadline (<= 0).
	public void StampSpoilClock(double spoilAtClock)
	{
		if (spoilAtClock <= 0.0)
		{
			return;
		}
		bool changed = false;
		for (int i = 0; i < _cohorts.Count; i++)
		{
			SpoilCohort c = _cohorts[i];
			if (c.spoilAtClock == 0.0)
			{
				c.spoilAtClock = spoilAtClock;
				_cohorts[i] = c;
				changed = true;
			}
		}
		if (changed)
		{
			Coalesce();
		}
	}

	// Consume up to `count` units oldest-first (soonest to spoil), discarding them.
	// Returns units actually removed. The canonical spend path — cooking, alchemy
	// reagents, and DecrementStack all funnel here, so the player always burns the
	// batch nearest spoiling first.
	public int Consume(int count)
	{
		if (count <= 0)
		{
			return 0;
		}
		int removed = 0;
		while (count > 0 && _cohorts.Count > 0)
		{
			int oldest = OldestIndex();
			SpoilCohort c = _cohorts[oldest];
			int take = System.Math.Min(count, c.count);
			c.count -= take;
			count -= take;
			removed += take;
			if (c.count <= 0)
			{
				_cohorts.RemoveAt(oldest);
			}
			else
			{
				_cohorts[oldest] = c;
			}
		}
		return removed;
	}

	// Move up to `maxUnits` oldest-first into `dest`, preserving each unit's spoil
	// deadline. Returns units moved. Replaces the paired `dest.stackCount += n;
	// src.stackCount -= n` merge used when folding one stack into another.
	public int TransferTo(ItemState dest, int maxUnits)
	{
		if (dest == null || maxUnits <= 0)
		{
			return 0;
		}
		int moved = 0;
		while (moved < maxUnits && _cohorts.Count > 0)
		{
			int oldest = OldestIndex();
			SpoilCohort c = _cohorts[oldest];
			int take = System.Math.Min(maxUnits - moved, c.count);
			dest.AddUnits(take, c.spoilAtClock);
			c.count -= take;
			moved += take;
			if (c.count <= 0)
			{
				_cohorts.RemoveAt(oldest);
			}
			else
			{
				_cohorts[oldest] = c;
			}
		}
		return moved;
	}

	// Carve `count` units oldest-first into a fresh same-kind ItemState (copying
	// touched / level and preserving spoil deadlines). The source shrinks by whatever
	// was available. Used by Drop and the backpack split gesture.
	public ItemState SplitOff(int count)
	{
		ItemState fresh = _data.CreateState();
		fresh.SetCount(0);
		fresh.touched = touched;
		fresh.level = level;
		TransferTo(fresh, count);
		return fresh;
	}

	// Drop every cohort whose spoil deadline has passed; returns units lost. The
	// stack survives until its LAST cohort expires, so a half-spoiled pile loses
	// only its old batch. `nowClock` is WorldState.WorldClockDays.
	public int PruneExpired(double nowClock)
	{
		int lost = 0;
		for (int i = _cohorts.Count - 1; i >= 0; i--)
		{
			double at = _cohorts[i].spoilAtClock;
			if (at != 0.0 && nowClock >= at)
			{
				lost += _cohorts[i].count;
				_cohorts.RemoveAt(i);
			}
		}
		return lost;
	}

	// --- Serialization hooks (EntitySerializer) ---
	// Cohorts ARE the persisted spoil state. Write CohortCount then each
	// (units, spoilAtClock); read back by ClearCohorts() + AddUnits per pair.
	public int CohortCount => _cohorts.Count;
	public void GetCohort(int i, out int count, out double spoilAtClock)
	{
		count = _cohorts[i].count;
		spoilAtClock = _cohorts[i].spoilAtClock;
	}
	public void ClearCohorts()
	{
		_cohorts.Clear();
	}

	// Index of the oldest cohort (soonest real deadline; never-spoil sorts last).
	private int OldestIndex()
	{
		int oldest = 0;
		for (int i = 1; i < _cohorts.Count; i++)
		{
			if (SpoilOrder(_cohorts[i].spoilAtClock) < SpoilOrder(_cohorts[oldest].spoilAtClock))
			{
				oldest = i;
			}
		}
		return oldest;
	}

	// Ordering key for oldest-first: soonest real deadline first, never-spoil (0)
	// last — so a never-spoiling unit is only consumed after all dated ones.
	private static double SpoilOrder(double spoilAtClock)
	{
		return spoilAtClock == 0.0 ? double.MaxValue : spoilAtClock;
	}

	// Merge cohorts that share a deadline into one entry — keeps the ledger
	// minimal after a stamp folds day-0 units into an existing dated cohort.
	private void Coalesce()
	{
		for (int i = _cohorts.Count - 1; i > 0; i--)
		{
			for (int j = 0; j < i; j++)
			{
				if (_cohorts[i].spoilAtClock == _cohorts[j].spoilAtClock)
				{
					SpoilCohort cj = _cohorts[j];
					cj.count += _cohorts[i].count;
					_cohorts[j] = cj;
					_cohorts.RemoveAt(i);
					break;
				}
			}
		}
	}
}
