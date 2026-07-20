using Godot;

// Ambient after-dark threat spawner: while it is night, keeps a live population
// of "night mobs" (the gellies) around the player that grows denser as midnight
// nears and only spawns away from artificial light. A global mechanic — not
// zone-based. Sibling of WeatherLightningSpawner under World.
//
// Light rule: spawns are allowed on open moonlit ground and in shadow alike —
// the gate is against BLOCK light only (torches, campfires, lanterns), not sky
// light, so mobs shun the lit circle around a fire but not plain moonlight. The
// night-only window keeps them out of daylight without a separate sun check.
//
// Spawn cadence is a cooldown that only counts down while there's room under the
// current population target: at the cap the timer holds at full, so a freshly-
// opened slot (a kill, or the target rising toward midnight) always waits a
// fresh full interval rather than firing off an already-elapsed timer.
//
// Spawns are TRANSIENT — created straight into the chunk's active-entity list
// via Sim.SpawnMobTransient, NOT recorded in WorldState. So they cost nothing
// to persist, vanish with their chunk when the player moves on, and never
// re-materialize: the population near the player is recomputed live each sweep
// rather than accumulated across the whole map. Each carries
// ESpawnConditions.Night so the existing off-condition cleanup fades any
// stragglers once dawn breaks (Sim.CleanupOffConditionMobs).
//
// Dormant (no cost) when SimData.nightSpawnMobs is empty or it isn't night.
[GlobalClass]
public partial class NightMobSpawner : Node
{
    // Height above the found surface to sample block light at — roughly where the
    // mob's body sits, so a torch pooling light on the floor still repels it.
    private const float LightSampleHeight = 0.3f;

    // Hard cap on the nav-grid window half-extent (voxels) the spawn search scans,
    // bounding worst-case cost to (2·this+1)² columns even if the spawn radius is
    // authored huge. The window still covers the smaller of this and the radius.
    private const int MaxWindowHalfExtent = 40;

    // Seconds between night_spawn_debug status dumps.
    private const double DebugIntervalSeconds = 1.0;

    // A standable, dark-enough spawn spot collected from the nav-grid window, with
    // its darkness weight for the weighted pick.
    private struct SpawnCandidate
    {
        public Vector3 Pos;
        public float Weight;
    }

    private double _timeUntilNext;
    private double _debugTimer;
    private readonly RandomNumberGenerator _rng = new RandomNumberGenerator();
    // Reused nav-grid window, standable-position buffer, and candidate pool
    // (rebuilt each spawn cycle, no per-cycle allocation).
    private readonly WalkabilityGrid _grid = new WalkabilityGrid();
    private readonly System.Collections.Generic.List<Vector3> _standable = new();
    private readonly System.Collections.Generic.List<SpawnCandidate> _candidates = new();

    public override void _Ready()
    {
        _rng.Randomize();
    }

