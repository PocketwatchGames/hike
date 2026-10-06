using System.Collections.Generic;
using System.IO;
using Godot;

// The player's roster of characters. One member is "active" (the character the
// player currently controls, driving the single controlled Player node); the
// rest are inactive party members that spawn around camp. Lives on
// SimState (worldState.SimState.Party), a sibling of the party stashes, so
// it's world-scope run state that SaveGame will persist.
//
// Members are runtime PlayerState instances — built once at game start by
// cloning the authored WorldStartData.startingParty templates (see
// Party.FromTemplates), so mutating a member's state never touches the .tres.
public class Party
{
	readonly List<PlayerState> _members = new();
	int _activeIndex;

	// The permanent, party-shared knowledge pool (identified items, discovered
	// recipes/species, learned languages). The active member accrues field
	// knowledge into their own PlayerState.Knowledge; BankActive folds it in here
	// when the player camps. Reads combine this with the active member's store.
	public readonly Knowledge Knowledge = new();

	// The party's map. Written directly by whoever is exploring — never banked.
	public readonly MapChart Chart = new();

	public IReadOnlyList<PlayerState> Members => _members;
	public int Count => _members.Count;
	public int ActiveIndex => _activeIndex;

	// The currently-controlled member, or null on an empty roster.
	public PlayerState Active =>
		_activeIndex >= 0 && _activeIndex < _members.Count ? _members[_activeIndex] : null;

	public PlayerState this[int index] =>
		index >= 0 && index < _members.Count ? _members[index] : null;

	// Build a runtime party by DEEP-cloning each authored template so the live
	// roster is independent of the .tres (a member's vitals / inventory evolve
	// per-run). Null / empty templates are skipped; a party with no valid member
	// yields an empty roster (the caller falls back to a default character).
	public static Party FromTemplates(IEnumerable<PlayerState> templates)
	{
		var party = new Party();
		if (templates != null)
		{
			foreach (PlayerState template in templates)
			{
				if (template == null) { continue; }
				party._members.Add(PlayerState.FromTemplate(template));
			}
		}
		return party;
	}

	// Inside a shared EntitySerializer table (SaveGame). The leader pick is not
	// written: a save is a sunrise wake, and a sunrise always resets it.
	public void Serialize(BinaryWriter w)
	{
		w.Write(_members.Count);
		for (int i = 0; i < _members.Count; i++)
		{
			_members[i].WriteRuntime(w);
		}
		w.Write(_activeIndex);
		Knowledge.Serialize(w);
		Chart.Serialize(w);
	}

	// A member whose template no longer exists is dropped (with a warning), and
	// the active index follows the member it named. `savedOrder` is the roster as
	// written, a null where a member was dropped, for sections keyed by it.
	public static Party Read(BinaryReader r, out List<PlayerState> savedOrder)
	{
		var party = new Party();
		int count = r.ReadInt32();
		var loaded = new List<PlayerState>(count);
		for (int i = 0; i < count; i++)
		{
			PlayerState member = PlayerState.ReadRuntime(r);
			if (member == null)
			{
				GD.PushWarning($"Party: saved member {i}'s template no longer exists — dropping them.");
			}
			loaded.Add(member);
		}
		int savedActive = r.ReadInt32();
		PlayerState active = savedActive >= 0 && savedActive < loaded.Count ? loaded[savedActive] : null;
		foreach (PlayerState member in loaded)
		{
			if (member != null)
			{
				party._members.Add(member);
			}
		}
		party._activeIndex = active != null ? party._members.IndexOf(active) : 0;
		party.Knowledge.Deserialize(r);
		party.Chart.Deserialize(r);
		savedOrder = loaded;
		return party;
	}

	// Append a new member to the roster (a recruited NPC). Joins as an inactive
	// member — the active index is unchanged — so control stays with whoever the
	// player is driving. Returns the new member's index. Flags them to win the
	// next morning's well-rested lottery outright — a fresh recruit arrives rested.
	public int Add(PlayerState member)
	{
		if (member != null)
		{
			member.ForceWellRestedNextDay = true;
			member.RestDays = 1;
		}
		_members.Add(member);
		return _members.Count - 1;
	}

	// Advance the rest bookkeeping and pick this rest's "well rested" member.
	// Called once per rest (Sim.RestToSunrise). Clears the last pick, ages every
	// member's rest counter (the still-controlled member stays at 0 — they're
	// being used, so they can never be their own well-rested pick), then draws one
	// idle member weighted by how long they've rested. A freshly recruited member
	// (ForceWellRestedNextDay) wins outright. Returns the winner, or null if nobody
	// was eligible (e.g. a solo party).
	public PlayerState AdvanceRestAndPickWellRested(System.Random rng)
	{
		// 1. Yesterday's buff expires for everyone.
		for (int i = 0; i < _members.Count; i++)
		{
			if (_members[i] != null) { _members[i].IsWellRested = false; }
		}

		// 2. Age the rest counters, then pin the controlled member back to 0.
		for (int i = 0; i < _members.Count; i++)
		{
			PlayerState m = _members[i];
			if (m != null) { m.RestDays++; }
		}
		if (Active != null) { Active.RestDays = 0; }

		// 3. A forced (freshly recruited) member wins outright; otherwise draw from
		//    idle members, weighting by rest days so the longest-rested is
		//    likeliest. The controlled member (RestDays 0) is never in the pool.
		PlayerState winner = null;
		for (int i = 0; i < _members.Count; i++)
		{
			PlayerState m = _members[i];
			if (m != null && m.ForceWellRestedNextDay)
			{
				winner = m;
				break;
			}
		}
		if (winner == null)
		{
			int totalWeight = 0;
			for (int i = 0; i < _members.Count; i++)
			{
				PlayerState m = _members[i];
				if (m == null || m.RestDays < 1) { continue; }
				totalWeight += m.RestDays;
			}
			if (totalWeight > 0)
			{
				int roll = rng.Next(totalWeight);
				for (int i = 0; i < _members.Count; i++)
				{
					PlayerState m = _members[i];
					if (m == null || m.RestDays < 1) { continue; }
					roll -= m.RestDays;
					if (roll < 0) { winner = m; break; }
				}
			}
		}

		// 4. Crown the winner: rested today, and reset to 1 so they can still be
		//    drawn tomorrow, just with the lowest odds.
		if (winner != null)
		{
			winner.IsWellRested = true;
			winner.RestDays = 1;
			winner.ForceWellRestedNextDay = false;
		}
		return winner;
	}

	// Bank the active member's provisional field knowledge into the permanent
	// party pool, then clear their individual store. Called when the player camps
	// (the "return to a campfire" commit). Clearing after the merge is required so
	// re-camping can't double-count species kills. Only the active member is
	// merged — they're the only one who explores in the field.
	public EKnowledgeCategory BankActive()
	{
		PlayerState active = Active;
		if (active == null)
		{
			return EKnowledgeCategory.None;
		}
		EKnowledgeCategory banked = Knowledge.MergeFrom(active.Knowledge);
		active.Knowledge.Clear();
		return banked;
	}

	// Point control at a different member. Clamped to the roster; a no-op if the
	// index is already active or out of range. Returns true if the active member
	// actually changed (the caller re-hosts control on the corresponding Player).
	public bool SetActive(int index)
	{
		if (index < 0 || index >= _members.Count || index == _activeIndex)
		{
			return false;
		}
		_activeIndex = index;
		// Taking control counts as "using" this member — reset their rest counter
		// so the well-rested lottery treats them as freshly used, even mid-day.
		if (_members[index] != null) { _members[index].RestDays = 0; }
		return true;
	}
}
