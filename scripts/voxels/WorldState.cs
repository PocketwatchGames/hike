using System;
using System.Collections.Generic;
using Godot;

public class WorldState
{
    public readonly Vector3I Min;
    public readonly Vector3I Max;
    public SimData SimData;

    // Zones present in this world. Populated by WorldGen (or the disk
    // loader) at world creation; each ChunkState.ZoneIndex picks one of
    // these. ZoneBlend.Sample reads this array to produce the player's
    // current blended zone/weather, so any change here is visible at
    // the next frame. Empty array means "no zones authored" — ZoneBlend
    // falls back to defaults.
    public ZoneState[] Zones = [];

    // Named regions present in this world. Independent from Zones —
    // populated separately by WorldGen / the disk loader; each
    // ChunkState.RegionIndex picks one of these. GameClient.UpdateRegion
    // and WorldMapScreen read this; Data == null means a border chunk
    // (no named region here, see GameClient hysteresis rules). Empty
    // array means "no regions authored" — region tracking is silently
    // off in that world.
    public RegionState[] Regions = [];

    // Default spawn point baked into the world. Set by the loader (from the
    // world file header) or by Main when starting a procedurally-generated
    // game. The packed world file persists this so a save can recreate the
    // intended starting position.
    public Vector3 Spawn;

    // World-scope simulation state that isn't per-chunk and isn't a per-
    // entity property — discovered regions today, quest progress and world
    // flags later. Lives here so the save layer can serialize one cohesive
    // bag of player-progression state alongside the chunk delta layer.
    public WorldSimState SimState = new();

    // Persistent simulation clock in milliseconds. Advanced by World.Tick while
    // unpaused; serialized with the rest of the world state so cooldowns,
    // AI timers, etc. survive save/load.
    public ulong GameTimeMs;

    // Normalized time-of-day in [0, 1): 0 = midnight, 0.25 = sunrise,
    // 0.5 = noon, 0.75 = sunset. Advanced by World.Tick scaled by
    // SimData.DayLengthSeconds and the time_scale CVar. SkyController reads
    // this each frame to compute sun/moon orbit and blend day/sunset/night
    // colors. Seeded from SimData.InitialTimeOfDay at world creation.
    public double TimeOfDay01;

    // Single shared night threshold so SpawnAtNight gating, the day/night
    // refresh in World, and ad-hoc isNight checks scattered across entities
    // all agree on when night begins and ends.
    public static bool IsNight(double timeOfDay01) => timeOfDay01 < 0.25 || timeOfDay01 >= 0.75;

    // Monotonic absolute-day counter advanced in lockstep with
    // TimeOfDay01 (= TimeOfDay01 + integer day index). Anything that
    // needs an unwrapping time coordinate aligned with the day cycle —
    // most importantly the WeatherSimulation variance phase, whose
    // handover boundaries are at TimeOfDayAbsolute = N/2 + 0.25 (i.e.
    // sunrise / sunset of each successive day) — should read this
    // rather than deriving from GameTimeMs. (GameTimeMs runs at real
    // time, while TimeOfDay is on the time_scale clock; deriving day
    // fractions from GameTimeMs causes the variance windows to drift
    // out of phase with the lighting transitions and lights pop when
    // a variance handover happens to coincide with sunrise/sunset.)
    public double TimeOfDayAbsolute;

    // Sun direction (unit vector, the direction light travels). Written by
    // SkyController each frame from TimeOfDay01; read by
    // World.IsPointInDirectionalSun for the gameplay shadow-reach raycast.
    // During night this holds the moon's light direction (the primary source
    // at night) so shadow-reach queries still make sense.
    public Vector3 ShadowLightDirection = new Vector3(-0.215f, -0.819f, -0.532f).Normalized();

    // Prevailing wind direction in world XZ. Lives here rather than on
    // SimData because it's a MUTABLE sim property — a future weather
    // system will rotate this as storms move in, and it'll want to be
    // serialized with the rest of the world state so reloading a save
    // doesn't reset the weather pattern. WeatherData tunes the strength
    // and rhythm of wind per preset (amplitude, frequency, gusts);
    // direction is orthogonal to that. Y component unused — wind
    // treated as horizontal.
    public Vector3 WindDirection = new Vector3(0.7f, 0f, 0.7f);

    // 12-hour weather variance, in [0, 1]. 0 = stormy / unstable
    // (cool), 1 = fair / stable (warm). Drives the temperature swing
    // around the diurnal baseline, and (via its analytical slope
    // across the sunrise/sunset crossfade) the wind transient.
    // Humidity and cloud cover use their OWN independent variance
    // channels (below) so a humid front can roll in without dragging
    // temperature with it.
    //
    // Each channel has prev / cur / next + a HANDOVER PHASE INDEX.
    // Phase increments at the start of each sunrise/sunset crossfade
    // window; when it does, prev := cur, cur := next, next := fresh
    // roll. Inside the window, the displayed value smooth-steps
    // prev → cur; outside, it sits at cur until the next window. The
    // pre-rolled `next` holds the variance for the UPCOMING phase so
    // the HUD weather icon can preview tomorrow's day / tonight's
    // night peak before its own handover lands. Lives on WorldState
    // so a save/reload resumes the same forecast rather than snapping.
    public float WeatherVariance = 0.5f;
    public float WeatherVariancePrev = 0.5f;
    public float WeatherVarianceCur = 0.5f;
    public float WeatherVarianceNext = 0.5f;
    // Per-day-fraction analytical slope of WeatherVariance. 0 outside
    // the crossfade window; ramps with SmoothStep' inside. Wind /
    // temperature transient calcs read this directly so the signal is
    // independent of frame rate and survives time_scale changes.
    public float WeatherVarianceSlope = 0.0f;
    // Most recently completed handover phase index. long.MinValue means
    // "uninitialized" — first UpdateVariance call snaps this to the
    // current phase without rolling, so the freshly seeded prev/cur/next
    // triple isn't promoted away on frame 0.
    public long WeatherVariancePhase = long.MinValue;

