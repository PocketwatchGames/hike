using System.Collections.Generic;
using Godot;

// World — spawn-condition gating and the matching presence lifecycle: whether
// a gated entity may materialize now (SpawnConditionsMet), spawning night-only
// entities at the sunset edge (RefreshTimeOfDayEntities), and despawning
// off-condition mobs once the encounter goes cold (CleanupOffConditionMobs).
// See World.cs for the file split.
public partial class World
{
    // Tracks the previous night state so Tick can detect the moment tod
    // crosses sunset and spawn SpawnAtNight entities on already-active
    // chunks. Without this, night-only goblins / chests stay missing on
    // chunks that loaded during the day until the player walks far enough
    // to evict and reload them. Only the sunset edge matters — sunrise
    // does not despawn anything; existing night mobs ride out daytime
    // until their chunk evicts.
    private bool _wasNight;
    // Time since the last off-condition mob cleanup sweep (see Tick /
    // CleanupOffConditionMobs). Throttles the per-mob walk to once per
    // SimData.SpawnCleanupIntervalSeconds.
    private float _spawnCleanupAccumulator;
    // Reused scratch list so the cleanup sweep doesn't allocate each interval.
    private readonly List<Mob> _cleanupScratch = new();

    // Current rain intensity from the live blended weather, or 0 when no
    // SkyController is up (editor / headless). Used by the spawn gate.
    public float CurrentRainAmount()
    {
        return SkyController.Current?.Weather?.rainAmount ?? 0f;
    }

    // Current ambient fog amount [0,1] from the live blended weather + time of
    // day (DerivedPalette.Fog), or 0 with no SkyController (editor / headless).
    // This is the SAME diurnal signal the renderer scales the volumetric
    // fog_map by, so perception's fog obscurant tracks the VISIBLE fog: ~0
    // across the warm midday plateau (fog burns off as the day warms), high at
    // cool dawn / night. The static per-voxel field (WorldState.GetFogWorld /
    // PlayerPerception.FogFraction) only says WHERE fog pools; multiplying it
    // by this says HOW MUCH is actually present right now. Deliberately the
    // pre-light-dimming Fog, not the render's FogDensity — night fog reads
    // thick on screen via contrast even though its density is dialed down by
    // fogPhaseScale, and perception should match what the player sees.
    public float CurrentFogAmount()
    {
        return SkyController.Current?.Palette.Fog ?? 0f;
    }

    // True iff every circumstance required by `conditions` currently holds, so
    // a gated mob/chest may materialize. None always passes. See ESpawnConditions.
    public bool SpawnConditionsMet(ESpawnConditions conditions)
    {
        if (conditions == ESpawnConditions.None)
        {
            return true;
        }
        bool night = WorldState.IsNight(_worldState.TimeOfDay01);
        if (conditions.HasFlag(ESpawnConditions.Day) && night)
        {
            return false;
        }
        if (conditions.HasFlag(ESpawnConditions.Night) && !night)
        {
            return false;
        }
        if (conditions.HasFlag(ESpawnConditions.Clear) && CurrentRainAmount() >= SimData.RainSpawnThreshold)
        {
            return false;
        }
        if (conditions.HasFlag(ESpawnConditions.NotHeavyRain) && CurrentRainAmount() >= SimData.HeavyRainSpawnThreshold)
        {
            return false;
        }
        return true;
    }

