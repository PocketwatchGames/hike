using System.Collections.Generic;
using Godot;

// Ambient daytime fairy spawner: over the course of a day it spawns up to a few
// fairies near the player — one attempt per day-period after the first — so the
// player runs into the odd fairy while exploring rather than fairies being baked
// into worldgen. The daytime sibling of NightMobSpawner; a global mechanic, not
// zone-list authoring.
//
// The stretch from sunrise to midnight is split into
// SimData.fairyDayPeriods equal blocks. Crossing into each block AFTER the first
// makes ONE spawn decision: if the player's current zone allows fairies
// (ZoneData.canSpawnFairy) a roll against that zone's ZoneData.fairySpawnChance
// decides whether a fairy actually appears. At most SimData.fairyMaxSpawnsPerDay
// spawn in a day, and once the player has killed SimData.fairyKillStopCount of them
// no more spawn until the next day. All per-day counters reset on the day rollover
// (detected by Sim.DayNumber changing).
//
// Spawns are TRANSIENT (Sim.SpawnMobTransient with ESpawnConditions.None — which
// the off-condition cleanup ignores) — like the night gellies they live only near
// the player and vanish with their chunk, never persisted. Dormant (no cost) when
// SimData has no fairySpawnDescriptor.
//
// Each fairy also has a bounded lifetime (SimData.fairyLifetimeDayFraction of a
// day). Once a fairy outlives it, ReapExpired despawns it — but only while it
// isn't currently drawn for the player, so it never blinks out on screen.
[GlobalClass]
public partial class FairySpawner : Node
{
    // Hard cap on the nav-grid window half-extent (voxels) the spawn search scans,
    // matching NightMobSpawner. Bounds the worst-case placement scan.
    private const int MaxWindowHalfExtent = 40;

    private readonly RandomNumberGenerator _rng = new RandomNumberGenerator();
    // Reused nav-grid window + standable-position buffer (no per-spawn allocation).
    private readonly WalkabilityGrid _grid = new WalkabilityGrid();
    private readonly List<Vector3> _standable = new();

    private int _lastDayNumber = int.MinValue;
    // Highest day-period index we've already made a spawn decision for. Reset to 0
    // each day so the first block (index 0) never spawns.
    private int _decidedPeriod;
    // A spawn roll succeeded but no valid spot was found yet; keep trying each frame
    // until the fairy is placed (near-player ground is almost always available at
    // once, so this normally clears the same frame it's set).
    private bool _pendingSpawn;
    // Level (→ HP) the pending fairy spawns at, locked in at decision time so a
    // retry that spills into the next day-period keeps the level it was rolled for.
    private int _pendingSpawnLevel;
    private int _spawnedToday;
    private int _killedToday;

    // Live spawned fairies paired with the GameTimeMs deadline at which their
    // lifetime lapses. Once lapsed a fairy is despawned, but only while it is not
    // currently visible to the player (checked each frame). The deadline rides the
    // sim clock (not TimeOfDay01, which clamps at the end-of-day hold), so a
    // fairy's life keeps counting down through the night rather than freezing
    // until the player sleeps. Transient fairies that vanish on their own (fled/killed/
    // chunk-unloaded) drop out as their node dies.
    private readonly List<(Mob mob, ulong expireMs)> _living = new();

    // The World whose onMobKilled we track kills through. Bound in _Ready (this
    // node is created by Sim.Initialize after Sim.Current is set) and dropped
    // in _ExitTree — no re-bind needed since a FairySpawner lives and dies with
    // its World.
    private Sim _subscribedWorld;

    public override void _Ready()
    {
        _rng.Randomize();
        _subscribedWorld = Sim.Current;
        if (_subscribedWorld != null)
        {
            _subscribedWorld.onMobKilled += OnMobKilled;
        }
    }

    public override void _ExitTree()
    {
        if (_subscribedWorld != null)
        {
            _subscribedWorld.onMobKilled -= OnMobKilled;
            _subscribedWorld = null;
        }
    }