    // Independent humidity-variance channel. Same handover-phase
    // structure as WeatherVariance, but its effect on simulated
    // humidity is GATED BY SIMULATED WIND SPEED — calm air holds the
    // regional baseline; rising wind blows the neighboring humidity
    // pattern in.
    public float HumidityVariance = 0.5f;
    public float HumidityVariancePrev = 0.5f;
    public float HumidityVarianceCur = 0.5f;
    public float HumidityVarianceNext = 0.5f;
    public float HumidityVarianceSlope = 0.0f;
    public long HumidityVariancePhase = long.MinValue;

    // Independent cloud-cover variance channel. Same gating rule as
    // humidity — clouds are physically advected by the wind, so on a
    // calm day the regional baseline holds and a strong wind pulls in
    // whatever the variance says is "out there".
    public float CloudVariance = 0.5f;
    public float CloudVariancePrev = 0.5f;
    public float CloudVarianceCur = 0.5f;
    public float CloudVarianceNext = 0.5f;
    public float CloudVarianceSlope = 0.0f;
    public long CloudVariancePhase = long.MinValue;

    // Independent lightning variance channel. Same handover-phase
    // structure as the other variance channels. Multiplies the zone's
    // authored lightningAmount (zone max) AFTER the storm gate (heavy
    // cloud + active rain), so it expresses "this storm is electric"
    // vs "this storm is just wet" — two adjacent rainy phases can have
    // very different lightning levels. Not wind-gated: unlike humidity
    // and cloud (which model advection), lightning is generated in
    // place by the storm itself, so its variance reads through directly.
    public float LightningVariance = 0.5f;
    public float LightningVariancePrev = 0.5f;
    public float LightningVarianceCur = 0.5f;
    public float LightningVarianceNext = 0.5f;
    public float LightningVarianceSlope = 0.0f;
    public long LightningVariancePhase = long.MinValue;

    // Lingering surface wetness in [0, 1]. Integrates rainAmount, derived
    // fog, and humidity (per-second gains in SimData) and decays with an
    // exponential half-life so surfaces stay visibly wet for a few minutes
    // after the rain stops. SkyController advances this each frame and
    // pushes it to the `wetness_level` shader global, which the voxel
    // terrain shader uses to blend a specular highlight + slight albedo
    // darkening onto sky-exposed, upward-facing faces.
    public float WetnessLevel = 0f;

    // Per-world deterministic RNG for weather rolls. Seeded so reloads
    // produce the same forecast.
    public RandomNumberGenerator WeatherRng = new RandomNumberGenerator();

    public readonly Dictionary<Vector3I, ChunkState> _chunks = new();
    public readonly Dictionary<Vector3I, List<EntitySimState>> _entities = new();

    // Active block-light sources. Each entry contributes additively to the
    // BlockLight channel via its cached footprint. Added/removed through
    // LightEngine.AddLightSource / RemoveLightSource.
    public readonly List<LightSource> LightSources = new();

    // Set of chunk coords whose stored sunlight or block-light arrays have
    // been written since the last LightMap upload. ChunkManager drains this
    // after each light operation so the GPU upload only re-encodes touched
    // chunks. Populated automatically by SetSunlightWorld / AddBlockLightWorld
    // / SubtractBlockLightWorld — callers don't need to remember.
    public readonly HashSet<Vector3I> LightChunkDirty = new();

    // Same pattern for FogMap. Populated automatically by SetFogWorld.
    // Currently only worldgen writes fog, so this only trips if something
    // mutates fog at runtime (e.g. a weather CVar or future fog emitter).
    public readonly HashSet<Vector3I> FogChunkDirty = new();

    // Same pattern for the SkyExposureMap GPU texture. Populated automatically
    // by SetSkyExposureWorld — the initial ComputeSunlight floods it (drained
    // harmlessly on the first frame after the map's constructor encodes every
    // chunk), and runtime voxel edits re-mark only the recomputed columns.
    public readonly HashSet<Vector3I> SkyExposureChunkDirty = new();

    // Per-chunk sun-attenuation field from foliage clusters. Sparse — only
    // chunks containing or shadowed by tree canopies allocate a 16³ byte
    // array. Per voxel: 0 = no foliage attenuation, 255 = saturated. Read
    // by LightEngine's sunlight passes as an extra falloff term (analogous
    // to the per-voxel fog falloff). Populated by FoliageStamper at world
    // creation (WorldGen + post-disk-load). NOT serialized — its effect is
    // baked into the persisted Sunlight bytes; the canopy field is rebuilt
    // on world load so OnVoxelsChanged re-propagation still sees foliage.
    public readonly Dictionary<Vector3I, byte[,,]> CanopyAttenuation = new();

    // Same pattern for WaterCurrentMap. Populated by anything that mutates
    // water-current vectors at runtime; ChunkManager drains it each frame
    // to push only touched chunks back to the GPU.
    public readonly HashSet<Vector3I> WaterCurrentChunkDirty = new();

    // Same pattern for WindMap. Used for both per-cell wind velocity (RGB
    // channels) and WindFactor (alpha channel) writes — they share a
    // texture, so a change to either marks the chunk for re-encode.
    public readonly HashSet<Vector3I> WindChunkDirty = new();

