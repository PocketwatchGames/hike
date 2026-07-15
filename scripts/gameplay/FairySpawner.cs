using System.Collections.Generic;
using Godot;

// Ambient daytime fairy spawner: over the course of a day it spawns up to a few
// fairies near the player — one attempt per day-period after the first — so the
// player runs into the odd fairy while exploring rather than fairies being baked
// into worldgen. The daytime sibling of NightMobSpawner; a global mechanic, not
// zone-list authoring.
//
// The day (sunrise → midnight, WorldState.TimeOfDay01 in [0,1]) is split into
// SimData.fairyDayPeriods equal blocks. Crossing into each block AFTER the first
// makes ONE spawn decision: if the player's current zone allows fairies
// (ZoneData.canSpawnFairy) a roll against that zone's ZoneData.fairySpawnChance
// decides whether a fairy actually appears. At most SimData.fairyMaxSpawnsPerDay
// spawn in a day, and once the player has killed SimData.fairyKillStopCount of them
// no more spawn until the next day. All per-day counters reset on the day rollover
// (detected by World.DayNumber changing).
//
// Spawns are TRANSIENT (World.SpawnMobTransient with ESpawnConditions.None — which
// the off-condition cleanup ignores) — like the night gellies they live only near
// the player and vanish with their chunk, never persisted. Dormant (no cost) when
// SimData has no fairySpawnDescriptor.
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

    // The GameClient whose onMobKilled we're currently subscribed to (for kill
    // tracking). Re-bound if it changes; unsubscribed on exit.
    private GameClient _subscribedClient;

    public override void _Ready()
    {
        _rng.Randomize();
    }

    public override void _ExitTree()
    {
        if (_subscribedClient != null)
        {
            _subscribedClient.onMobKilled -= OnMobKilled;
            _subscribedClient = null;
        }
    }

    public override void _Process(double delta)
    {
        World world = World.Current;
        SimData data = world?.SimData;
        // Dormant unless a fairy is wired up (and its descriptor resolves a species).
        if (world == null || data == null || data.fairySpawnDescriptor?.species == null)
        {
            return;
        }
        Player player = world.player;
        if (player == null)
        {
            return;
        }
        EnsureKillSubscription();

        // Reset the per-day budget on the day rollover (World.AdvanceToNextSunrise
        // bumps DayNumber). Also seeds _lastDayNumber on the first frame.
        if (world.DayNumber != _lastDayNumber)
        {
            _lastDayNumber = world.DayNumber;
            _decidedPeriod = 0;
            _pendingSpawn = false;
            _spawnedToday = 0;
            _killedToday = 0;
        }

        // Once the player has killed enough, the day's fairies are done.
        if (_killedToday >= data.fairyKillStopCount)
        {
            return;
        }

        // Which day-period are we in? Equal slices of [0,1]; the clock holds at 1
        // (midnight) so the final block covers up to midnight.
        int periods = Mathf.Max(1, data.fairyDayPeriods);
        int currentPeriod = Mathf.Clamp(
            Mathf.FloorToInt((float)world.WorldState.TimeOfDay01 * periods), 0, periods - 1);

        // Crossing into a new block makes one spawn decision for it. Skipped blocks
        // (fast time_scale) collapse into a single decision — fine for an ambient
        // roll, and the daily cap still bounds the total.
        if (currentPeriod > _decidedPeriod)
        {
            _decidedPeriod = currentPeriod;
            if (_spawnedToday < data.fairyMaxSpawnsPerDay)
            {
                ZoneData zone = DominantZoneData(world, player.GlobalPosition);
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
            if (TrySpawnFairy(world, data, player, _pendingSpawnLevel))
            {
                _pendingSpawn = false;
                _spawnedToday++;
                if (CVars.fairySpawnLog.Value)
                {
                    GD.Print($"[fairyspawn] spawned day={world.DayNumber} period={currentPeriod} " +
                        $"count={_spawnedToday}/{data.fairyMaxSpawnsPerDay} killed={_killedToday} " +
                        $"level={_pendingSpawnLevel}");
                }
            }
        }
    }

    // Find a reachable standable spot in the fairy spawn ring around the player and
    // materialize a transient fairy there. Returns false when no valid, resident
    // ground is available (caller keeps the pending spawn and retries next frame).
    private bool TrySpawnFairy(World world, SimData data, Player player, int level)
    {
        MobData mob = data.fairySpawnDescriptor.mob;
        if (mob == null)
        {
            return false;
        }
        // Reachability flood (not a radius scan) so the spawn lands on ground the
        // player could actually walk to — same placement path the night spawner uses.
        NavigationGoals.CollectReachableStandableCells(world, new TraversalProfile(mob), _grid,
            player.GlobalPosition, data.fairySpawnMinRadius, data.fairySpawnMaxRadius,
            MaxWindowHalfExtent, allowFalling: false, _standable);
        if (_standable.Count == 0)
        {
            return false;
        }
        Vector3 pos = _standable[_rng.RandiRange(0, _standable.Count - 1)];
        return world.SpawnMobTransient(data.fairySpawnDescriptor, pos, ESpawnConditions.None, level) != null;
    }

    // The authored ZoneData of the chunk under `pos` (the dominant zone there), read
    // straight off the loaded chunk rather than the blended sample so the per-zone
    // fairy flags come through unblended. Null when no zone data is loaded there.
    private static ZoneData DominantZoneData(World world, Vector3 pos)
    {
        WorldState ws = world.WorldState;
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

    // Kill tracking rides GameClient.onMobKilled (fired from Mob.Die). GameClient may
    // not exist yet when this node is created, so bind lazily and re-bind if it swaps.
    private void EnsureKillSubscription()
    {
        GameClient gc = GameClient.Current;
        if (gc == _subscribedClient)
        {
            return;
        }
        if (_subscribedClient != null)
        {
            _subscribedClient.onMobKilled -= OnMobKilled;
        }
        _subscribedClient = gc;
        if (gc != null)
        {
            gc.onMobKilled += OnMobKilled;
        }
    }

    // Count a player kill of a fairy toward the day's kill-stop threshold. Matches by
    // species against the fairy descriptor; damagedByPlayer gates out non-player deaths.
    private void OnMobKilled(SpeciesData species, bool damagedByPlayer)
    {
        if (!damagedByPlayer)
        {
            return;
        }
        SpeciesData fairySpecies = World.Current?.SimData?.fairySpawnDescriptor?.species;
        if (fairySpecies != null && species == fairySpecies)
        {
            _killedToday++;
        }
    }
}
