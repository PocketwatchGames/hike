using System;
using System.Collections.Generic;
using Godot;

// Aggregates per-mob combat reports into a single "is the player in combat"
// state and raises edge events (onCombatBegin / onCombatEnd) on GameClient for
// music and any other client-side reaction. Owned by GameClient, ticked each
// frame after World.Tick. Distinct from AIOutput.inCombat (a mob's own
// awareness, used for AI-tick LOD): combat here keys off the PLAYER perceiving
// a dangerous enemy that is actively attacking.
//
// Rules:
//   ON   — any dangerous, player-perceived enemy is in an attack behavior.
//   OFF  — no such enemy for combatExitGraceSeconds (the player ran away:
//          lost perception, or the enemy disengaged).
//   OFF immediately — when the last relevant enemy is KILLED and no dangerous
//          enemy has been perceived within the grace window (don't make the
//          player wait out the linger after they win the fight).
//
// Dangerous hostile mobs self-report every AI tick via Report; deaths route
// through OnMobDied (from Mob.Die). Entries age out so a mob that stops
// reporting (streamed out, suspended) can't pin combat on forever.
public class CombatTracker
{
    private struct Entry
    {
        public ulong lastPerceivedMs;   // last tick the player perceived this mob
        public ulong lastEngagedMs;     // last tick it was perceived AND attacking
    }

    // Freshness window for an "engaged" report. Engaged mobs are near and
    // fighting, so they tick at full 60Hz — a quarter-second covers a few
    // skipped frames without letting a disengaged mob read as still engaged.
    private const ulong EngagedFreshMs = 250;

    private readonly Dictionary<Mob, Entry> _entries = new();
    private readonly ulong _graceMs;
    private List<Mob> _pruneScratch;

    private bool _inCombat;
    private ulong _exitDeadlineMs;      // 0 = no pending exit

    public bool InCombat => _inCombat;
    public Action onCombatBegin;
    public Action onCombatEnd;
    // Fired in addition to onCombatEnd when combat ends specifically because the
    // player killed the last perceived threat (vs running away). The "you won
    // the fight" flourish — victory sting + slow-mo + camera focus — hangs off
    // this. Carries the mob whose death ended the fight (the finisher target).
    public Action<Mob> onCombatVictory;

    public CombatTracker(float graceSeconds)
    {
        _graceMs = (ulong)(graceSeconds * 1000f);
    }

    // Called by dangerous hostile mobs each AI tick while the player perceives
    // them. `engaged` adds "and currently in an attack behavior".
    public void Report(Mob mob, bool engaged, ulong nowMs)
    {
        _entries.TryGetValue(mob, out Entry e);
        e.lastPerceivedMs = nowMs;
        if (engaged) { e.lastEngagedMs = nowMs; }
        _entries[mob] = e;
    }

    // Routed from Mob.Die. If killing this mob leaves nothing engaged and
    // nothing dangerous perceived within the grace window, end combat now
    // rather than waiting out the linger.
    public void OnMobDied(Mob mob, ulong nowMs)
    {
        bool wasTracked = _entries.Remove(mob);
        if (!wasTracked || !_inCombat) { return; }
        if (!AnyEngaged(nowMs) && !AnyPerceivedWithinGrace(nowMs))
        {
            SetCombat(false);        // fires onCombatEnd
            onCombatVictory?.Invoke(mob);
        }
    }

    public void Tick(ulong nowMs)
    {
        Prune(nowMs);

        if (AnyEngaged(nowMs))
        {
            _exitDeadlineMs = 0;
            SetCombat(true);
        }
        else if (_inCombat)
        {
            if (_exitDeadlineMs == 0)
            {
                _exitDeadlineMs = nowMs + _graceMs;
            }
            else if (nowMs >= _exitDeadlineMs)
            {
                SetCombat(false);
            }
        }
    }

    private bool AnyEngaged(ulong nowMs)
    {
        foreach (KeyValuePair<Mob, Entry> kv in _entries)
        {
            if (nowMs - kv.Value.lastEngagedMs <= EngagedFreshMs) { return true; }
        }
        return false;
    }

    private bool AnyPerceivedWithinGrace(ulong nowMs)
    {
        foreach (KeyValuePair<Mob, Entry> kv in _entries)
        {
            if (nowMs - kv.Value.lastPerceivedMs <= _graceMs) { return true; }
        }
        return false;
    }

    // Drop freed mobs and entries no longer engaged whose last perception is
    // older than the grace window — they can't influence combat state anymore.
    private void Prune(ulong nowMs)
    {
        _pruneScratch?.Clear();
        foreach (KeyValuePair<Mob, Entry> kv in _entries)
        {
            bool gone = !GodotObject.IsInstanceValid(kv.Key);
            bool engagedFresh = nowMs - kv.Value.lastEngagedMs <= EngagedFreshMs;
            bool perceivedFresh = nowMs - kv.Value.lastPerceivedMs <= _graceMs;
            if (gone || (!engagedFresh && !perceivedFresh))
            {
                (_pruneScratch ??= new List<Mob>()).Add(kv.Key);
            }
        }
        if (_pruneScratch != null)
        {
            for (int i = 0; i < _pruneScratch.Count; i++) { _entries.Remove(_pruneScratch[i]); }
        }
    }

    private void SetCombat(bool value)
    {
        if (_inCombat == value) { return; }
        _inCombat = value;
        _exitDeadlineMs = 0;
        if (value) { onCombatBegin?.Invoke(); }
        else { onCombatEnd?.Invoke(); }
    }
}
