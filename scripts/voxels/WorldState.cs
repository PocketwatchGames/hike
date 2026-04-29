using System;
using System.Collections.Generic;
using Godot;

public class WorldState
{
    public readonly Vector3I Min;
    public readonly Vector3I Max;
    public SimData SimData;

    // Regions present in this world. Populated by WorldGen (or the disk
    // loader) at world creation; each ChunkState.RegionIndex picks one of
    // these. RegionBlend.Sample reads this array to produce the player's
    // current blended region/weather, so any change here is visible at
    // the next frame. Empty array means "no regions authored" — RegionBlend
    // falls back to defaults.
    public RegionState[] Regions = [];

    // Default spawn point baked into the world. Set by the loader (from the
    // world file header) or by Main when starting a procedurally-generated
    // game. The packed world file persists this so a save can recreate the
    // intended starting position.
    public Vector3 Spawn;

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
    // Each channel has prev / next + a HANDOVER PHASE INDEX. Phase
    // increments at the start of each sunrise/sunset crossfade window;
    // when it does, prev := next (so displayed value is continuous)
    // and a fresh next is rolled. Inside the window, the displayed
    // value smooth-steps prev → next; outside, it sits at next until
    // the next window. Lives on WorldState so a save/reload resumes
    // the same forecast rather than snapping to a new value.
    public float WeatherVariance = 0.5f;
    public float WeatherVariancePrev = 0.5f;
    public float WeatherVarianceNext = 0.5f;
    // Per-day-fraction analytical slope of WeatherVariance. 0 outside
    // the crossfade window; ramps with SmoothStep' inside. Wind /
    // temperature transient calcs read this directly so the signal is
    // independent of frame rate and survives time_scale changes.
    public float WeatherVarianceSlope = 0.0f;
    // Most recently completed handover phase index. long.MinValue means
    // "uninitialized" — first UpdateVariance call snaps this to the
    // current phase without rolling, so the freshly seeded prev/next
    // values aren't promoted away on frame 0.
    public long WeatherVariancePhase = long.MinValue;

    // Independent humidity-variance channel. Same handover-phase
    // structure as WeatherVariance, but its effect on simulated
    // humidity is GATED BY SIMULATED WIND SPEED — calm air holds the
    // regional baseline; rising wind blows the neighboring humidity
    // pattern in.
    public float HumidityVariance = 0.5f;
    public float HumidityVariancePrev = 0.5f;
    public float HumidityVarianceNext = 0.5f;
    public float HumidityVarianceSlope = 0.0f;
    public long HumidityVariancePhase = long.MinValue;

    // Independent cloud-cover variance channel. Same gating rule as
    // humidity — clouds are physically advected by the wind, so on a
    // calm day the regional baseline holds and a strong wind pulls in
    // whatever the variance says is "out there".
    public float CloudVariance = 0.5f;
    public float CloudVariancePrev = 0.5f;
    public float CloudVarianceNext = 0.5f;
    public float CloudVarianceSlope = 0.0f;
    public long CloudVariancePhase = long.MinValue;

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

    public WorldState(Vector3I min, Vector3I max, SimData simData)
    {
        Min = min;
        Max = max;
        SimData = simData;
        TimeOfDay01 = simData?.InitialTimeOfDay ?? 0.3f;
        TimeOfDayAbsolute = TimeOfDay01;
        WeatherRng.Randomize();
        // Pre-roll prev/next on each channel so the first frame has a
        // valid lerp pair. The phase fields stay at long.MinValue —
        // UpdateVariance snaps each channel's phase to "current" on
        // first call without rolling, so these initial values aren't
        // promoted away.
        WeatherVariancePrev = WeatherRng.Randf();
        WeatherVarianceNext = WeatherRng.Randf();
        WeatherVariance = WeatherVarianceNext;
        HumidityVariancePrev = WeatherRng.Randf();
        HumidityVarianceNext = WeatherRng.Randf();
        HumidityVariance = HumidityVarianceNext;
        CloudVariancePrev = WeatherRng.Randf();
        CloudVarianceNext = WeatherRng.Randf();
        CloudVariance = CloudVarianceNext;
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

    public int GetKitIdWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetKitId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetKitIdWorld(int wx, int wy, int wz, int kitId)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetKitId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), kitId);
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
        // Use the time-of-day-blended ambient AND time-of-day-scaled
        // primary intensity (not the raw palette value / CVars.sunIntensity)
        // so night/sunset dim the perceived brightness the same way sprites
        // see it — stealth mechanics track the visible darkness of dusk.
        SkyController sky = SkyController.Current;
        float ambient = sky?.CurrentAmbient ?? 0.4f;
        float primaryIntensity = sky?.CurrentPrimaryIntensity ?? CVars.sunIntensity.Value;
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
