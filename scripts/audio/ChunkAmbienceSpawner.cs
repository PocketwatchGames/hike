using System;
using System.Collections.Generic;
using Godot;

// Per-chunk positional ambience emitters — birds in trees, frogs by
// water, crickets in grass, drips in caves. On chunk load this walks
// the chunk's zone's PositionalEmitterData palette, deterministically
// rolls instance positions from a chunk-coord-seeded RNG, and attaches
// AudioStreamPlayer3D children to itself at those positions. On chunk
// unload it tears them down.
//
// Determinism: the same chunk coord + emitter index always produces
// the same placement, so reloading a zone from disk doesn't shuffle
// the soundscape. The seed RNG is seeded from
//   coord.X * a ^ coord.Y * b ^ coord.Z * c ^ emitter.seed
// so two emitters in the same chunk don't pick the same cells.
//
// Streaming bound: every spawned player has a per-stream MaxDistance.
// Beyond that, Godot still streams + mixes the player at -inf dB. To
// avoid the decode cost on far chunks (player can be many chunks from
// the audio source on the streaming radius edge), we pause streams
// whose chunk center is farther than maxDistance + EARSHOT_SLACK from
// the listener and unpause when the listener returns.
//
// AmbSpot_* sources (campfire, well, hermit, …) are NOT this spawner's
// responsibility — those attach to authored interactive entities,
// where the placement is intentional rather than rule-driven.
[GlobalClass]
public partial class ChunkAmbienceSpawner : Node3D
{
    public static ChunkAmbienceSpawner Current { get; private set; }

    // Tracking record for one spawned emitter so we can pause/unpause
    // by listener distance without re-walking the player tree each frame.
    private struct EmitterInstance
    {
        public AudioStreamPlayer3D Player;
        public PositionalEmitterData Data;
        public Vector3 WorldPos;
    }

    // Headroom past an emitter's MaxDistance before we pause its stream.
    // Prevents thrashing pause/unpause on listeners hovering near the
    // attenuation edge — a slight hysteresis.
    private const float EARSHOT_SLACK = 4f;

    // Listener-position update interval for the pause/unpause sweep.
    // Cheaper than per-frame; emitters fade in/out smoothly via
    // attenuation so a 200ms gap is invisible.
    private const float SWEEP_INTERVAL_SEC = 0.2f;

    // Hash-mix constants for the per-chunk + per-emitter seed.
    private const long HASH_MIX_X = 73856093;
    private const long HASH_MIX_Y = 19349663;
    private const long HASH_MIX_Z = 83492791;

    private readonly Dictionary<Vector3I, List<EmitterInstance>> _byChunk = new();
    private double _sweepAccumSec;

    public override void _Ready()
    {
        Current = this;
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    // Called by World once its ChunkManager events are wired and the
    // WorldState is ready. Late-binding rather than _Ready because
    // World.ChunkManager isn't constructed until World.Initialize runs.
    public void Bind(World world)
    {
        if (world == null || world.ChunkManager == null) { return; }
        world.ChunkManager.onChunkLoaded += OnChunkLoaded;
        world.ChunkManager.onChunkUnloaded += OnChunkUnloaded;
    }

    public override void _Process(double delta)
    {
        _sweepAccumSec += delta;
        if (_sweepAccumSec < SWEEP_INTERVAL_SEC) { return; }
        _sweepAccumSec = 0.0;

        World w = World.Current;
        if (w == null || w.player == null) { return; }
        Vector3 listenerPos = w.player.GlobalPosition;
        float tod = (float)(w.WorldState?.TimeOfDay01 ?? 0.5);

        SweepEarshotAndTod(listenerPos, tod);
    }

    private void OnChunkLoaded(Vector3I coord)
    {
        World w = World.Current;
        WorldState ws = w?.WorldState;
        if (ws == null) { return; }
        ChunkState chunk = ws.GetChunk(coord);
        if (chunk == null) { return; }

        ZoneState[] zones = ws.Zones;
        if (zones == null || chunk.ZoneIndex >= zones.Length) { return; }
        ZoneAmbienceData ambience = zones[chunk.ZoneIndex].Data?.ambience;
        if (ambience == null || ambience.positionalEmitters == null) { return; }

        var instances = new List<EmitterInstance>();
        for (int e = 0; e < ambience.positionalEmitters.Length; e++)
        {
            PositionalEmitterData data = ambience.positionalEmitters[e];
            if (data == null || data.stream == null || data.instancesPerChunk <= 0) { continue; }
            SpawnEmitterInChunk(chunk, coord, data, e, instances);
        }

        if (instances.Count > 0)
        {
            _byChunk[coord] = instances;
        }
    }

    private void OnChunkUnloaded(Vector3I coord)
    {
        if (!_byChunk.TryGetValue(coord, out List<EmitterInstance> list)) { return; }
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Player.QueueFree();
        }
        _byChunk.Remove(coord);
    }