    public WorldState(Vector3I min, Vector3I max, SimData simData)
    {
        Min = min;
        Max = max;
        SimData = simData;
        // Seed the scripting-variable bank from the authored registry before
        // any save data loads; harmless when no registry is authored.
        SimState.ScriptVars.Initialize(simData?.ScriptVariables);
        TimeOfDay01 = simData?.InitialTimeOfDay ?? 0.3f;
        TimeOfDayAbsolute = TimeOfDay01;
        WeatherRng.Randomize();
        // Seed prev/cur/next on each channel so the first frame has a
        // valid lerp pair AND a pre-rolled upcoming-phase value for the
        // HUD forecast. The phase fields stay at long.MinValue —
        // UpdateVariance snaps each channel's phase to "current" on
        // first call without rolling, so these initial values aren't
        // promoted away.
        WeatherVariancePrev = WeatherRng.Randf();
        WeatherVarianceCur = WeatherRng.Randf();
        WeatherVarianceNext = WeatherRng.Randf();
        WeatherVariance = WeatherVarianceCur;
        HumidityVariancePrev = WeatherRng.Randf();
        HumidityVarianceCur = WeatherRng.Randf();
        HumidityVarianceNext = WeatherRng.Randf();
        HumidityVariance = HumidityVarianceCur;
        CloudVariancePrev = WeatherRng.Randf();
        CloudVarianceCur = WeatherRng.Randf();
        CloudVarianceNext = WeatherRng.Randf();
        CloudVariance = CloudVarianceCur;
        LightningVariancePrev = WeatherRng.Randf();
        LightningVarianceCur = WeatherRng.Randf();
        LightningVarianceNext = WeatherRng.Randf();
        LightningVariance = LightningVarianceCur;
    }

    // World-coordinate accessors for cross-chunk light propagation

    private static Vector3I WorldToChunkCoord(int wx, int wy, int wz)
    {
        return new Vector3I(
            (int)Math.Floor((double)wx / ChunkState.SIZE),
            (int)Math.Floor((double)wy / ChunkState.SIZE),
            (int)Math.Floor((double)wz / ChunkState.SIZE)
        );
    }

    private static int Mod(int a, int m)
    {
        return ((a % m) + m) % m;
    }

    public bool IsInBounds(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        return _chunks.ContainsKey(cc);
    }

