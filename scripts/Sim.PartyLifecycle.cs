using System;
using System.Collections.Generic;
using Godot;

// Sim-side party & member lifecycle: the authoritative roster (recruit / active),
// and the camp / rest / return-home / death time-skips that restore the controlled
// member. The roster and every member-state mutation live here; GameClient owns
// only the Player NODES and mirrors the roster with them (spawning on recruit), so
// the two never share a mutation.
public partial class Sim
{
    // Salts the daily "well rested" draw (AdvanceToNextSunrise →
    // Party.AdvanceRestAndPickWellRested) off WorldState.DailyRandom.
    private const int WELL_RESTED_SALT = 0x4E57;

    // The active roster, or null before it's built. Read access for the client (it
    // reads ActiveIndex / members to drive the party UI); all WRITES go through the
    // Sim methods below so no roster mutation lives in the client.
    public Party Party => _worldState?.SimState?.Party;

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
        PlayerState member = PlayerState.FromTemplate(template);
        party.Add(member);
        return member;
    }

    // Point control at a different roster member (data only — the client re-hosts the
    // controlled Player on the next SyncControlToActive). Returns true if it changed.
    public bool SetPartyActive(int index) => Party?.SetActive(index) ?? false;

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

    // The death wake: the controlled member stands back up at `pos` and the party
    // sleeps to the next sunrise, as a pray-home does. Respawn comes first — the
    // time-skip stops early on a dead controlled member.
    public void RespawnAtSunrise(Vector3 pos)
    {
        if (_player == null)
        {
            return;
        }
        _player.Respawn(pos);
        ReturnHomeToSunrise(pos);
    }

    // Thin command wrappers so the client records discoveries without reaching
    // through WorldState.SimState. Each guards / announces inside SimState.
    public void DiscoverSpecies(SpeciesData species) => _worldState?.SimState?.DiscoverSpecies(species);
    public void DiscoverRegion(RegionData region) => _worldState?.SimState?.DiscoverRegion(region);
}