    public override void _Process(double delta)
    {
        Sim sim = Sim.Current;
        SimData data = sim?.SimData;
        if (sim == null || data == null || data.nightSpawnMobs == null || data.nightSpawnMobs.Count == 0)
        {
            return;
        }
        Player player = sim.player;
        if (player == null)
        {
            return;
        }

        // ONE danger scalar drives everything: max of the time-of-day term (calm
        // by day, ramping sunset → midnight) and the local darkness dwell (a cave /
        // dungeon / shadow charging up, day or night). This IS the max the design
        // wants — midnight OR deep dark is dangerous, both together no more so.
        float danger = Danger(sim, data);
        float interval = Mathf.Lerp(data.nightSpawnSlowIntervalSeconds, data.nightSpawnIntervalSeconds, danger);

        // Population cap scales with danger; below ~one slime's worth it rounds to
        // zero and nothing spawns (a calm moonlit dusk, a lit daytime field).
        int target = Mathf.RoundToInt(data.nightSpawnMaxPopulation * danger);
        int current = CountLiveNightMobs(sim, data);

        // Periodic diagnostics — the status dump (night_spawn_debug) and/or the
        // in-world overlay of every valid spawn spot (night_spawn_draw). Both run
        // regardless of whether anything spawns, so a "why aren't they spawning
        // here / why on the surface not in the cave?" can be seen directly. Shares
        // one throttled collect for both.
        if (CVars.nightSpawnDebug.Value || CVars.nightSpawnDraw.Value)
        {
            _debugTimer -= delta;
            if (_debugTimer <= 0.0)
            {
                _debugTimer = DebugIntervalSeconds;
                MobData probeMob = data.nightSpawnMobs.Count > 0 ? data.nightSpawnMobs[0]?.mob : null;
                if (probeMob != null)
                {
                    CollectSpawnCandidates(sim, data, new TraversalProfile(probeMob), player.GlobalPosition);
                }
                if (CVars.nightSpawnDebug.Value)
                {
                    PrintDebug(sim, data, danger, target, current, interval, probeMob != null ? _candidates.Count : 0);
                }
                if (CVars.nightSpawnDraw.Value)
                {
                    DrawSpawnCandidates(data, player.GlobalPosition);
                }
            }
        }

        if (current >= target)
        {
            // At/over the cap: hold the cooldown at full so the countdown only ever
            // runs while there's room. A freshly-opened slot (a kill, or danger
            // rising) then waits a fresh full interval rather than firing off an
            // already-elapsed timer sitting at zero on the cap.
            _timeUntilNext = interval;
            return;
        }

        _timeUntilNext -= delta;
        if (_timeUntilNext > 0.0)
        {
            return;
        }
        _timeUntilNext = interval;
        Spawn(sim, data, player, target, current, danger);
    }

    // Danger [0,1] = max(time-of-day term, darkness dwell). The time term is
    // pow(nightProgress, curve): 0 all day (nightProgress clamps to 0 before
    // sunset), ramping to 1 at midnight. The darkness dwell (Sim.DarknessDwell)
    // supplies danger in dark places regardless of the hour.
    private static float Danger(Sim sim, SimData data)
    {
        float t = (float)((sim.WorldState.TimeOfDay01 - WorldState.SunsetTimeOfDay01)
            / (WorldState.MidnightTimeOfDay01 - WorldState.SunsetTimeOfDay01));
        float timeDanger = Mathf.Pow(Mathf.Clamp(t, 0f, 1f), data.nightTimeDangerCurve);
        return Mathf.Max(timeDanger, sim.DarknessDwell);
    }

    // Dump every input that feeds slime spawning plus the candidate `pool` size
    // (collected by the caller), and a one-word reason nothing is spawning, so the
    // mechanic can be diagnosed. Toggle with `night_spawn_debug 1`.
    private void PrintDebug(Sim sim, SimData data, float danger, int target, int current, float interval, int pool)
    {
        double tod = sim.WorldState.TimeOfDay01;
        float t = (float)((tod - WorldState.SunsetTimeOfDay01)
            / (WorldState.MidnightTimeOfDay01 - WorldState.SunsetTimeOfDay01));
        float timeDanger = Mathf.Pow(Mathf.Clamp(t, 0f, 1f), data.nightTimeDangerCurve);
        float total01 = sim.player?.visibilityLight ?? 1f;
        float darkTarget = data.nightDarkThreshold > 0f
            ? Mathf.Clamp((data.nightDarkThreshold - total01) / data.nightDarkThreshold, 0f, 1f)
            : (total01 <= 0f ? 1f : 0f);

        // pool == 0 while target > 0 is the "why aren't they spawning" signal (no
        // reachable dark standable ground in range at the player's level).
        string reason;
        if (target <= 0)
        {
            reason = "danger-too-low (no target)";
        }
        else if (current >= target)
        {
            reason = "at-cap";
        }
        else if (pool == 0)
        {
            reason = "no-dark-standable-ground-in-range";
        }
        else
        {
            reason = "spawning";
        }

        GD.Print(
            $"[nightspawn] tod={tod:F3} night={WorldState.IsNight(tod)} timeTerm={timeDanger:F2} " +
            $"visLight={total01:F2} block={sim.PlayerBlockLight01:F2} darkTarget={darkTarget:F2} " +
            $"dwell={sim.DarknessDwell:F2} danger={danger:F2} | target={target} current={current} " +
            $"interval={interval:F1} timer={_timeUntilNext:F1} | standable={_standable.Count} pool={pool} | {reason}");
    }

