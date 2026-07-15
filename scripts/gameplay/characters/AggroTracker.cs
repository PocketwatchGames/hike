using System.Collections.Generic;
using Godot;

// Per-mob damage-driven threat priority, kept deliberately separate from
// perception (awareness). Each tracked enemy accrues aggro equal to the health
// damage it deals to this mob — or, for a companion, the damage it deals to the
// companion's master — scaled by the hit's DamageData.aggroMultiplier, and
// bleeds back down at MobData.aggroReductionSpeed per second. Target selection
// (BehaviorAttack.ResolveTarget for hostiles choosing player-vs-companion;
// ThreatScan for companions ranking hostiles) picks the perceivable enemy with
// the most aggro, so a mob fights whoever has hurt it (or its master) most
// rather than merely whoever is nearest. Transient combat state — not
// serialized; a reloaded mob re-earns aggro as the fight resumes.
public class AggroTracker
{
    private struct Entry
    {
        public Node3D target;
        public float aggro;
    }

    // Small by construction — a mob only tracks the handful of enemies it can
    // perceive (the player and the companion today). A flat list scans faster
    // than a dictionary at this size and sidesteps stale-key hazards when a
    // target node is freed.
    private readonly List<Entry> _entries = new();

    // Credit `target` with `amount` aggro, creating the entry on first contact.
    public void Add(Node3D target, float amount)
    {
        if (target == null || amount <= 0f)
        {
            return;
        }
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].target == target)
            {
                Entry e = _entries[i];
                e.aggro += amount;
                _entries[i] = e;
                return;
            }
        }
        _entries.Add(new Entry { target = target, aggro = amount });
    }

    // Current aggro toward `target` (0 when untracked) — read by target
    // selection to rank candidates.
    public float Get(Node3D target)
    {
        if (target == null)
        {
            return 0f;
        }
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].target == target)
            {
                return _entries[i].aggro;
            }
        }
        return 0f;
    }

    // Drop every tracked enemy — used by a full spawn-state reset (World
    // .ResetSpawns) so a revived/returned mob starts the next encounter
    // with no leftover threat priority.
    public void Clear()
    {
        _entries.Clear();
    }

    // Bleed every entry down by ratePerSecond * delta and drop entries that have
    // decayed away or whose target has been freed / killed, so the list stays
    // bounded and never hands back a dead node.
    public void Decay(float ratePerSecond, float delta)
    {
        float drop = ratePerSecond * delta;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            Entry e = _entries[i];
            bool targetGone = !GodotObject.IsInstanceValid(e.target)
                || (e.target is Mob m && !m.alive);
            e.aggro -= drop;
            if (targetGone || e.aggro <= 0f)
            {
                _entries.RemoveAt(i);
                continue;
            }
            _entries[i] = e;
        }
    }
}
