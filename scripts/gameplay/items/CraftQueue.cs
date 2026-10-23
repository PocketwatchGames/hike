using System.Collections.Generic;

// One job on a crafting bench's queue: an item being made, or a piece of gear
// being repaired. Its cost was paid when it was queued — `paid` is what came out,
// refunded if the job is removed — and a repair holds the gear on the bench until
// it is done, so nothing can wear it or change its price meanwhile.
public class CraftJob
{
	// A craft: the item made and how many. Null for a repair.
	public ItemData made;
	public int count;
	// A repair: the piece on the bench.
	public GearState gear;
	// In-world hours the bench spends on it, fixed when queued.
	public double hours;
	public readonly List<SpentItem> paid = new();

	public bool IsRepair => gear != null;

	// What the job makes, as the queue and the info panel show it.
	public ItemState Preview()
	{
		if (IsRepair)
		{
			return gear;
		}
		ItemState state = made?.CreateState();
		state?.SetCount(made.IsStackable ? count : 1);
		return state;
	}

	// What the job hands over when it finishes: the repaired gear, or `count` of
	// the item — one stack when it stacks, one state per unit otherwise.
	public List<ItemState> Outputs(double clock)
	{
		var outputs = new List<ItemState>();
		if (IsRepair)
		{
			outputs.Add(gear);
			return outputs;
		}
		if (made == null || count <= 0)
		{
			return outputs;
		}
		int states = made.IsStackable ? 1 : count;
		for (int i = 0; i < states; i++)
		{
			ItemState state = made.CreateState();
			state.SetCount(made.IsStackable ? count : 1);
			outputs.Add(Fresh(state, clock));
		}
		return outputs;
	}

	// What removing the job gives back: everything it was paid with, and the gear
	// it was repairing.
	public List<ItemState> Refund(double clock)
	{
		var refund = new List<ItemState>();
		for (int i = 0; i < paid.Count; i++)
		{
			ItemData data = paid[i].item;
			int left = paid[i].count;
			int perState = data.IsStackable ? System.Math.Max(1, data.maxStack) : 1;
			while (left > 0)
			{
				ItemState state = data.CreateState();
				state.SetCount(System.Math.Min(left, perState));
				left -= state.stackCount;
				refund.Add(Fresh(state, clock));
			}
		}
		if (IsRepair)
		{
			refund.Add(gear);
		}
		return refund;
	}

	// A perishable made or handed back now starts its spoil clock now.
	static ItemState Fresh(ItemState state, double clock)
	{
		if (state.data.spoilDays > 0)
		{
			state.StampSpoilClock(clock + state.data.spoilDays);
		}
		return state;
	}
}

// A crafting bench's work, done in order. Only the head job is in progress, and
// it runs on the in-world clock whether or not anyone is at the bench: it is done
// at HeadStartedAtClock + its hours, and Settle hands it to the party stash. A
// finished job the stash has no room for waits at the head and holds up the rest.
public class CraftQueue
{
	public const int Capacity = 8;

	readonly List<CraftJob> _jobs = new();

	// The WorldClockDays the head job started at.
	public double HeadStartedAtClock;
	// The head finished while the stash was full. The next job then starts when
	// this one is finally delivered, not when it finished.
	public bool HeadBlocked;

	public IReadOnlyList<CraftJob> Jobs => _jobs;
	public int Count => _jobs.Count;
	public bool IsEmpty => _jobs.Count == 0;
	public bool IsFull => _jobs.Count >= Capacity;

	public double HeadDoneAtClock => IsEmpty ? 0.0 : HeadStartedAtClock + _jobs[0].hours / Sim.HoursPerDay;

	// The head job's progress at `clock`, 0..1.
	public float HeadProgress(double clock)
	{
		if (IsEmpty)
		{
			return 0f;
		}
		double days = _jobs[0].hours / Sim.HoursPerDay;
		if (days <= 0.0)
		{
			return 1f;
		}
		return (float)System.Math.Clamp((clock - HeadStartedAtClock) / days, 0.0, 1.0);
	}

	public bool Enqueue(CraftJob job, double clock)
	{
		if (job == null || IsFull)
		{
			return false;
		}
		if (IsEmpty)
		{
			HeadStartedAtClock = clock;
			HeadBlocked = false;
		}
		_jobs.Add(job);
		return true;
	}

	// For the serializer, which restores the head's timing itself.
	public void Restore(CraftJob job)
	{
		_jobs.Add(job);
	}

	// Remove job `index`, refunding it into `stash`. Refused, changing nothing,
	// when the stash can't take all of it. Removing the head starts the next job
	// now.
	public bool Cancel(int index, double clock, ItemGrid stash)
	{
		if (index < 0 || index >= _jobs.Count || stash == null)
		{
			return false;
		}
		List<ItemState> refund = _jobs[index].Refund(clock);
		if (!stash.CanFullyAddAll(refund))
		{
			return false;
		}
		for (int i = 0; i < refund.Count; i++)
		{
			stash.Add(refund[i]);
		}
		_jobs.RemoveAt(index);
		if (index == 0)
		{
			HeadStartedAtClock = clock;
			HeadBlocked = false;
		}
		stash.NotifyChanged();
		return true;
	}

	// Hand every job finished by `clock` to `stash`, in order, appending each to
	// `delivered`. Stops at the first one the stash can't take whole.
	public void Settle(double clock, ItemGrid stash, List<CraftJob> delivered)
	{
		if (stash == null)
		{
			return;
		}
		bool changed = false;
		while (!IsEmpty && clock >= HeadDoneAtClock)
		{
			CraftJob head = _jobs[0];
			List<ItemState> outputs = head.Outputs(clock);
			if (!stash.CanFullyAddAll(outputs))
			{
				HeadBlocked = true;
				break;
			}
			head.gear?.Repair();
			for (int i = 0; i < outputs.Count; i++)
			{
				stash.Add(outputs[i]);
			}
			double finishedAt = HeadDoneAtClock;
			_jobs.RemoveAt(0);
			HeadStartedAtClock = HeadBlocked ? clock : finishedAt;
			HeadBlocked = false;
			delivered?.Add(head);
			changed = true;
		}
		if (changed)
		{
			stash.NotifyChanged();
		}
	}
}
