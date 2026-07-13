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
// via World.SpawnMobTransient, NOT recorded in WorldState. So they cost nothing
// to persist, vanish with their chunk when the player moves on, and never
// re-materialize: the population near the player is recomputed live each sweep
// rather than accumulated across the whole map. Each carries
// ESpawnConditions.Night so the existing off-condition cleanup fades any
// stragglers once dawn breaks (World.CleanupOffConditionMobs).
//
// Dormant (no cost) when SimData.nightSpawnMobs is empty or it isn't night.
[GlobalClass]
public partial class NightMobSpawner : Node
{
    // Candidate ground positions probed per intended spawn before giving up for
    // this cycle. Each probe is one ground raycast (the block-light check is a
    // cheap lightmap lookup); a cycle that finds no spot simply tries again next
    // interval, so this is a best-effort bound, not a guarantee.
    private const int CandidatesPerSpawn = 6;

    // Ground search ray span, centered on the candidate's Y (= player Y). Tall
    // enough to clear overhead geometry the player may be standing under.
    private const float GroundRayHeightOffset = 80f;
    private const float GroundRayDepthOffset = 80f;

    // Height above the found ground to sample block light at — roughly where the
    // mob's body sits, so a torch pooling light on the floor still repels it.
    private const float LightSampleHeight = 0.3f;

    private double _timeUntilNext;
    private readonly RandomNumberGenerator _rng = new RandomNumberGenerator();

    public override void _Ready()
    {
        _rng.Randomize();
    }

    public override void _Process(double delta)
    {
        World world = World.Current;
        SimData data = world?.SimData;
        if (world == null || data == null || data.nightSpawnMobs == null || data.nightSpawnMobs.Count == 0)
        {
            return;
        }
        Player player = world.player;
        if (player == null)
        {
            return;
        }

        double tod = world.WorldState.TimeOfDay01;
        if (!WorldState.IsNight(tod))
        {
            // Park the timer so the first post-sunset cycle waits a full interval
            // rather than firing the instant night falls.
            _timeUntilNext = data.nightSpawnIntervalSeconds;
            return;
        }

        // How deep into the night: 0 at sunset → 1 at midnight (clamped there,
        // since the clock holds at midnight until the player sleeps). Drives both
        // the population target and the spawn level.
        float t = NightProgress(tod);
        int target = ComputeTarget(data, t);
        int current = CountLiveNightMobs(world, data);
        if (current >= target)
        {
            // At/over the cap: hold the cooldown at full so the countdown only
            // ever runs while there's room. A freshly-opened slot — a kill, or
            // the target rising as midnight nears — then waits a fresh full
            // interval before the next spawn, instead of firing off an
            // already-elapsed timer sitting at zero on the cap.
            _timeUntilNext = data.nightSpawnIntervalSeconds;
            return;
        }

        _timeUntilNext -= delta;
        if (_timeUntilNext > 0.0)
        {
            return;
        }
        _timeUntilNext = data.nightSpawnIntervalSeconds;
        Spawn(world, data, player, target, current, t);
    }

    // Normalized night progress: 0 at sunset → 1 at midnight, clamped.
    private static float NightProgress(double tod)
    {
        float t = (float)((tod - WorldState.SunsetTimeOfDay01)
            / (WorldState.MidnightTimeOfDay01 - WorldState.SunsetTimeOfDay01));
        return Mathf.Clamp(t, 0f, 1f);
    }

    // Live population target for the current night progress: sunsetFraction of
    // the max at dusk → full at midnight, curved so early night stays sparse and
    // density ramps hard toward midnight.
    private int ComputeTarget(SimData data, float t)
    {
        float density = data.nightSpawnSunsetFraction
            + (1f - data.nightSpawnSunsetFraction) * Mathf.Pow(t, data.nightSpawnDensityCurve);
        return Mathf.RoundToInt(data.nightSpawnMaxPopulation * density);
    }