    public VoxelType GetVoxelWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return VoxelType.Air;
        }
        return chunk.Voxels[Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE)];
    }

    // Baked wind velocity (world m/s) at a world voxel, decoded from the
    // owning chunk's coarse wind subgrid and scaled to world units. Returns
    // Vector3.Zero when that chunk isn't resident. Used by flying mobs so
    // spatially-varying air currents (mountain passes, sheltered hollows)
    // push them around — the same field that drives sprite sway and water.
    public Vector3 GetWindVelocityWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return Vector3.Zero;
        }
        int sx = Mod(wx, ChunkState.SIZE) / ChunkState.ENV_VOXELS_PER_CELL;
        int sy = Mod(wy, ChunkState.SIZE) / ChunkState.ENV_VOXELS_PER_CELL;
        int sz = Mod(wz, ChunkState.SIZE) / ChunkState.ENV_VOXELS_PER_CELL;
        return chunk.GetWindVelocity(sx, sy, sz) * WindGen.WIND_VELOCITY_SCALE;
    }

    public VoxelTypeInfo.SharpAxes GetShapeWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return VoxelTypeInfo.SharpAxes.None;
        }
        return chunk.GetShape(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetShapeWorld(int wx, int wy, int wz, VoxelTypeInfo.SharpAxes shape)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetShape(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), shape);
    }

    public int GetTerrainIdWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetTerrainId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetTerrainIdWorld(int wx, int wy, int wz, int TerrainId)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetTerrainId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), TerrainId);
    }

    public int GetOverlayIdWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetOverlayId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetOverlayIdWorld(int wx, int wy, int wz, int overlayId)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetOverlayId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), overlayId);
    }

    public int GetDetailGroupWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetDetailGroup(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetDetailGroupWorld(int wx, int wy, int wz, int groupId)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetDetailGroup(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), groupId);
    }

    public int GetDetailStrengthWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetDetailStrength(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetDetailStrengthWorld(int wx, int wy, int wz, int strength)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetDetailStrength(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), strength);
    }

    public int GetSunlightWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetSunlight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetSunlightWorld(int wx, int wy, int wz, int level)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetSunlight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), level);
        LightChunkDirty.Add(cc);
    }

    // Zero every resident chunk's Sunlight bytes so a fresh ComputeSunlight
    // pass starts from a known baseline. Required because the column scan
    // breaks when sunLevel reaches zero — without a reset, voxels below the
    // break keep whatever a previous propagation pass set them to. Marks
    // every chunk dirty so the GPU upload reflects the fresh propagation.
    public void ClearSunlightAll()
    {
        foreach (var kvp in _chunks)
        {
            Array.Clear(kvp.Value.Sunlight, 0, kvp.Value.Sunlight.Length);
            LightChunkDirty.Add(kvp.Key);
        }
    }

    // Same baseline reset for the vertical SkyExposure field. The column scan
    // only writes voxels above the first occluder, so everything below an
    // opaque break must start at 0 (= fully sheltered) for the field to read
    // correctly. Called by ComputeSunlight before its column pass.
    public void ClearSkyExposureAll()
    {
        foreach (var kvp in _chunks)
        {
            Array.Clear(kvp.Value.SkyExposure, 0, kvp.Value.SkyExposure.Length);
        }
    }

    public int GetSkyExposureWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetSkyExposure(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetSkyExposureWorld(int wx, int wy, int wz, int level)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetSkyExposure(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), level);
        SkyExposureChunkDirty.Add(cc);
    }

    // BFS sky-light at `worldPos`, normalized to [0, 1]. This is the LIGHTING
    // signal — max(vertical column, horizontal BFS spread) — so it leaks
    // sideways (light bends into a cave mouth). Correct for "how lit is this
    // point" (sun masking, sprite lighting, rain-drop shading) but WRONG for
    // "is there cover overhead", because the leak reads bright one ledge into
    // shelter. Cover/shelter probes (rain wetness, NoCeilingRequirement, rain
    // splash spawning) must use GetSkyExposure01 instead.
    public float GetSkyLight01(Godot.Vector3 worldPos)
    {
        int wx = Mathf.FloorToInt(worldPos.X);
        int wy = Mathf.FloorToInt(worldPos.Y);
        int wz = Mathf.FloorToInt(worldPos.Z);
        int sun = GetSunlightWorld(wx, wy, wz);
        return Mathf.Clamp((float)sun / LightEngine.MAX_LIGHT, 0f, 1f);
    }

    // Vertical sky exposure at `worldPos`, normalized to [0, 1] where 1.0 is
    // open sky straight up and 0 means fully covered (solid ceiling, or canopy
    // dense enough to extinguish the column). Reads the non-leaky SkyExposure
    // field — the sunlight column scan's pre-spread value — so it answers "is
    // there cover overhead, and how much" WITHOUT the horizontal bleed of
    // GetSkyLight01. Single source of truth for cover/shelter gameplay: rain
    // wetness, rain-splash spawning, and verb gating like NoCeilingRequirement.
    // Cross-chunk-correct (the column scan descends through chunk boundaries)
    // and baked, so a ceiling in the chunk above still shelters the point.
    public float GetSkyExposure01(Godot.Vector3 worldPos)
    {
        int wx = Mathf.FloorToInt(worldPos.X);
        int wy = Mathf.FloorToInt(worldPos.Y);
        int wz = Mathf.FloorToInt(worldPos.Z);
        int sky = GetSkyExposureWorld(wx, wy, wz);
        return Mathf.Clamp((float)sky / LightEngine.MAX_LIGHT, 0f, 1f);
    }

    // True when `worldPos` is exposed to sky at or above `minSkyExposure01`,
    // using the non-leaky vertical SkyExposure field. Default 1.0 is strict
    // open-sky (nothing overhead); lower values accept partial canopy. This
    // replaces the old GetSkyLight01-based check for cover gating so a cave
    // mouth's horizontal light leak no longer reads as "outside".
    public bool IsOutside(Godot.Vector3 worldPos, float minSkyExposure01 = 1f)
    {
        return GetSkyExposure01(worldPos) >= minSkyExposure01;
    }

    public void GetBlockLightWorld(int wx, int wy, int wz, out int r, out int g, out int b)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            r = 0; g = 0; b = 0;
            return;
        }
        chunk.GetBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), out r, out g, out b);
    }

    public void AddBlockLightWorld(int wx, int wy, int wz, int r, int g, int b)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.AddBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), r, g, b);
        LightChunkDirty.Add(cc);
    }

    public void SubtractBlockLightWorld(int wx, int wy, int wz, int r, int g, int b)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SubtractBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), r, g, b);
        LightChunkDirty.Add(cc);
    }

    // Fog density at a world-space voxel, byte 0-255. 0 = clear air, 255 =
    // thickest. Unloaded chunks return 0 — the streaming-correct default so a
    // chunk outside the resident window reads as "no fog data here" rather
    // than bleeding fog across the window edge.
    public int GetFogWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetFog(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetFogWorld(int wx, int wy, int wz, int density)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetFog(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), density);
        FogChunkDirty.Add(cc);
    }

    // Foliage canopy sun-attenuation at a world voxel, byte 0-255. Returns
    // 0 (no attenuation) for chunks outside the resident set or with no
    // canopy data yet — same streaming-correct default as fog.
    public int GetCanopyAttenuationWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!CanopyAttenuation.TryGetValue(cc, out byte[,,] arr))
        {
            return 0;
        }
        return arr[Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE)];
    }

    // Saturating add — multiple overlapping foliage clusters stack but
    // can never exceed 255. Allocates the chunk's canopy array on demand,
    // but only if the chunk is resident and `amount` is positive.
    public void AddCanopyAttenuationWorld(int wx, int wy, int wz, int amount)
    {
        if (amount <= 0)
        {
            return;
        }
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.ContainsKey(cc))
        {
            return;
        }
        if (!CanopyAttenuation.TryGetValue(cc, out byte[,,] arr))
        {
            arr = new byte[ChunkState.SIZE, ChunkState.SIZE, ChunkState.SIZE];
            CanopyAttenuation[cc] = arr;
        }
        int lx = Mod(wx, ChunkState.SIZE);
        int ly = Mod(wy, ChunkState.SIZE);
        int lz = Mod(wz, ChunkState.SIZE);
        int sum = arr[lx, ly, lz] + amount;
        arr[lx, ly, lz] = (byte)(sum > 255 ? 255 : sum);
    }

    public void SetVoxelWorld(int wx, int wy, int wz, VoxelType type)
    {
        SetVoxelWorld(wx, wy, wz, type, VoxelTypeInfo.GetDefaultShape(type));
    }

    public void SetVoxelWorld(int wx, int wy, int wz, VoxelType type, VoxelTypeInfo.SharpAxes shape)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        int lx = Mod(wx, ChunkState.SIZE);
        int ly = Mod(wy, ChunkState.SIZE);
        int lz = Mod(wz, ChunkState.SIZE);
        chunk.Voxels[lx, ly, lz] = type;
        chunk.Shape[lx, ly, lz] = (byte)shape;
    }

    // Combined "how lit is this voxel" used by AI visibility checks. Returns
    // a value in [0, LightEngine.MAX_LIGHT]. Sunlight is already in that
    // space; block light is per-channel byte-scale post-pow values, so we
    // collapse to luminance and rescale.
    public int GetLightLevelWorld(int wx, int wy, int wz)
    {
        int sun = GetSunlightWorld(wx, wy, wz);
        GetBlockLightWorld(wx, wy, wz, out int r, out int g, out int b);
        // Rec.601 luminance, integer-scaled. Each channel saturates at 255
        // for the GPU but the stored ushort can be larger; clamp here too.
        if (r > 255) { r = 255; }
        if (g > 255) { g = 255; }
        if (b > 255) { b = 255; }
        int lum = (r * 299 + g * 587 + b * 114) / 1000;             // 0..255
        int blkScaled = (lum * LightEngine.MAX_LIGHT) / 255;        // 0..MAX_LIGHT
        return Math.Max(sun, blkScaled);
    }
    public int GetLightLevelWorld(Vector3 position)
    {
        int wx = Mathf.FloorToInt(position.X);
        int wy = Mathf.FloorToInt(position.Y);
        int wz = Mathf.FloorToInt(position.Z);
        return GetLightLevelWorld(wx, wy, wz);
    }

    // Perceived brightness at a point, in the same model the shaders use, so
    // gameplay decisions (stealth, mob spawn, etc.) match what the player sees.
    // Mirrors voxel_clip.gdshader / sprite_lit.gdshader:
    //   sun = (bfs_sun / MAX_LIGHT) * sun_intensity
    //         * (sun_ambient + (sunReachesPoint ? (1 - sun_ambient) : 0))
    //   block = max(r, g, b) / 255
    //   perceived = max(sun, block)   (additive in shader, but max here matches
    //                                  how callers reason about "lit vs dark")
    // sunReachesPoint is the result of a directional-shadow raycast: true if
    // the point is in direct sun, false if a tree / cliff / cave is in the way.
    // Callers without physics access (pure-sim code) can pass false to get the
    // shadow-ambient-only value, which is the conservative answer for stealth.
    // Returns float in [0, 1+]; over-bright is possible when block lights stack.
    public float GetPerceivedLightWorld(int wx, int wy, int wz, bool sunReachesPoint)
    {
        int sunBfs = GetSunlightWorld(wx, wy, wz);
        GetBlockLightWorld(wx, wy, wz, out int r, out int g, out int b);

        float sunMask = (float)sunBfs / LightEngine.MAX_LIGHT;
        // Use the time-of-day-blended ambient AND time-of-day-blended
        // primary intensity so night/sunset dim the perceived brightness
        // the same way sprites see it — stealth mechanics track the
        // visible darkness of dusk.
        SkyController sky = SkyController.Current;
        float ambient = sky?.CurrentAmbient ?? 0.4f;
        float primaryIntensity = sky?.CurrentPrimaryIntensity ?? SimData?.DayIntensityBase ?? 2f;
        float sunFactor = ambient + (sunReachesPoint ? (1f - ambient) : 0f);
        float sun = sunMask * primaryIntensity * sunFactor;

        if (r > 255) { r = 255; }
        if (g > 255) { g = 255; }
        if (b > 255) { b = 255; }
        float block = Math.Max(r, Math.Max(g, b)) / 255f;

        return Math.Max(sun, block);
    }

    public float GetPerceivedLightWorld(Vector3 position, bool sunReachesPoint)
    {
        int wx = Mathf.FloorToInt(position.X);
        int wy = Mathf.FloorToInt(position.Y);
        int wz = Mathf.FloorToInt(position.Z);
        return GetPerceivedLightWorld(wx, wy, wz, sunReachesPoint);
    }

    // Trilinearly-sampled wind factor at a world position, in [0, 1].
    // Cell centers sit at the midpoint of each ENV_VOXELS_PER_CELL cube,
    // so the continuous cell coordinate of a world point is
    //     f = wp / ENV_VOXELS_PER_CELL - 0.5
    // Floor → base cell, fract → blend weight to the next cell. Mirrors
    // the GPU's trilinear filter on the wind_map texture so audio code
    // and shaders agree on the value at any point. Out-of-bounds corner
    // cells contribute 0 — same convention as unloaded chunks reading
    // as "no wind here".
    public float SampleWindFactor(Vector3 worldPos)
    {
        const float CELL = ChunkState.ENV_VOXELS_PER_CELL;
        float fx = worldPos.X / CELL - 0.5f;
        float fy = worldPos.Y / CELL - 0.5f;
        float fz = worldPos.Z / CELL - 0.5f;
        int cx0 = (int)Math.Floor(fx);
        int cy0 = (int)Math.Floor(fy);
        int cz0 = (int)Math.Floor(fz);
        float tx = fx - cx0;
        float ty = fy - cy0;
        float tz = fz - cz0;

        float c000 = GetWindFactorAtCell(cx0,     cy0,     cz0);
        float c100 = GetWindFactorAtCell(cx0 + 1, cy0,     cz0);
        float c010 = GetWindFactorAtCell(cx0,     cy0 + 1, cz0);
        float c110 = GetWindFactorAtCell(cx0 + 1, cy0 + 1, cz0);
        float c001 = GetWindFactorAtCell(cx0,     cy0,     cz0 + 1);
        float c101 = GetWindFactorAtCell(cx0 + 1, cy0,     cz0 + 1);
        float c011 = GetWindFactorAtCell(cx0,     cy0 + 1, cz0 + 1);
        float c111 = GetWindFactorAtCell(cx0 + 1, cy0 + 1, cz0 + 1);

        float c00 = c000 * (1f - tx) + c100 * tx;
        float c01 = c001 * (1f - tx) + c101 * tx;
        float c10 = c010 * (1f - tx) + c110 * tx;
        float c11 = c011 * (1f - tx) + c111 * tx;
        float c0 = c00 * (1f - ty) + c10 * ty;
        float c1 = c01 * (1f - ty) + c11 * ty;
        float c = c0 * (1f - tz) + c1 * tz;
        return c / 255f;
    }

    // Trilinearly-sampled env-tag weights at a world position. Each of the
    // eight surrounding cells contributes its tag's wire value with a
    // fractional weight; weights sum to 1 when all corners are loaded.
    // Audio uses these to blend reverb preset parameters smoothly across
    // cell boundaries instead of swapping presets discretely.
    public EnvTagWeights SampleEnvTagWeights(Vector3 worldPos)
    {
        const float CELL = ChunkState.ENV_VOXELS_PER_CELL;
        float fx = worldPos.X / CELL - 0.5f;
        float fy = worldPos.Y / CELL - 0.5f;
        float fz = worldPos.Z / CELL - 0.5f;
        int cx0 = (int)Math.Floor(fx);
        int cy0 = (int)Math.Floor(fy);
        int cz0 = (int)Math.Floor(fz);
        float tx = fx - cx0;
        float ty = fy - cy0;
        float tz = fz - cz0;

        var weights = new EnvTagWeights();
        AccumEnvTagAtCell(ref weights, cx0,     cy0,     cz0,     (1f - tx) * (1f - ty) * (1f - tz));
        AccumEnvTagAtCell(ref weights, cx0 + 1, cy0,     cz0,     tx        * (1f - ty) * (1f - tz));
        AccumEnvTagAtCell(ref weights, cx0,     cy0 + 1, cz0,     (1f - tx) * ty        * (1f - tz));
        AccumEnvTagAtCell(ref weights, cx0 + 1, cy0 + 1, cz0,     tx        * ty        * (1f - tz));
        AccumEnvTagAtCell(ref weights, cx0,     cy0,     cz0 + 1, (1f - tx) * (1f - ty) * tz);
        AccumEnvTagAtCell(ref weights, cx0 + 1, cy0,     cz0 + 1, tx        * (1f - ty) * tz);
        AccumEnvTagAtCell(ref weights, cx0,     cy0 + 1, cz0 + 1, (1f - tx) * ty        * tz);
        AccumEnvTagAtCell(ref weights, cx0 + 1, cy0 + 1, cz0 + 1, tx        * ty        * tz);
        return weights;
    }

    // Trilinearly-sampled water current at a world position, in world m/s.
    // Storage is normalized to [-1, 1] per axis; CVars.waterCurrentSpeed
    // scales it to m/s, matching the water shader's drift integration so
    // gameplay physics agrees with the visible surface flow. Y is always
    // 0 — currents are 2D in the XZ plane.
    public Vector3 SampleWaterCurrent(Vector3 worldPos)
    {
        const float CELL = ChunkState.ENV_VOXELS_PER_CELL;
        float fx = worldPos.X / CELL - 0.5f;
        float fy = worldPos.Y / CELL - 0.5f;
        float fz = worldPos.Z / CELL - 0.5f;
        int cx0 = (int)Math.Floor(fx);
        int cy0 = (int)Math.Floor(fy);
        int cz0 = (int)Math.Floor(fz);
        float tx = fx - cx0;
        float ty = fy - cy0;
        float tz = fz - cz0;

        Vector2 c000 = GetCurrentAtCell(cx0,     cy0,     cz0);
        Vector2 c100 = GetCurrentAtCell(cx0 + 1, cy0,     cz0);
        Vector2 c010 = GetCurrentAtCell(cx0,     cy0 + 1, cz0);
        Vector2 c110 = GetCurrentAtCell(cx0 + 1, cy0 + 1, cz0);
        Vector2 c001 = GetCurrentAtCell(cx0,     cy0,     cz0 + 1);
        Vector2 c101 = GetCurrentAtCell(cx0 + 1, cy0,     cz0 + 1);
        Vector2 c011 = GetCurrentAtCell(cx0,     cy0 + 1, cz0 + 1);
        Vector2 c111 = GetCurrentAtCell(cx0 + 1, cy0 + 1, cz0 + 1);

        Vector2 c00 = c000 * (1f - tx) + c100 * tx;
        Vector2 c01 = c001 * (1f - tx) + c101 * tx;
        Vector2 c10 = c010 * (1f - tx) + c110 * tx;
        Vector2 c11 = c011 * (1f - tx) + c111 * tx;
        Vector2 c0 = c00 * (1f - ty) + c10 * ty;
        Vector2 c1 = c01 * (1f - ty) + c11 * ty;
        Vector2 c = c0 * (1f - tz) + c1 * tz;
        float speed = CVars.waterCurrentSpeed.Value;
        return new Vector3(c.X * speed, 0f, c.Y * speed);
    }

    private Vector2 GetCurrentAtCell(int cellWx, int cellWy, int cellWz)
    {
        Vector3I cc = CellWorldToChunkCoord(cellWx, cellWy, cellWz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return Vector2.Zero;
        }
        int sx = Mod(cellWx, ChunkState.ENV_SUBGRID_SIZE);
        int sy = Mod(cellWy, ChunkState.ENV_SUBGRID_SIZE);
        int sz = Mod(cellWz, ChunkState.ENV_SUBGRID_SIZE);
        return chunk.GetCurrent(sx, sy, sz);
    }

    private int GetWindFactorAtCell(int cellWx, int cellWy, int cellWz)
    {
        Vector3I cc = CellWorldToChunkCoord(cellWx, cellWy, cellWz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        int sx = Mod(cellWx, ChunkState.ENV_SUBGRID_SIZE);
        int sy = Mod(cellWy, ChunkState.ENV_SUBGRID_SIZE);
        int sz = Mod(cellWz, ChunkState.ENV_SUBGRID_SIZE);
        return chunk.WindFactor[sx, sy, sz];
    }

    private void AccumEnvTagAtCell(ref EnvTagWeights weights, int cellWx, int cellWy, int cellWz, float w)
    {
        Vector3I cc = CellWorldToChunkCoord(cellWx, cellWy, cellWz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            // Drop the contribution rather than defaulting to a tag —
            // weights sum < 1 is the listener's "no data here" signal.
            return;
        }
        int sx = Mod(cellWx, ChunkState.ENV_SUBGRID_SIZE);
        int sy = Mod(cellWy, ChunkState.ENV_SUBGRID_SIZE);
        int sz = Mod(cellWz, ChunkState.ENV_SUBGRID_SIZE);
        weights.Add((EnvironmentTag)chunk.EnvTag[sx, sy, sz], w);
    }

    private static Vector3I CellWorldToChunkCoord(int cellWx, int cellWy, int cellWz)
    {
        return new Vector3I(
            (int)Math.Floor((double)cellWx / ChunkState.ENV_SUBGRID_SIZE),
            (int)Math.Floor((double)cellWy / ChunkState.ENV_SUBGRID_SIZE),
            (int)Math.Floor((double)cellWz / ChunkState.ENV_SUBGRID_SIZE)
        );
    }

    public ChunkState GetChunk(Vector3I coord)
    {
        _chunks.TryGetValue(coord, out ChunkState data);
        return data;
    }

    // World-XZ centroid of every named region in the loaded world,
    // computed as the unweighted average of the centers of the chunks
    // whose RegionIndex maps to that region. Border chunks (Data == null
    // on their region entry) are skipped. Lazy-computed on first access;
    // the result is invariant after world load so subsequent reads are
    // cache hits.
    //
    // Streaming caveat: this iterates every resident chunk, which the
    // future streaming path can't do.
    private Dictionary<RegionData, Vector2> _regionCentroidsXZ;
    public IReadOnlyDictionary<RegionData, Vector2> RegionCentroidsXZ
    {
        get
        {
            if (_regionCentroidsXZ == null)
            {
                _regionCentroidsXZ = ComputeRegionCentroidsXZ();
            }
            return _regionCentroidsXZ;
        }
    }

    private Dictionary<RegionData, Vector2> ComputeRegionCentroidsXZ()
    {
        var sums = new Dictionary<RegionData, (Vector2 sum, int count)>();
        foreach (var kv in _chunks)
        {
            ChunkState chunk = kv.Value;
            if (Regions == null || chunk.RegionIndex >= Regions.Length)
            {
                continue;
            }
            RegionData region = Regions[chunk.RegionIndex].Data;
            if (region == null)
            {
                continue;
            }
            Vector2 chunkCenter = new Vector2(
                kv.Key.X * ChunkState.SIZE + ChunkState.SIZE * 0.5f,
                kv.Key.Z * ChunkState.SIZE + ChunkState.SIZE * 0.5f);
            sums.TryGetValue(region, out var entry);
            sums[region] = (entry.sum + chunkCenter, entry.count + 1);
        }
        var centroids = new Dictionary<RegionData, Vector2>(sums.Count);
        foreach (var kv in sums)
        {
            centroids[kv.Key] = kv.Value.sum / kv.Value.count;
        }
        return centroids;
    }

    public bool ContainsChunk(Vector3I coord)
    {
        return _chunks.ContainsKey(coord);
    }

    public List<EntitySimState> GetEntities(Vector3I coord)
    {
        _entities.TryGetValue(coord, out List<EntitySimState> entities);
        return entities;
    }

    public void AddEntity(EntitySimState entity)
    {
        Vector3I coord = World.WorldToChunkCoord(entity.WorldPosition);
        if (!_entities.TryGetValue(coord, out List<EntitySimState> entities))
        {
            entities = new List<EntitySimState>();
            _entities[coord] = entities;
        }
        entities.Add(entity);
    }

    public bool RemoveEntity(EntitySimState entity)
    {
        Vector3I coord = World.WorldToChunkCoord(entity.WorldPosition);
        if (_entities.TryGetValue(coord, out List<EntitySimState> entities))
        {
            return entities.Remove(entity);
        }
        return false;
    }

    // Persistent (non-chunked) entity states — the player's companion(s). Unlike
    // _entities, these are NOT filed by chunk: they're always resident, spawned
    // once at startup, never despawned by chunk eviction, and serialized in the
    // world file's global section rather than inside a chunk blob. A mob starts
    // life chunk-streamed (in _entities) and is moved here by PromoteToPersistent
    // the moment it's tamed (see World.PromoteCompanionToPersistent).
    private readonly List<EntitySimState> _persistentEntities = new();
    public IReadOnlyList<EntitySimState> PersistentEntities => _persistentEntities;

    public void AddPersistentEntity(EntitySimState entity)
    {
        if (!_persistentEntities.Contains(entity))
        {
            _persistentEntities.Add(entity);
        }
    }

    public void RemovePersistentEntity(EntitySimState entity)
    {
        _persistentEntities.Remove(entity);
    }

    // Moves a chunk-filed entity into the persistent store — the runtime-taming
    // transition. Searches the per-chunk buckets to remove it because the bucket
    // is keyed by spawn chunk, which a mover (a wild mob that chased the player
    // before being tamed) no longer matches by position. Searching only walks
    // resident buckets and taming is rare, so the cost is irrelevant.
    public void PromoteToPersistent(EntitySimState entity)
    {
        foreach (List<EntitySimState> bucket in _entities.Values)
        {
            if (bucket.Remove(entity))
            {
                break;
            }
        }
        AddPersistentEntity(entity);
    }

    // Zero out the DetailGroup / DetailStrength painting on every voxel
    // whose scattered sprite would sit within `radius` of `position`.
    // Detail sprites visually sit one voxel above their painted voxel
    // (ChunkDetailScatter anchors at vy + 1), so the distance test
    // compares against (vx + 0.5, vy + 1, vz + 0.5). Detail stamping
    // happens in WorldGen.Generate before any surface entity spawns
    // (StampDetailScatter runs before the per-chunk GenerateProps loop),
    // so callers from a spawn-entry Spawn method see the full painted
    // field and can erase it inline.
    public void ClearDetailVoxelsWithin(Vector3 position, float radius)
    {
        if (radius <= 0f)
        {
            return;
        }
        int rCeil = Mathf.CeilToInt(radius);
        int cx = Mathf.FloorToInt(position.X);
        int cy = Mathf.FloorToInt(position.Y);
        int cz = Mathf.FloorToInt(position.Z);
        float r2 = radius * radius;
        for (int vx = cx - rCeil; vx <= cx + rCeil; vx++)
        {
            for (int vy = cy - rCeil; vy <= cy + rCeil; vy++)
            {
                for (int vz = cz - rCeil; vz <= cz + rCeil; vz++)
                {
                    var spritePos = new Vector3(vx + 0.5f, vy + 1f, vz + 0.5f);
                    if (spritePos.DistanceSquaredTo(position) > r2)
                    {
                        continue;
                    }
                    SetDetailGroupWorld(vx, vy, vz, 0);
                    SetDetailStrengthWorld(vx, vy, vz, 0);
                }
            }
        }
    }

    // True if any existing entity sits within `radius` of `position`. Walks
    // the 3x3x3 chunk neighborhood around `position` so candidates near a
    // chunk boundary still see entities just across the seam.
    public bool HasEntityWithinRadius(Vector3 position, float radius)
    {
        if (radius <= 0f)
        {
            return false;
        }
        Vector3I centerCoord = World.WorldToChunkCoord(position);
        float r2 = radius * radius;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    var coord = new Vector3I(centerCoord.X + dx, centerCoord.Y + dy, centerCoord.Z + dz);
                    if (!_entities.TryGetValue(coord, out List<EntitySimState> list))
                    {
                        continue;
                    }
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].WorldPosition.DistanceSquaredTo(position) < r2)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    // True if any hazard entity's danger zone (HazardRadius) covers `position`.
    // Used to keep mob spawns out of fire traps / campfires / spike traps.
    // Walks the same 3x3x3 chunk neighborhood as HasEntityWithinRadius so a
    // candidate near a chunk seam still sees a hazard just across it.
    public bool HasHazardSpawnConflict(Vector3 position)
    {
        Vector3I centerCoord = World.WorldToChunkCoord(position);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    var coord = new Vector3I(centerCoord.X + dx, centerCoord.Y + dy, centerCoord.Z + dz);
                    if (!_entities.TryGetValue(coord, out List<EntitySimState> list))
                    {
                        continue;
                    }
                    for (int i = 0; i < list.Count; i++)
                    {
                        EntitySimState state = list[i];
                        if (state.HazardRadius > 0f
                            && state.WorldPosition.DistanceSquaredTo(position) < state.HazardRadius * state.HazardRadius)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    // True if any mob sim state sits within `radius` of `position`. Used by
    // hazard spawns to avoid dropping onto an already-placed mob (the reverse
    // of HasHazardSpawnConflict, for order-independence).
    public bool HasMobWithinRadius(Vector3 position, float radius)
    {
        if (radius <= 0f)
        {
            return false;
        }
        Vector3I centerCoord = World.WorldToChunkCoord(position);
        float r2 = radius * radius;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    var coord = new Vector3I(centerCoord.X + dx, centerCoord.Y + dy, centerCoord.Z + dz);
                    if (!_entities.TryGetValue(coord, out List<EntitySimState> list))
                    {
                        continue;
                    }
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] is MobSimState
                            && list[i].WorldPosition.DistanceSquaredTo(position) < r2)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    public void OnVoxelsChanged(List<Vector3I> changedPositions)
    {
        LightEngine.OnVoxelsChanged(this, changedPositions);
    }

    public void AddLightSource(LightSource source)
    {
        LightEngine.AddLightSource(this, source);
    }

    public void RemoveLightSource(LightSource source)
    {
        LightEngine.RemoveLightSource(this, source);
    }

    public void SetLightAmplitude(LightSource source, float amplitude)
    {
        LightEngine.SetAmplitude(this, source, amplitude);
    }
}
