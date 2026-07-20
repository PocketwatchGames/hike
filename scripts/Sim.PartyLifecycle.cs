using System;
using System.Collections.Generic;
using Godot;

// Sim-side party & member lifecycle: the authoritative roster (recruit / active /
// death / revive), the revive-deadline bookkeeping for fallen members, and the
// camp / rest / return-home time-skips that restore the controlled member. The
// roster and every member-state mutation live here; GameClient owns only the
// Player NODES and mirrors the roster with them (spawning on recruit, tearing down
// on onPartyMemberExpired), so the two never share a mutation.
public partial class Sim
{
    // Draws the daily "well rested" member each sunrise (see AdvanceToNextSunrise →
    // Party.AdvanceRestAndPickWellRested). Sim-side because the pick is roster state.
    readonly System.Random _wellRestedRng = new();

    // The active roster, or null before it's built. Read access for the client (it
    // reads ActiveIndex / members to drive the party UI); all WRITES go through the
    // Sim methods below so no roster mutation lives in the client.
    public Party Party => _worldState?.SimState?.Party;
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
            // Drop the roster entry HERE (sim owns the roster), then let the client
            // tear down the matching Player node. The node still carries its Member
            // reference after the roster removal, so the client resolves it by identity.
            PlayerState expired = _expiredMembers[i];
            party.Remove(expired);
            onPartyMemberExpired?.Invoke(expired);
        }
    }

    // Ensure the runtime roster exists, building it once from the authored templates.
    // Idempotent: a future disk-load that already carries a party is left intact.
    // Returns the live roster so the client can spawn a Player node per member.
    public Party EnsureParty(IEnumerable<PlayerState> templates)
    {
        SimState sim = _worldState?.SimState;
        if (sim == null)
        {
            return Party.FromTemplates(templates);
        }
        if (sim.Party == null)
        {
            sim.Party = Party.FromTemplates(templates);
        }
        return sim.Party;
    }

    // Bind this world's authored scripted content (quests) onto the runtime state.
    public void BindScriptData(WorldScriptData scriptData)
    {
        if (_worldState != null)
        {
            _worldState.ScriptData = scriptData;
        }
    }

    // Clone a recruit template into a new inactive roster member and return it (the
    // client spawns the matching Player node on the campfire ring). Null if there's
    // no roster or no template.
    public PlayerState RecruitMember(PlayerState template)
    {
        Party party = Party;
        if (template == null || party == null)
        {
            return null;
        }
        // Clone so the roster member is independent of the authored .tres (their
        // vitals / inventory evolve per-run), matching Party.FromTemplates.
        var member = (PlayerState)template.Duplicate(true);
        party.Add(member);
        return member;
    }

    // Point control at a different roster member (data only — the client re-hosts the
    // controlled Player on the next SyncControlToActive). Returns true if it changed.
    public bool SetPartyActive(int index) => Party?.SetActive(index) ?? false;

    // Mark a member fallen: their body becomes a revivable corpse. PlayerState-level
    // only; the client turns the Player node into a standing dead-pose body.
    public void MarkMemberDead(PlayerState member)
    {
        if (member != null)
        {
            member.IsDead = true;
        }
    }

    // Restore a fallen member: fold their un-banked field knowledge back into the
    // reviver's provisional store and clear the death flags. Returns true if knowledge
    // moved, so the client recomposes the minimap fog to surface it.
    public bool ReviveMember(PlayerState reviver, PlayerState corpse)
    {
        if (corpse == null || !corpse.IsDead)
        {
            return false;
        }
        bool moved = false;
        if (reviver != null && reviver != corpse)
        {
            reviver.Knowledge.MergeFrom(corpse.Knowledge);
            corpse.Knowledge.Clear();
            moved = true;
        }
        corpse.IsDead = false;
        corpse.ReviveByDay = 0;
        return moved;
    }

    // Commit a camp stop: bank the active member's provisional field knowledge into
    // the permanent party pool and drain their carried materials into the shared
    // stash. Returns the banked knowledge categories so the client can announce them;
    // the map-reveal bookkeeping stays client-side (it's presentation).
    public EKnowledgeCategory CommitCamp()
    {
        EKnowledgeCategory banked = _worldState?.SimState?.BankActiveKnowledge() ?? EKnowledgeCategory.None;
        List<ItemState> stash = _worldState?.SimState?.PartyMaterialStash;
        Inventory inv = _player?.Inventory;
        if (inv != null && stash != null)
        {
            foreach (ItemState material in inv.DrainBackpack())
            {
                ItemStash.Add(stash, material);
            }
        }
        return banked;
    }

    // Sleep behind the client's fade. toSunrise rolls to the next day and full-heals
    // the controlled member (a DoT can't chip or kill them in their sleep); otherwise
    // a nap integrates effects over `hours` then heals a fraction. A surviving
    // companion wakes at the player's side (one that died stays dead).
    public void PerformSleepAdvance(double hours, double healFractionPerHour, bool toSunrise)
    {
        if (toSunrise)
        {
            AdvanceToNextSunrise();
            if (_player != null && !_player.IsDead)
            {
                _player.ClearTransientStatusEffects();
                _player.Heal(_player.MaxHealth);
            }
        }
        else
        {
            // Rest heals AFTER the skip's status effects resolve, so a DoT that ran
            // during the nap lands first — and a player it killed isn't revived by the heal.
            AdvanceTime(hours);
            if (_player != null && !_player.IsDead)
            {
                _player.Heal((float)(_player.MaxHealth * healFractionPerHour * hours));
            }
        }
        if (_player != null)
        {
            Companion?.RecallToPlayer(_player.GlobalPosition);
        }
    }

    // The death "sleep off": advance to the next sunrise, grant the newly-fallen
    // member their one-day revive grace, and retire anyone whose deadline the skip
    // just passed.
    public void ResolveDeathDayRoll()
    {
        AdvanceToNextSunrise();
        AssignReviveDeadlines();
        CheckReviveDeadlines();
    }

    // Respawn the controlled member at `pos`: reset their pools/effects, refill their
    // carried lanterns (this path doesn't roll the day), and recall a surviving
    // companion. The client keeps the camera snap and death-cam release.
    public void RespawnControlledPlayer(Vector3 pos)
    {
        if (_player == null)
        {
            return;
        }
        _player.Respawn(pos);
        _player.RefuelLantern();
        Companion?.RecallToPlayer(pos);
    }

    // Pray-return-home: teleport the controlled member to `pos`, sleep to the next
    // sunrise (clear transient effects, full-heal, refuel lanterns), and recall a
    // surviving companion. Deliberately does NOT bank — that's the cost of the free
    // trip. The client keeps the camera reframe, campfire relight, and camp screen.
    public void ReturnHomeToSunrise(Vector3 pos)
    {
        if (_player == null)
        {
            return;
        }
        _player.TeleportTo(pos);
        AdvanceToNextSunrise();
        if (!_player.IsDead)
        {
            _player.ClearTransientStatusEffects();
            _player.Heal(_player.MaxHealth);
        }
        _player.RefuelLantern();
        Companion?.RecallToPlayer(pos);
    }

    // Thin command wrappers so the client records discoveries without reaching
    // through WorldState.SimState. Each guards / announces inside SimState.
    public void DiscoverSpecies(SpeciesData species) => _worldState?.SimState?.DiscoverSpecies(species);
    public void DiscoverRegion(RegionData region) => _worldState?.SimState?.DiscoverRegion(region);
    public void SnapshotWorldMapReveal() => _worldState?.SimState?.SnapshotWorldMapReveal();
}