    public override void _Process(double delta)
    {
        Sim sim = Sim.Current;
        SimData data = sim?.SimData;
        // Dormant unless a fairy is wired up (and its descriptor resolves a species).
        if (sim == null || data == null || data.fairySpawnDescriptor?.species == null)
        {
            return;
        }
        Player player = sim.player;
        if (player == null)
        {
            return;
        }

        // Reset the per-day budget on the day rollover (Sim.AdvanceToNextSunrise
        // bumps DayNumber). Also seeds _lastDayNumber on the first frame.
        if (sim.DayNumber != _lastDayNumber)
        {
            _lastDayNumber = sim.DayNumber;
            _decidedPeriod = 0;
            _pendingSpawn = false;
            _spawnedToday = 0;
            _killedToday = 0;
        }

        // Retire fairies that have outlived their lifetime (runs regardless of the
        // per-day spawn budget below).
        ReapExpired(sim);

        // Once the player has killed enough, the day's fairies are done.
        if (_killedToday >= data.fairyKillStopCount)
        {
            return;
        }

        // Which day-period are we in? Equal slices of sunrise→midnight, not of the
        // whole clock — the post-midnight hours are the dark run-out to the day's
        // end, and a daytime spawner has no business making decisions in them.
        int periods = Mathf.Max(1, data.fairyDayPeriods);
        int currentPeriod = Mathf.Clamp(
            Mathf.FloorToInt((float)(sim.WorldState.TimeOfDay01 / WorldState.MidnightTimeOfDay01) * periods),
            0, periods - 1);

        // Crossing into a new block makes one spawn decision for it. Skipped blocks
        // (fast time_scale) collapse into a single decision — fine for an ambient
        // roll, and the daily cap still bounds the total.
        if (currentPeriod > _decidedPeriod)
        {
            _decidedPeriod = currentPeriod;
            if (_spawnedToday < data.fairyMaxSpawnsPerDay)
            {
                ZoneData zone = DominantZoneData(sim, player.GlobalPosition);
                // No fairy in a zone that forbids them (skip this window entirely),
                // else roll the zone's chance.
                if (zone != null && zone.canSpawnFairy && _rng.Randf() < zone.fairySpawnChance)
                {
                    _pendingSpawn = true;
                    // Scale level with how far into the day we are: period 1 (first
                    // spawnable block) → 0, the final period → fairyMaxLevel.
                    int lastPeriod = Mathf.Max(1, periods - 1);
                    _pendingSpawnLevel = Mathf.RoundToInt(
                        (float)currentPeriod / lastPeriod * data.fairyMaxLevel);
                }
            }
        }

        if (_pendingSpawn && _spawnedToday < data.fairyMaxSpawnsPerDay)
        {
            Mob spawned = TrySpawnFairy(sim, data, player, _pendingSpawnLevel);
            if (spawned != null)
            {
                _pendingSpawn = false;
                _spawnedToday++;
                // Lifetime = a fraction of a day's worth of clock, converted to a
                // GameTimeMs deadline so it keeps ticking through the end-of-day hold.
                float dayLengthSec = Mathf.Max(1f, data.dayLengthSeconds);
                ulong lifetimeMs = (ulong)(Mathf.Max(0.001f, data.fairyLifetimeDayFraction) * dayLengthSec * 1000f);
                _living.Add((spawned, sim.GameTimeMs + lifetimeMs));
                if (CVars.fairySpawnLog.Value)
                {
                    GD.Print($"[fairyspawn] spawned day={sim.DayNumber} period={currentPeriod} " +
                        $"count={_spawnedToday}/{data.fairyMaxSpawnsPerDay} killed={_killedToday} " +
                        $"level={_pendingSpawnLevel}");
                }
            }
        }
    }

    // Find a reachable standable spot in the fairy spawn ring around the player and
    // materialize a transient fairy there. Returns the spawned Mob, or null when no
    // valid, resident ground is available (caller keeps the pending spawn and retries
    // next frame).
    private Mob TrySpawnFairy(Sim sim, SimData data, Player player, int level)
    {
        MobData mob = data.fairySpawnDescriptor.mob;
        if (mob == null)
        {
            return null;
        }
        // Reachability flood (not a radius scan) so the spawn lands on ground the
        // player could actually walk to — same placement path the night spawner uses.
        NavigationGoals.CollectReachableStandableCells(sim, new TraversalProfile(mob), _grid,
            player.GlobalPosition, data.fairySpawnMinRadius, data.fairySpawnMaxRadius,
            MaxWindowHalfExtent, allowFalling: false, _standable);
        if (_standable.Count == 0)
        {
            return null;
        }
        Vector3 pos = _standable[_rng.RandiRange(0, _standable.Count - 1)];
        return sim.SpawnMobTransient(data.fairySpawnDescriptor, pos, ESpawnConditions.None, level);
    }

    // Despawn fairies whose lifetime has lapsed, but only while they aren't being
    // drawn for the player, so one never blinks out on screen. Also prunes fairies
    // that already left on their own (fled, killed, chunk-unloaded).
    private void ReapExpired(Sim sim)
    {
        if (_living.Count == 0)
        {
            return;
        }
        ulong now = sim.GameTimeMs;
        for (int i = _living.Count - 1; i >= 0; i--)
        {
            Mob mob = _living[i].mob;
            if (mob == null || !GodotObject.IsInstanceValid(mob) || !mob.alive)
            {
                _living.RemoveAtSwap(i);
                continue;
            }
            if (now < _living[i].expireMs)
            {
                continue;
            }
            if (!mob.IsPerceivedByPlayer)
            {
                if (CVars.fairySpawnLog.Value)
                {
                    GD.Print($"[fairyspawn] lifetime despawn day={sim.DayNumber} living={_living.Count - 1}");
                }
                mob.Despawn();
                _living.RemoveAtSwap(i);
            }
        }
    }

    // The authored ZoneData of the chunk under `pos` (the dominant zone there), read
    // straight off the loaded chunk rather than the blended sample so the per-zone
    // fairy flags come through unblended. Null when no zone data is loaded there.
    private static ZoneData DominantZoneData(Sim sim, Vector3 pos)
    {
        WorldState ws = sim.WorldState;
        if (ws?.Zones == null || ws.Zones.Length == 0)
        {
            return null;
        }
        ChunkState chunk = ws.GetChunk(new Vector3I(
            Mathf.FloorToInt(pos.X / ChunkState.SIZE),
            Mathf.FloorToInt(pos.Y / ChunkState.SIZE),
            Mathf.FloorToInt(pos.Z / ChunkState.SIZE)));
        if (chunk == null)
        {
            return null;
        }
        int zi = chunk.ZoneIndex;
        return (zi >= 0 && zi < ws.Zones.Length) ? ws.Zones[zi].Data : null;
    }

    // Count a player kill of a fairy toward the day's kill-stop threshold. Matches by
    // species against the fairy descriptor; damagedByPlayer gates out non-player deaths.
    private void OnMobKilled(SpeciesData species, bool damagedByPlayer)
    {
        if (!damagedByPlayer)
        {
            return;
        }
        SpeciesData fairySpecies = Sim.Current?.SimData?.fairySpawnDescriptor?.species;
        if (fairySpecies != null && species == fairySpecies)
        {
            _killedToday++;
        }
    }
}
