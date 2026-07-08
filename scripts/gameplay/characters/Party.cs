using System.Collections.Generic;
using Godot;

// The player's roster of characters. One member is "active" (the character the
// player currently controls, driving the single controlled Player node); the
// rest are inactive party members that spawn around camp. Lives on
// WorldSimState (worldState.SimState.Party), a sibling of the party stashes, so
// it's world-scope run state that SaveGame will persist.
//
// Members are runtime PlayerState instances — built once at game start by
// cloning the authored WorldGenData.startingParty templates (see
// Party.FromTemplates), so mutating a member's state never touches the .tres.
public class Party
{
	readonly List<PlayerState> _members = new();
	int _activeIndex;

	// The permanent, party-shared knowledge pool (identified items, discovered
	// recipes/species/regions, learned languages). The active member accrues field
	// knowledge into their own PlayerState.Knowledge; BankActive folds it in here
	// when the player camps. Reads combine this with the active member's store.
	public readonly Knowledge Knowledge = new();

	public IReadOnlyList<PlayerState> Members => _members;
	public int Count => _members.Count;
	public int ActiveIndex => _activeIndex;

	// The currently-controlled member, or null on an empty roster.
	public PlayerState Active =>
		_activeIndex >= 0 && _activeIndex < _members.Count ? _members[_activeIndex] : null;

	// Living (not fallen) members. Used by the death flow: a total wipe (0 alive)
	// ends the run; otherwise the player picks a survivor to control.
	public int AliveCount
	{
		get
		{
			int n = 0;
			for (int i = 0; i < _members.Count; i++)
			{
				if (_members[i] != null && !_members[i].IsDead) { n++; }
			}
			return n;
		}
	}

	// Index of the first living member, or -1 if the whole party is dead.
	public int FirstAliveIndex()
	{
		for (int i = 0; i < _members.Count; i++)
		{
			if (_members[i] != null && !_members[i].IsDead) { return i; }
		}
		return -1;
	}

	public bool IsAlive(int index) => this[index] is { IsDead: false };

	// Permanently remove a member (their un-revived body was destroyed). Keeps the
	// active index pointing at the same member by shifting it when an earlier slot
	// is removed. Only dead members are ever removed, so the active (living,
	// controlled) member is never the one dropped.
	public void RemoveAt(int index)
	{
		if (index < 0 || index >= _members.Count)
		{
			return;
		}
		_members.RemoveAt(index);
		if (index < _activeIndex)
		{
			_activeIndex--;
		}
		if (_activeIndex >= _members.Count)
		{
			_activeIndex = _members.Count > 0 ? _members.Count - 1 : 0;
		}
	}

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
				party._members.Add((PlayerState)template.Duplicate(true));
			}
		}
		return party;
	}

	// Append a new member to the roster (a recruited NPC). Joins as an inactive
	// member — the active index is unchanged — so control stays with whoever the
	// player is driving. Returns the new member's index.
	public int Add(PlayerState member)
	{
		_members.Add(member);
		return _members.Count - 1;
	}

	// Bank the active member's provisional field knowledge into the permanent
	// party pool, then clear their individual store. Called when the player camps
	// (the "return to a campfire" commit). Clearing after the merge is required so
	// re-camping can't double-count species kills. Only the active member is
	// merged — they're the only one who explores in the field.
	public void BankActive()
	{
		PlayerState active = Active;
		if (active == null)
		{
			return;
		}
		Knowledge.MergeFrom(active.Knowledge);
		active.Knowledge.Clear();
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
		return true;
	}
}