    private void SpawnEmitterInChunk(ChunkState chunk, Vector3I coord, PositionalEmitterData data, int emitterIndex, List<EmitterInstance> outList)
    {
        // Find candidate cells in this chunk that match the spawn rules.
        // Worst case 4096 reads per emitter; chunks load at <1Hz under
        // typical movement, so this isn't on a hot path.
        var candidates = new List<Vector3I>();
        for (int lx = 0; lx < ChunkState.SIZE; lx++)
        {
            for (int ly = 0; ly < ChunkState.SIZE; ly++)
            {
                for (int lz = 0; lz < ChunkState.SIZE; lz++)
                {
                    if (!QualifiesForSpawn(chunk, lx, ly, lz, data)) { continue; }
                    candidates.Add(new Vector3I(lx, ly, lz));
                }
            }
        }
        if (candidates.Count == 0) { return; }

        // Deterministic per-(chunk, emitter) RNG. The same chunk coord
        // + same emitter always picks the same cells, so a save/reload
        // doesn't shuffle the soundscape.
        long seed = (coord.X * HASH_MIX_X) ^ (coord.Y * HASH_MIX_Y) ^ (coord.Z * HASH_MIX_Z) ^ data.seed ^ (emitterIndex * 0x9E3779B1L);
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)seed;

        int picks = data.instancesPerChunk;
        if (picks > candidates.Count) { picks = candidates.Count; }

        for (int i = 0; i < picks; i++)
        {
            int idx = (int)(rng.Randi() % (uint)candidates.Count);
            Vector3I local = candidates[idx];
            // Sample with replacement — simpler than maintaining a
            // shuffled deck, and double-picks just produce overlapping
            // sources at the same cell which is acoustically harmless.

            Vector3 worldPos = new Vector3(
                coord.X * ChunkState.SIZE + local.X + 0.5f,
                coord.Y * ChunkState.SIZE + local.Y + data.yOffset,
                coord.Z * ChunkState.SIZE + local.Z + 0.5f);

            var player = new AudioStreamPlayer3D();
            player.Stream = data.stream;
            player.Bus = !string.IsNullOrEmpty(data.bus) ? data.bus : "World3D";
            player.MaxDistance = data.maxDistance;
            player.VolumeDb = data.volumeDb;
            player.Position = worldPos;
            // Start paused; the sweep will unpause when in earshot AND
            // the TOD curve is non-zero. Calling Play() first warms the
            // decoder so the first audible frame doesn't pop.
            AddChild(player);
            player.Play();
            player.StreamPaused = true;

            outList.Add(new EmitterInstance
            {
                Player = player,
                Data = data,
                WorldPos = worldPos,
            });
        }
    }

    private static bool QualifiesForSpawn(ChunkState chunk, int lx, int ly, int lz, PositionalEmitterData data)
    {
        VoxelType v = chunk.GetVoxel(lx, ly, lz);
        if (data.spawnVoxelType != VoxelType.Air && v != data.spawnVoxelType) { return false; }
        if (data.spawnDetailGroupId != 0 && chunk.GetDetailGroup(lx, ly, lz) != data.spawnDetailGroupId) { return false; }
        if (data.requiresAirAbove)
        {
            VoxelType above = ly < ChunkState.SIZE - 1 ? chunk.GetVoxel(lx, ly + 1, lz) : VoxelType.Air;
            if (above != VoxelType.Air) { return false; }
        }
        if (data.requiresAdjacentSolid)
        {
            if (!HasAdjacentSolid(chunk, lx, ly, lz)) { return false; }
        }
        return true;
    }

    private static bool HasAdjacentSolid(ChunkState chunk, int lx, int ly, int lz)
    {
        // Within-chunk only — cross-chunk neighbor checks would need
        // WorldState lookups during chunk load, which races with chunk
        // streaming. The accuracy loss at chunk borders is acceptable
        // for ambience (an extra silent or extra spawned emitter at
        // the seam is inaudible).
        return IsLand(chunk.GetVoxel(lx + 1, ly, lz))
            || IsLand(chunk.GetVoxel(lx - 1, ly, lz))
            || IsLand(chunk.GetVoxel(lx, ly, lz + 1))
            || IsLand(chunk.GetVoxel(lx, ly, lz - 1));
    }

    private static bool IsLand(VoxelType v)
    {
        return v != VoxelType.Air && v != VoxelType.Water;
    }

    private void SweepEarshotAndTod(Vector3 listenerPos, float tod)
    {
        foreach (var kv in _byChunk)
        {
            List<EmitterInstance> list = kv.Value;
            for (int i = 0; i < list.Count; i++)
            {
                EmitterInstance inst = list[i];
                float distSq = (inst.WorldPos - listenerPos).LengthSquared();
                float earshot = inst.Data.maxDistance + EARSHOT_SLACK;
                bool inEarshot = distSq <= earshot * earshot;

                float todAmp = inst.Data.timeOfDayVolume != null
                    ? inst.Data.timeOfDayVolume.Sample(tod)
                    : 1f;
                bool todActive = todAmp > 0.001f;

                bool shouldPlay = inEarshot && todActive;
                if (inst.Player.StreamPaused == shouldPlay)
                {
                    inst.Player.StreamPaused = !shouldPlay;
                }

                // Live TOD volume in dB on top of the authored volumeDb.
                // Skipped when paused — saves the LinearToDb call.
                if (shouldPlay)
                {
                    float ampLin = todAmp;
                    if (ampLin < 0.001f) { ampLin = 0.001f; }
                    inst.Player.VolumeDb = inst.Data.volumeDb + Mathf.LinearToDb(ampLin);
                }
            }
        }
    }
}