    // In-world overlay of the last collected spawn search, so "where are the valid
    // spawns?" is answerable at a glance (toggle `night_spawn_draw 1`). Persists
    // one debug interval so it doesn't flicker. Reads _standable / _candidates
    // populated by the shared CollectSpawnCandidates call in _Process.
    //   • gray box   = standable ground found in range (pre light-gate)
    //   • red→green box = a VALID candidate, green = darker (higher spawn weight),
    //                     red = barely dark enough — its Y is the spawn height, so
    //                     boxes up on the surface vs down at your feet show whether
    //                     the search is finding the cave floor or the roof above it.
    //   • cyan cross = the player (search center).
    private void DrawSpawnCandidates(SimData data, Vector3 playerPos)
    {
        float life = (float)DebugIntervalSeconds * 1.1f;
        DebugDraw.Cross(playerPos + Vector3.Up * 0.2f, 1.0f, new Color(0f, 1f, 1f), life);
        for (int k = 0; k < _standable.Count; k++)
        {
            DebugDraw.BoxCentered(_standable[k] + Vector3.Up * 0.4f, new Vector3(0.9f, 0.05f, 0.9f), new Color(0.5f, 0.5f, 0.5f, 0.5f), life);
        }
        for (int k = 0; k < _candidates.Count; k++)
        {
            // Weight was pow(darkness, bias); undo the bias for a linear 0..1 hue so
            // the color reads as "how dark" rather than the raw weight.
            float darkness = data.nightSpawnDarknessBias > 0f
                ? Mathf.Pow(Mathf.Clamp(_candidates[k].Weight, 0f, 1f), 1f / data.nightSpawnDarknessBias)
                : Mathf.Clamp(_candidates[k].Weight, 0f, 1f);
            Color c = new Color(1f - darkness, darkness, 0.1f);
            DebugDraw.BoxCentered(_candidates[k].Pos + Vector3.Up * 0.5f, new Vector3(0.7f, 0.9f, 0.7f), c, life);
        }
    }

    // Fill toward the target this cycle, at most nightSpawnMaxPerSweep, so a danger
    // spike builds up over several intervals instead of a sudden wall. Each mob's
    // level is round(danger × nightSpawnMaxLevel) — midnight and deep darkness both
    // drive it toward the max, so a moonlit dusk spawns weak gellies and a pitch-
    // black midnight spawns level-max ones.
    private void Spawn(Sim sim, SimData data, Player player, int target, int current, float danger)
    {
        int toSpawn = Mathf.Min(target - current, data.nightSpawnMaxPerSweep);
        int level = Mathf.RoundToInt(danger * data.nightSpawnMaxLevel);
        Vector3 playerPos = player.GlobalPosition;

        // Collect EVERY valid, dark-enough spot in a nav-grid window around the
        // player once, then place from that pool. Enumerating the walkable cells
        // (rather than throwing random darts that mostly miss a narrow tunnel) is
        // what makes confined spaces populate. Sampled with the first night mob's
        // profile — the variants share a body size.
        MobData sampleMob = data.nightSpawnMobs.Count > 0 ? data.nightSpawnMobs[0]?.mob : null;
        if (sampleMob == null)
        {
            return;
        }
        CollectSpawnCandidates(sim, data, new TraversalProfile(sampleMob), playerPos);

        int spawned = 0;
        for (int i = 0; i < toSpawn && _candidates.Count > 0; i++)
        {
            int idx = PickWeightedCandidate();
            Vector3 pos = _candidates[idx].Pos;
            _candidates.RemoveAtSwap(idx); // place without replacement so two don't stack on one cell
            MobDescriptor descriptor = data.nightSpawnMobs[_rng.RandiRange(0, data.nightSpawnMobs.Count - 1)];
            if (sim.SpawnMobTransient(descriptor, pos, ESpawnConditions.Night, level) != null)
            {
                spawned++;
            }
        }

        if (CVars.nightSpawnLog.Value)
        {
            GD.Print($"[nightspawn] danger={danger:F2} dwell={sim.DarknessDwell:F2} block={sim.PlayerBlockLight01:F2} target={target} current={current} pool={_candidates.Count + spawned} spawned={spawned} level={level}");
        }
    }

