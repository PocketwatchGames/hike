using System;
using System.Collections.Generic;

// Sim-side party-member lifecycle: the revive-deadline bookkeeping for fallen
// members. Detection lives here (it's pure sim state — PlayerState.IsDead /
// ReviveByDay against DayNumber); the client only tears down the corpse's Player
// NODE in response to onPartyMemberExpired, since those nodes are GameClient's.
public partial class Sim
{
    // Fired for each fallen member whose revive deadline the clock has reached.
    // GameClient frees the corpse body node and drops the roster entry. Passes the
    // PlayerState (not an index) so the client resolves the matching node itself
    // and index shifts between detection and teardown can't misfire.
    public event Action<PlayerState> onPartyMemberExpired;

    // Reused across ticks; expired members are collected first, then reported, so
    // the roster isn't mutated (by the client handler) mid-scan.
    readonly List<PlayerState> _expiredMembers = new();

    // Give every fallen member without a deadline one day of grace: they must be
    // revived before the NEXT sunrise (a full day past the one the party just woke
    // at) or be lost. Called from the death time-skip once DayNumber sits on the
    // wake-up day.
    public void AssignReviveDeadlines()
    {
        Party party = _worldState?.SimState?.Party;
        if (party == null)
        {
            return;
        }
        int deadlineDay = DayNumber + 1;
        for (int i = 0; i < party.Members.Count; i++)
        {
            PlayerState m = party.Members[i];
            if (m != null && m.IsDead && m.ReviveByDay <= 0)
            {
                m.ReviveByDay = deadlineDay;
            }
        }
    }

    // Report any fallen member whose revive deadline the clock has reached. Runs
    // every tick (natural day passage) and once right after the death time-skip, so
    // a corpse left un-revived past its deadline is retired promptly.
    public void CheckReviveDeadlines()
    {
        Party party = _worldState?.SimState?.Party;
        if (party == null)
        {
            return;
        }
        int today = DayNumber;
        _expiredMembers.Clear();
        for (int i = 0; i < party.Members.Count; i++)
        {
            PlayerState m = party.Members[i];
            if (m != null && m.IsDead && m.ReviveByDay > 0 && today >= m.ReviveByDay)
            {
                _expiredMembers.Add(m);
            }
        }
        for (int i = 0; i < _expiredMembers.Count; i++)
        {
            onPartyMemberExpired?.Invoke(_expiredMembers[i]);
        }
    }
}