    // Fill toward the target this cycle, at most nightSpawnMaxPerSweep, so a
    // large deficit (nightfall, or after fast-forwarding the clock) builds up
    // over several intervals instead of a sudden wall of enemies. Each mob is
    // stamped with the current night level — 0 at dusk ramping to
    // nightSpawnMaxLevel at midnight — so later-night arrivals are tougher.
    private void Spawn(World world, SimData data, Player player, int target, int current, float t)
    {
        int toSpawn = Mathf.Min(target - current, data.nightSpawnMaxPerSweep);
        int level = Mathf.RoundToInt(t * data.nightSpawnMaxLevel);
        int spawned = 0;
        Vector3 playerPos = player.GlobalPosition;
        for (int i = 0; i < toSpawn; i++)
        {
            if (!TryFindSpawnGround(world, data, playerPos, out Vector3 pos))
            {
                continue;
            }
            MobDescriptor descriptor = data.nightSpawnMobs[_rng.RandiRange(0, data.nightSpawnMobs.Count - 1)];
            if (world.SpawnMobTransient(descriptor, pos, ESpawnConditions.Night, level) != null)
            {
                spawned++;
            }
        }

        if (CVars.nightSpawnLog.Value)
        {
            GD.Print($"[nightspawn] target={target} current={current} spawned={spawned} level={level}");
        }
    }

    // Count currently-loaded, living mobs whose species is one this spawner
    // manages. Loaded mobs are all near the player, so this is effectively the
    // local population — the whole budget of the mechanic. Recomputed each sweep
    // so it self-corrects as mobs die, are killed, or evict behind the player.
    private int CountLiveNightMobs(World world, SimData data)
    {
        int count = 0;
        foreach (Mob mob in world.GetEntities<Mob>())
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

    // Probe up to CandidatesPerSpawn random points in the spawn annulus around
    // the player, returning the first that has ground under it AND isn't lit by a
    // block light source (torch, campfire, lantern) above nightSpawnMaxBlockLight.
    // The gate is on BLOCK light only, NOT sky light — so open moonlit ground and
    // shadowed cover both qualify, while the lit circle around a fire is shunned.
    // Time of day (the night-only spawn window) handles keeping them out of
    // sunlight; there's no sun term here because the sky-light lightmap can't
    // tell sun from moon.
    private bool TryFindSpawnGround(World world, SimData data, Vector3 playerPos, out Vector3 ground)
    {
        ground = default;
        float minR = data.nightSpawnMinRadius;
        float maxR = Mathf.Max(minR, data.nightSpawnMaxRadius);
        for (int attempt = 0; attempt < CandidatesPerSpawn; attempt++)
        {
            float yaw = _rng.RandfRange(0f, Mathf.Tau);
            // Uniform across the annulus area (sqrt of a uniform between the
            // squared radii), so spawns aren't bunched at the inner ring.
            float r = Mathf.Sqrt(Mathf.Lerp(minR * minR, maxR * maxR, _rng.Randf()));
            Vector3 query2d = playerPos + new Vector3(Mathf.Cos(yaw) * r, 0f, Mathf.Sin(yaw) * r);
            if (!TryFindGround(world, query2d, out Vector3 hit))
            {
                continue;
            }
            Vector3 sample = hit + Vector3.Up * LightSampleHeight;
            if (BlockLightAt(world, sample) > data.nightSpawnMaxBlockLight)
            {
                continue;
            }
            ground = hit;
            return true;
        }
        return false;
    }

    // Peak block-light channel [0, 1+] at a world position — the torch / campfire
    // / lantern contribution only, excluding sky and moonlight. Mirrors the
    // `block` term in WorldState.GetPerceivedLightWorld. A direct lightmap lookup,
    // no raycast.
    private static float BlockLightAt(World world, Vector3 pos)
    {
        int wx = Mathf.FloorToInt(pos.X);
        int wy = Mathf.FloorToInt(pos.Y);
        int wz = Mathf.FloorToInt(pos.Z);
        world.WorldState.GetBlockLightWorld(wx, wy, wz, out int r, out int g, out int b);
        return Mathf.Max(r, Mathf.Max(g, b)) / 255f;
    }

    // Vertical raycast through a candidate XZ, returning the first Solid hit.
    // NightMobSpawner is a plain Node with no physics world of its own, so it
    // borrows World's (World extends Node3D). Mirrors WeatherLightningSpawner.
    private bool TryFindGround(World world, Vector3 query2d, out Vector3 ground)
    {
        ground = default;
        World3D world3D = world.GetWorld3D();
        if (world3D == null)
        {
            return false;
        }
        Vector3 from = query2d + new Vector3(0f, GroundRayHeightOffset, 0f);
        Vector3 to = query2d + new Vector3(0f, -GroundRayDepthOffset, 0f);
        using var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.CollisionMask = (uint)ECollisionLayer.Solid;
        query.CollideWithBodies = true;
        query.CollideWithAreas = false;
        var result = world3D.DirectSpaceState.IntersectRay(query);
        if (result.Count == 0)
        {
            return false;
        }
        ground = (Vector3)result["position"];
        return true;
    }
}