    // Gather the standable spots around the player (shared nav-grid enumeration,
    // cave/tunnel-correct — see NavigationGoals.CollectStandableCells), then apply
    // the night-spawn-specific gate/weight: drop block-lit spots and tag each
    // survivor with a darkness weight so the pick favors deep shadow / caves over
    // moonlit ground.
    private void CollectSpawnCandidates(Sim sim, SimData data, in TraversalProfile profile, Vector3 playerPos)
    {
        // Reachability flood, NOT a radius scan: only ground the player could walk
        // to qualifies, so the surface directly above a cave (close in XZ but
        // through solid rock) is excluded and can't dominate the pick.
        NavigationGoals.CollectReachableStandableCells(sim, profile, _grid, playerPos,
            data.nightSpawnMinRadius, data.nightSpawnMaxRadius, MaxWindowHalfExtent, allowFalling: false, _standable);

        _candidates.Clear();
        for (int k = 0; k < _standable.Count; k++)
        {
            Vector3 sample = _standable[k] + Vector3.Up * LightSampleHeight;
            if (BlockLightAt(sim, sample) > data.nightSpawnMaxBlockLight)
            {
                continue;
            }
            // Sun-shade falloff: weight is multiplied by SunShade01, which is 0
            // wherever a slime would burn (open-sky daytime — even a dim cloudy
            // clearing, and even a cave mouth within range), so those cells drop to
            // weight 0 and are never picked, smoothly ramping in at dawn/dusk. Same
            // exposure signal as the sunburn DoT.
            float shade = sim.SunShade01(sample);
            if (shade <= 0f)
            {
                continue;
            }
            // Darkness weight — darker preferred. Shadow-only reading (no raycast)
            // folds moonlight + block so caves outrank moonlit ground.
            float light = sim.WorldState.GetPerceivedLightWorld(sample, sunReachesPoint: false);
            float darkness = Mathf.Clamp(1f - light, 0f, 1f);
            float weight = (Mathf.Pow(darkness, data.nightSpawnDarknessBias) + 0.0001f) * shade;
            _candidates.Add(new SpawnCandidate { Pos = _standable[k], Weight = weight });
        }
    }

    // Weighted-random index into _candidates by Weight (darkness). Roulette over
    // the live pool so it stays correct as picks are removed without replacement.
    private int PickWeightedCandidate()
    {
        float total = 0f;
        for (int i = 0; i < _candidates.Count; i++)
        {
            total += _candidates[i].Weight;
        }
        float roll = _rng.Randf() * total;
        for (int i = 0; i < _candidates.Count; i++)
        {
            roll -= _candidates[i].Weight;
            if (roll <= 0f)
            {
                return i;
            }
        }
        return _candidates.Count - 1;
    }

    // Count currently-loaded, living mobs whose species is one this spawner
    // manages. Loaded mobs are all near the player, so this is effectively the
    // local population — the whole budget of the mechanic. Recomputed each sweep
    // so it self-corrects as mobs die, are killed, or evict behind the player.
    private int CountLiveNightMobs(Sim sim, SimData data)
    {
        int count = 0;
        foreach (Mob mob in sim.GetEntities<Mob>())
        {
            if (mob.alive && IsNightSpecies(data, mob))
            {
                count++;
            }
        }
        return count;
    }

    private bool IsNightSpecies(SimData data, Mob mob)
    {
        SpeciesData species = mob.SimState?.Species;
        if (species == null)
        {
            return false;
        }
        foreach (MobDescriptor descriptor in data.nightSpawnMobs)
        {
            if (descriptor?.species == species)
            {
                return true;
            }
        }
        return false;
    }

    // Peak block-light channel [0, 1+] at a world position — the torch / campfire
    // / lantern contribution only, excluding sky and moonlight. Mirrors the
    // `block` term in WorldState.GetPerceivedLightWorld. A direct lightmap lookup,
    // no raycast.
    private static float BlockLightAt(Sim sim, Vector3 pos)
    {
        int wx = Mathf.FloorToInt(pos.X);
        int wy = Mathf.FloorToInt(pos.Y);
        int wz = Mathf.FloorToInt(pos.Z);
        sim.WorldState.GetBlockLightWorld(wx, wy, wz, out int r, out int g, out int b);
        return Mathf.Max(r, Mathf.Max(g, b)) / 255f;
    }
}