    // Despawns loaded mobs whose ESpawnConditions no longer hold (a night
    // goblin caught past dawn, a clear-day sparrow once rain starts), but only
    // when the encounter is "cold": the mob is far from the player, the player
    // has lost track of it (DiscoveryState back to Hidden), and the mob isn't
    // aware of / hunting the player. This is a presence gate that complements
    // the spawn gate — a goblin caught out at dawn keeps hunting as long as it
    // can see the player or the player can see it, and only quietly vanishes
    // once everyone has disengaged and walked away. The MobSimState persists in
    // WorldState, so the mob respawns naturally when its conditions return and
    // its chunk is active (same path as RefreshTimeOfDayEntities). Despawn is
    // identical to a chunk eviction: QueueFree → TreeExiting syncs the node
    // back to its sim state. Called from Tick on an interval.
    private void CleanupOffConditionMobs()
    {
        if (_player == null)
        {
            return;
        }
        using var _prof = Profiler.Sample("World.CleanupOffConditionMobs");
        float distance = _worldState.SimData?.SpawnCleanupDistance ?? 50f;
        float distanceSq = distance * distance;
        Vector3 playerPos = _player.GlobalPosition;

        // Collect first, mutate after — RemoveEntity edits the lists that
        // GetEntities<Mob> walks.
        _cleanupScratch.Clear();
        foreach (Mob mob in GetEntities<Mob>())
        {
            // Unconditional spawns and corpses are never cleaned up on this
            // account; corpses have their own lifecycle (loot, chunk eviction).
            if (!mob.alive || mob.spawnConditions == ESpawnConditions.None)
            {
                continue;
            }
            // Conditions still hold — the mob legitimately belongs here.
            if (SpawnConditionsMet(mob.spawnConditions))
            {
                continue;
            }
            // Player still knows about it (Discovered with live memory, or
            // mid-Detected) — let memory lapse before we touch it.
            if (mob.playerPerceptionState != EPlayerPerceptionState.Hidden)
            {
                continue;
            }
            // Mob is aware of the player (alerted or investigating) — a goblin
            // mid-hunt doesn't blink out at dawn.
            if (mob.triggered || mob.investigation != null)
            {
                continue;
            }
            // Close enough that despawning could be seen.
            if ((mob.GlobalPosition - playerPos).LengthSquared() < distanceSq)
            {
                continue;
            }
            _cleanupScratch.Add(mob);
        }

        for (int i = 0; i < _cleanupScratch.Count; i++)
        {
            Mob mob = _cleanupScratch[i];
            RemoveEntity(mob);
            mob.QueueFree();
        }
        _cleanupScratch.Clear();
    }

    // Walks active chunks and spawns any night-only entities whose chunk is
    // active when night begins. The reverse direction — despawning entities
    // whose conditions lapsed — is handled separately by CleanupOffConditionMobs,
    // which removes a SpawnAtNight mob only once the player is far and unaware;
    // a goblin caught out at dawn keeps hunting until then. Non-night entities
    // override ShouldSpawn => true unconditionally and are unaffected. Called
    // from Tick on day↔night transitions.
    private void RefreshTimeOfDayEntities()
    {
        // Don't let a gated mob materialize right on top of the player when
        // night falls — these chunks are already active, so without a distance
        // gate a goblin can appear a couple meters away the instant tod crosses
        // sunset. A skipped mob keeps its sim state and spawns later via the
        // normal chunk-load path (which streams in far away) once the player
        // moves off, or at the next nightfall. 0 disables the gate.
        float minDistance = _worldState.SimData?.SpawnMinDistanceFromPlayer ?? 0f;
        float minDistanceSq = minDistance * minDistance;
        bool gateOnDistance = _player != null && minDistanceSq > 0f;
        Vector3 playerPos = _player?.GlobalPosition ?? Vector3.Zero;

        foreach (var pair in _activeEntities)
        {
            List<EntitySimState> states = _worldState.GetEntities(pair.Key);
            if (states == null)
            {
                continue;
            }
            List<Node3D> nodes = pair.Value;
            foreach (EntitySimState state in states)
            {
                if (state.RuntimeNode != null)
                {
                    continue;
                }
                if (!state.ShouldSpawn(this))
                {
                    continue;
                }
                if (gateOnDistance && (state.WorldPosition - playerPos).LengthSquared() < minDistanceSq)
                {
                    continue;
                }
                Node3D entity = state.CreateEntity(this);
                if (entity != null)
                {
                    RegisterEntity(entity, nodes, state);
                }
            }
        }
    }
}
