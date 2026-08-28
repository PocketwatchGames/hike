using System;
using System.Collections.Generic;
using Godot;

public class WorldState
{
    // Chunk-coordinate extent, inclusive. Fixed at construction except for the
    // Y axis, which worldgen fits to the terrain it built — see
    // SetVerticalChunkExtent.
    public Vector3I Min { get; private set; }
    public Vector3I Max { get; private set; }
    public SimData SimData;

    // This world's kit palette: the slot table every ChunkState.TerrainId byte
    // indexes, plus the detail groups derived from it. Owned HERE rather than on
    // a static, because it is world state — it outlives generation, it is the
    // .hike's wire format, and a second world (a painter bake on a background
    // thread) must not be able to rebind the one the live world is reading.
    // Never null; a world built without one gets KitPalette.Empty.
    public readonly KitPalette Kits;
    // WHAT A RUN IN THIS WORLD STARTS WITH — the quests, the party and what they
    // already know. Authored on WorldGenData, but they are facts about the WORLD
    // rather than about how it was generated, so they belong here: a .hike
    // loaded in the game used to take all three from whichever WorldGenData the
    // menu happened to have selected, which for a hand-painted or hand-edited
    // world is another world's content entirely (and, for quests, usually none
    // at all).
    //
    // Bound by both producers through BindStartContent, and re-resolved on a
    // .hike load from StartContentPath.
    public WorldScriptData ScriptData;
    public PlayerState[] StartingParty = System.Array.Empty<PlayerState>();
    public TeachableConcept[] InitialKnowledge = System.Array.Empty<TeachableConcept>();

    // Resource path of the authored asset the three above came from, recorded so
    // a .hike can resolve them again. A PATH rather than three serialized
    // tables because initialKnowledge is authored as embedded sub-resources,
    // which have no path of their own — the owner does.
    public string StartContentPath = "";

    // Copy this world's starting content off the resource that authors it.
    // Called once at world creation by WorldGen and by the map painter's bake,
    // and again after a .hike load resolves StartContentPath.
    public void BindStartContent(WorldStartData start)
    {
        if (start == null)
        {
            return;
        }
        ScriptData = start.scriptData;
        StartingParty = start.startingParty ?? System.Array.Empty<PlayerState>();
        InitialKnowledge = start.initialKnowledge != null
            ? System.Linq.Enumerable.ToArray(start.initialKnowledge)
            : System.Array.Empty<TeachableConcept>();
        StartContentPath = start.ResourcePath ?? "";
    }

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

    // Named points of interest resolved during worldgen: name -> world
    // position (XZ centered, Y at the ground top). The reusable anchor map for
    // POI-driven placement — roads and signposts today; bosses, important loot,
    // villages later. Authored names live on ZoneData.PointsOfInterest;
    // WorldGen.ResolvePointsOfInterest fills this. In-memory for now (not
    // serialized into the .hike); add persistence when a runtime system needs
    // POIs after a disk load.
    public readonly Dictionary<string, Vector3> PointsOfInterest = new();

    // Buried-treasure locations by name — the anchors a treasure map points to.
    // Placed by WorldGen.PlaceZoneTreasures and, crucially, RE-REGISTERED by each
    // BuriedSpot as it streams in (BuriedSpot.Create), so this survives the
    // worldgen cache / .hike reload without its own file section: the name rides
    // on the persisted BuriedSpotSimState. An excavated spot does not register,
    // and digging removes its entry, so a map never points at an emptied hole.
    // Runtime cache, not serialized here.
    public readonly Dictionary<string, Vector3> TreasureSpots = new();

    // Default spawn point baked into the world. Set by the loader (from the
    // world file header) or by Main when starting a procedurally-generated
    // game. The packed world file persists this so a save can recreate the
    // intended starting position.
    public Vector3 Spawn;

    // World-scope simulation state that isn't per-chunk and isn't a per-
    // entity property — discovered regions today, quest progress and world
    // flags later. Lives here so the save layer can serialize one cohesive
    // bag of player-progression state alongside the chunk delta layer.
    public SimState SimState = new();

    // Persistent simulation clock in milliseconds. Advanced by Sim.Tick while
    // unpaused; serialized with the rest of the world state so cooldowns,
    // AI timers, etc. survive save/load.
    public ulong GameTimeMs;

    // Normalized time-of-day, in [0, 1], spanning a FULL 24-hour cycle:
    // 0 = sunrise, 0.25 = noon, 0.5 = sunset, 0.75 = midnight, 1 = the next
    // sunrise. Advanced by Sim.Tick scaled by SimData.DayLengthSeconds and the
    // time_scale CVar, and CLAMPED at 1 — the cycle pauses on the threshold of
    // sunrise and only a sleep starts the next day (see Sim.AdvanceToNextSunrise).
    // The last quarter (midnight → sunrise) is played, not elided, but the sun
    // never comes back up over it: the palette stays on its night colors and
    // slides to black (see WeatherDerivation's nightfall pass).
    // SkyController remaps this to the orbit phase (0.25 + tod) to drive the
    // sun/moon arc. Seeded from SimData.InitialTimeOfDay at world creation.
    public double TimeOfDay01;

    // The single source of truth for the day's key normalized-time positions,
    // so spawn gating, the day/night refresh, weather, and ad-hoc checks all
    // agree. Evenly spaced because the clock is a true 24-hour cycle: 6am, 12pm,
    // 6pm, 12am, 6am.
    public const double SunriseTimeOfDay01 = 0.0;
    public const double NoonTimeOfDay01 = 0.25;
    public const double SunsetTimeOfDay01 = 0.5;
    public const double MidnightTimeOfDay01 = 0.75;
    // Where the clock stops and waits for a sleep. Celestially the next
    // sunrise, but the sun does not rise on it — the day ends here instead.
    public const double EndOfDayTimeOfDay01 = 1.0;

    // Night is everything from sunset onward, which now includes the post-
    // midnight quarter up to the end-of-day hold.
    public static bool IsNight(double timeOfDay01) => timeOfDay01 >= SunsetTimeOfDay01;

    // Map the day clock [0,1] (0 = sunrise … 1 = the next sunrise) onto the
    // celestial orbit phase (0.25 = sunrise, 0.5 = noon, 0.75 = sunset, 1.0/0 =
    // midnight). The sun/moon arc math and the diurnal weather curve are written
    // in orbit-phase terms, so anything driving them from TimeOfDay01 remaps
    // through here. Wraps: the post-midnight quarter lands on phase [0, 0.25),
    // where the moon descends to its setting point exactly as the clock hits 1.
    public static double OrbitPhase01(double timeOfDay01) => Mathf.PosMod(0.25 + timeOfDay01, 1.0);

    // Explicit whole-day counter. Starts at 0 and is incremented ONLY by the
    // sleep-to-sunrise path (Sim.AdvanceToNextSunrise) — the day cycle no
    // longer rolls over on its own, so this can't be derived from the clock.
    // Dawn-expiring deadlines (time-limited items, forge cooldown) compare against
    // this rather than projecting a wall-clock sunrise (there is no such time
    // now — the clock stops at the end of the day until the player sleeps).
    public int DayNumber;

    // Unwrapping day+fraction coordinate = DayNumber + TimeOfDay01. Still used
    // by "until sunrise" status-effect expiry (StatusEffectState) and the sky
    // disk-fade windows. Advances with TimeOfDay01 during the day and
    // jumps to (DayNumber+1) + 0 on a sleep-to-sunrise.
    public double TimeOfDayAbsolute;

    // Sun direction (unit vector, the direction light travels). Written by
    // SkyController each frame from TimeOfDay01; read by
    // Sim.IsPointInDirectionalSun for the gameplay shadow-reach raycast.
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

    // Per-day weather variance, in [0, 1]. 0 = stormy / unstable (cool),
    // 1 = fair / stable (warm). Each day pre-rolls TWO weather states
    // at sunrise (Sim.RollDailyWeather, on OnNewDay): a DAY slot (active
    // sunrise → sunset) and a NIGHT slot (active sunset → the day's end), with a
    // crossfade between them across the sunset window. Four independent
    // channels each: WeatherVariance drives temperature (+wind transient via
    // its sunset-crossfade slope); Humidity and Cloud are wind-gated advection
    // channels; Lightning multiplies the storm gate. Both slots are known at
    // sunrise so the HUD can forecast the day AND night icons up front. Lives
    // on WorldState so a save/reload resumes the same forecast.
    public float DayWeatherVariance = 0.5f, NightWeatherVariance = 0.5f;
    public float DayHumidityVariance = 0.5f, NightHumidityVariance = 0.5f;
    public float DayCloudVariance = 0.5f, NightCloudVariance = 0.5f;
    public float DayLightningVariance = 0.5f, NightLightningVariance = 0.5f;

    // Active (sunset-crossfaded) variance for the current frame — lerp(day,
    // night, sunsetBlend), computed by WeatherSimulation.UpdateVariance and
    // read by Apply. WeatherVarianceSlope is the per-day-fraction analytical
    // slope of the sunset crossfade (nonzero only inside the sunset window),
    // driving the wind "frontal kick".
    public float WeatherVariance = 0.5f;
    public float HumidityVariance = 0.5f;
    public float CloudVariance = 0.5f;
    public float LightningVariance = 0.5f;
    public float WeatherVarianceSlope = 0.0f;

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
    // / SubtractBlockLightWorld — callers don't need to remember. The block-light
    // writers only mark when the write moved the value the texture would show
    // (ChunkState.BLOCK_LIGHT_TEXTURE_MAX), so a flicker roll that only shuffles
    // the saturated core or rounds to the same byte costs no upload.
    public readonly HashSet<Vector3I> LightChunkDirty = new();

    // Chunks whose stored sunlight moved during the most recent incremental
    // relight. Narrower than LightChunkDirty (which block-light flicker
    // re-marks constantly) because sunlight is BAKED INTO MESH VERTICES — a
    // chunk in here has a stale mesh until it is re-meshed. Cleared at the top
    // of LightEngine.OnVoxelsChanged, so it only ever describes that call; the
    // world-load flood populates it harmlessly and the first incremental edit
    // wipes it.
    public readonly HashSet<Vector3I> SunlightChunkDirty = new();

    // Same pattern for FogMap. Populated automatically by SetFogWorld.
    // Currently only worldgen writes fog, so this only trips if something
    // mutates fog at runtime (e.g. a weather CVar or future fog emitter).
    public readonly HashSet<Vector3I> FogChunkDirty = new();

    // Same pattern for the SkyExposureMap GPU texture. Populated automatically
    // by the sky-exposure scan — the initial pass floods it (drained
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

    // The SHADOW cast below a canopy, as distinct from the canopy itself.
    // CanopyAttenuation is a medium — actual leaves, which absorb light passing
    // through them. The air under a tree contains no leaves, so the vertical
    // column scan must not charge it again: it already paid for the canopy on
    // the way down, and re-charging per voxel made a tree's shade a function of
    // its TRUNK HEIGHT (a 10m birch went black at the base, a 4m pine didn't)
    // rather than of its foliage.
    //
    // But the lateral BFS still has to be stopped, or a neighbouring un-canopied
    // column refills the shaded voxel at MAX_LIGHT minus a couple of levels and
    // the tree casts no shade at all. That is this field's ONLY job: it is read
    // by SpreadSunlight and by nothing else — not the column scan, not sky
    // exposure, not the block-light flood (a torch under a tree is not shining
    // through leaves). FoliageStamper derives it per column as the total canopy
    // density that column passes through, so lateral refill can never exceed the
    // vertical answer. Same sparse layout, lifetime and non-serialization as
    // CanopyAttenuation.
    public readonly Dictionary<Vector3I, byte[,,]> CanopyShade = new();

    // Voxels that stop sunlight DEAD, as a solid voxel does, for cover that
    // isn't made of voxels — a roof. Distinct from CanopyAttenuation because
    // that field can only ever attenuate: its transmittance is
    // exp(-density * canopySunExtinction) with a single global extinction
    // tuned for leaves, so even a saturated byte lets ~55% through per voxel.
    // Fine for a canopy, wrong for a solid roof. Same lifetime and streaming
    // rules as CanopyAttenuation, and likewise NOT serialized — rebuilt from
    // the entity list on load.
    public readonly Dictionary<Vector3I, bool[,,]> SunOpaque = new();

    // Same pattern for WaterCurrentMap. Populated by anything that mutates
    // water-current vectors at runtime; ChunkManager drains it each frame
    // to push only touched chunks back to the GPU.
    public readonly HashSet<Vector3I> WaterCurrentChunkDirty = new();

    // Same pattern for WindMap. Used for both per-cell wind velocity (RGB
    // channels) and WindFactor (alpha channel) writes — they share a
    // texture, so a change to either marks the chunk for re-encode.
    public readonly HashSet<Vector3I> WindChunkDirty = new();

    public WorldState(Vector3I min, Vector3I max, SimData simData, KitPalette kits = null)
    {
        Min = min;
        Max = max;
        SimData = simData;
        Kits = kits ?? KitPalette.Empty;
        // Seed the scripting-variable bank from the authored registry before
        // any save data loads; harmless when no registry is authored.
        SimState.ScriptVars.Initialize(simData?.scriptVariables);
        TimeOfDay01 = simData?.initialTimeOfDay ?? 0.05f;
        DayNumber = 0;
        TimeOfDayAbsolute = DayNumber + TimeOfDay01;
        WeatherRng.Randomize();
        // Roll the first day's day + night weather slots. Subsequent days
        // re-roll on the sleep-to-sunrise (Sim fires OnNewDay → RollDailyWeather).
        RollDailyWeather();
        WeatherVariance = DayWeatherVariance;
        HumidityVariance = DayHumidityVariance;
        CloudVariance = DayCloudVariance;
        LightningVariance = DayLightningVariance;
    }

    // Fit the vertical extent to the terrain worldgen just built. The heightmap
    // is produced before any chunk exists, so the world doesn't have to guess
    // its own height up front — see WorldGen.FitVerticalExtent for the headroom
    // rules. Guarded on emptiness because every chunk's coord, and every
    // Min.Y-relative scan, would be sized against the old extent.
    //
    // A world that reaches its own top voxel has no air above the peaks: the
    // sunlight column scan breaks on the first solid voxel it meets and the
    // whole column goes dark, and anything sampling the light_map above the top
    // wraps back to the underground band. Hence the guaranteed headroom.
    public void SetVerticalChunkExtent(int minChunkY, int maxChunkY)
    {
        if (_chunks.Count > 0)
        {
            throw new InvalidOperationException("SetVerticalChunkExtent after chunks exist");
        }
        Min = new Vector3I(Min.X, minChunkY, Min.Z);
        Max = new Vector3I(Max.X, maxChunkY, Max.Z);
    }

    // Pre-roll a fresh DAY and NIGHT weather slot from WeatherRng. Called at
    // world creation and on every sleep-to-sunrise (Sim.AdvanceToNextSunrise).
    // Both slots are determined here so the HUD can forecast the whole day up
    // front and the sunset crossfade has a fixed target.
    public void RollDailyWeather()
    {
        DayWeatherVariance = WeatherRng.Randf();
        DayHumidityVariance = WeatherRng.Randf();
        DayCloudVariance = WeatherRng.Randf();
        DayLightningVariance = WeatherRng.Randf();
        NightWeatherVariance = WeatherRng.Randf();
        NightHumidityVariance = WeatherRng.Randf();
        NightCloudVariance = WeatherRng.Randf();
        NightLightningVariance = WeatherRng.Randf();
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

    public int GetBlockWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return Blocks.AirId;
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

    public SharpAxes GetShapeWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return SharpAxes.None;
        }
        return chunk.GetShape(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetShapeWorld(int wx, int wy, int wz, SharpAxes shape)
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

    public int GetOverlayFacesWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetOverlayFaces(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetOverlayFacesWorld(int wx, int wy, int wz, int faces)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetOverlayFaces(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), faces);
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
        SunlightChunkDirty.Add(cc);
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
            kvp.Value.MarkSunlightChanged();
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
        if (chunk.AddBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), r, g, b))
        {
            LightChunkDirty.Add(cc);
        }
    }

    public void SubtractBlockLightWorld(int wx, int wy, int wz, int r, int g, int b)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        if (chunk.SubtractBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), r, g, b))
        {
            LightChunkDirty.Add(cc);
        }
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
        return chunk.GetFog(SimData, Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
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

    // Canopy SHADOW below a tree at a world voxel, byte 0-255. Lateral-transport
    // only — see the CanopyShade field comment for why the column scan must not
    // read this.
    public int GetCanopyShadeWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!CanopyShade.TryGetValue(cc, out byte[,,] arr))
        {
            return 0;
        }
        return arr[Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE)];
    }

    // True if a non-voxel occluder blocks the sky at this voxel outright.
    public bool GetSunOpaqueWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!SunOpaque.TryGetValue(cc, out bool[,,] arr))
        {
            return false;
        }
        return arr[Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE)];
    }

    // Allocates the chunk's array on demand, but only if the chunk is resident.
    public void SetSunOpaqueWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.ContainsKey(cc))
        {
            return;
        }
        if (!SunOpaque.TryGetValue(cc, out bool[,,] arr))
        {
            arr = new bool[ChunkState.SIZE, ChunkState.SIZE, ChunkState.SIZE];
            SunOpaque[cc] = arr;
        }
        arr[Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE)] = true;
        // A roof is a ceiling with no voxel behind it, so this is the only signal
        // the cell decomposition gets that one appeared.
    }

    // Wipes both non-voxel occlusion fields at one voxel. They are cleared as a
    // pair because they are two halves of one answer — a regional restamp that
    // reset only one would leave the other's stale stamp behind.
    public void ClearSunOcclusionWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        int lx = Mod(wx, ChunkState.SIZE);
        int ly = Mod(wy, ChunkState.SIZE);
        int lz = Mod(wz, ChunkState.SIZE);
        if (CanopyAttenuation.TryGetValue(cc, out byte[,,] canopy))
        {
            canopy[lx, ly, lz] = 0;
        }
        if (CanopyShade.TryGetValue(cc, out byte[,,] shade))
        {
            shade[lx, ly, lz] = 0;
        }
        if (SunOpaque.TryGetValue(cc, out bool[,,] opaque))
        {
            opaque[lx, ly, lz] = false;
        }
    }

    // Air thickness at a voxel, [0,1]. The CPU read of the same serialized fog
    // field the fog raymarch and mote shaders sample, for effects that size
    // themselves off local air rather than the regional weather scalar.
    // 0 outside a resident chunk.
    public float GetAirDensityWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0f;
        }
        int lx = Mod(wx, ChunkState.SIZE);
        int ly = Mod(wy, ChunkState.SIZE);
        int lz = Mod(wz, ChunkState.SIZE);
        return chunk.GetFog(SimData, lx, ly, lz) / 255f;
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

    // Saturating add for the canopy shadow field. Separate trees shading the
    // same column stack, exactly as their canopies do.
    public void AddCanopyShadeWorld(int wx, int wy, int wz, int amount)
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
        if (!CanopyShade.TryGetValue(cc, out byte[,,] arr))
        {
            arr = new byte[ChunkState.SIZE, ChunkState.SIZE, ChunkState.SIZE];
            CanopyShade[cc] = arr;
        }
        int lx = Mod(wx, ChunkState.SIZE);
        int ly = Mod(wy, ChunkState.SIZE);
        int lz = Mod(wz, ChunkState.SIZE);
        int sum = arr[lx, ly, lz] + amount;
        arr[lx, ly, lz] = (byte)(sum > 255 ? 255 : sum);
    }

    // Rewriting a voxel with the SAME material keeps its shape tag; changing
    // the material resets to the new material's default.
    //
    // Both halves matter. Unconditionally defaulting meant any later pass
    // re-touching a graded column silently hardened it back to a stair.
    // Unconditionally preserving is worse: the editor's stone brush writes
    // Stone through this overload and depends on the default SharpAxes.All
    // for its cubic edges, so painting stone over terrain would inherit Y and
    // round the wall off. Callers wanting a non-default shape use the 5-arg form.
    public void SetBlockWorld(int wx, int wy, int wz, int type)
    {
        SharpAxes shape = GetBlockWorld(wx, wy, wz) == type
            ? GetShapeWorld(wx, wy, wz)
            : Blocks.DefaultShape(type);
        SetBlockWorld(wx, wy, wz, type, shape);
    }

    public void SetBlockWorld(int wx, int wy, int wz, int type, SharpAxes shape)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        int lx = Mod(wx, ChunkState.SIZE);
        int ly = Mod(wy, ChunkState.SIZE);
        int lz = Mod(wz, ChunkState.SIZE);
        chunk.Voxels[lx, ly, lz] = (byte)type;
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
        float primaryIntensity = sky?.CurrentPrimaryIntensity ?? SimData?.dayIntensityBase ?? 2f;
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
        // Space-class suppression is already baked in by WindGen, so this is
        // the final value — applying it again here would double it, and the
        // GPU (wind_map's alpha) reads the baked channel and could not see a
        // sample-time multiply anyway.
        return c / 255f;
    }

    // Trilinearly-sampled space-class ambience at a world position. Each of the
    // eight surrounding cells contributes its palette entry's fields with a
    // fractional weight; weights sum to 1 when all corners are loaded.
    // Blending the values means crossing a threshold crossfades reverb, wind
    // and dust together instead of snapping at a cell boundary.
    public InteriorAmbience SampleInteriorAmbience(Vector3 worldPos)
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

        var ambience = new InteriorAmbience();
        AccumAmbienceAtCell(ref ambience, cx0,     cy0,     cz0,     (1f - tx) * (1f - ty) * (1f - tz));
        AccumAmbienceAtCell(ref ambience, cx0 + 1, cy0,     cz0,     tx        * (1f - ty) * (1f - tz));
        AccumAmbienceAtCell(ref ambience, cx0,     cy0 + 1, cz0,     (1f - tx) * ty        * (1f - tz));
        AccumAmbienceAtCell(ref ambience, cx0 + 1, cy0 + 1, cz0,     tx        * ty        * (1f - tz));
        AccumAmbienceAtCell(ref ambience, cx0,     cy0,     cz0 + 1, (1f - tx) * (1f - ty) * tz);
        AccumAmbienceAtCell(ref ambience, cx0 + 1, cy0,     cz0 + 1, tx        * (1f - ty) * tz);
        AccumAmbienceAtCell(ref ambience, cx0,     cy0 + 1, cz0 + 1, (1f - tx) * ty        * tz);
        AccumAmbienceAtCell(ref ambience, cx0 + 1, cy0 + 1, cz0 + 1, tx        * ty        * tz);

        // Class says WHAT KIND of interior; interiorness says HOW MUCH of it
        // applies. Classification is a threshold, so without this the cell grid
        // gives a hard boundary softened only by the trilinear blend above —
        // one 4m cell wide. Blending back toward outdoor by the continuous
        // interiorness spreads the transition over the whole aperture falloff
        // instead, with no step where the class flipped.
        //
        // Interiorness is ALSO what the derived dust term scales by
        // (ChunkState.GetFog), so air and acoustics cannot disagree about how
        // enclosed a point is.
        float openness01 = 1f - SampleInteriorness(worldPos);
        ambience.BlendToward(SimData?.GetInteriorAmbience(0), Mathf.Clamp(openness01, 0f, 1f));
        return ambience;
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

    // Write one env cell's current, addressed in WORLD cell coords. Public
    // because worldgen bakes the field per column and has to resolve the chunk
    // per cell; a cell outside the loaded world is dropped rather than faulted in.
    public void SetCurrentAtCell(int cellWx, int cellWy, int cellWz, float fx, float fz)
    {
        Vector3I cc = CellWorldToChunkCoord(cellWx, cellWy, cellWz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetCurrent(
            Mod(cellWx, ChunkState.ENV_SUBGRID_SIZE),
            Mod(cellWy, ChunkState.ENV_SUBGRID_SIZE),
            Mod(cellWz, ChunkState.ENV_SUBGRID_SIZE),
            fx, fz);
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
        return chunk.GetWindFactor(SimData, sx, sy, sz);
    }

    // Trilinearly-sampled interiorness at a world position, in [0, 1].
    // 1 = deeply enclosed, 0 = outdoors. Shares the cell-centre convention of
    // every other env-subgrid sampler so the class and its strength are read
    // from the same interpolation.
    public float SampleInteriorness(Vector3 worldPos)
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

        float c00 = Mathf.Lerp(InteriornessAtCell(cx0, cy0, cz0), InteriornessAtCell(cx0 + 1, cy0, cz0), tx);
        float c01 = Mathf.Lerp(InteriornessAtCell(cx0, cy0, cz0 + 1), InteriornessAtCell(cx0 + 1, cy0, cz0 + 1), tx);
        float c10 = Mathf.Lerp(InteriornessAtCell(cx0, cy0 + 1, cz0), InteriornessAtCell(cx0 + 1, cy0 + 1, cz0), tx);
        float c11 = Mathf.Lerp(InteriornessAtCell(cx0, cy0 + 1, cz0 + 1), InteriornessAtCell(cx0 + 1, cy0 + 1, cz0 + 1), tx);
        float c0 = Mathf.Lerp(c00, c10, ty);
        float c1 = Mathf.Lerp(c01, c11, ty);
        return Mathf.Lerp(c0, c1, tz) / 255f;
    }

    // Unloaded neighbours read as 0 (open) rather than enclosed, so the edge of
    // the resident window never fabricates an interior.
    private float InteriornessAtCell(int cellWx, int cellWy, int cellWz)
    {
        Vector3I cc = CellWorldToChunkCoord(cellWx, cellWy, cellWz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0f;
        }
        return chunk.GetInteriorness(
            Mod(cellWx, ChunkState.ENV_SUBGRID_SIZE),
            Mod(cellWy, ChunkState.ENV_SUBGRID_SIZE),
            Mod(cellWz, ChunkState.ENV_SUBGRID_SIZE));
    }

    private void AccumAmbienceAtCell(ref InteriorAmbience ambience, int cellWx, int cellWy, int cellWz, float w)
    {
        Vector3I cc = CellWorldToChunkCoord(cellWx, cellWy, cellWz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            // Drop the contribution rather than defaulting to a class —
            // TotalWeight < 1 is the listener's "no data here" signal.
            return;
        }
        int sx = Mod(cellWx, ChunkState.ENV_SUBGRID_SIZE);
        int sy = Mod(cellWy, ChunkState.ENV_SUBGRID_SIZE);
        int sz = Mod(cellWz, ChunkState.ENV_SUBGRID_SIZE);
        ambience.Accumulate(SimData?.GetInteriorAmbience(chunk.EnvTag[sx, sy, sz]), w);
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

    // World Y of the highest non-Air voxel anywhere in the world, or null when
    // nothing is filled. Chunks that can't beat the best found so far are
    // skipped whole, but this still walks resident chunks — for one-off framing
    // decisions (the editor's initial cutaway height), never per-frame, and the
    // future streaming path can't do it at all (see AllChunkEntities).
    public int? GetHighestSolidVoxelY()
    {
        int best = int.MinValue;
        foreach (var kv in _chunks)
        {
            int baseY = kv.Key.Y * ChunkState.SIZE;
            if (baseY + ChunkState.SIZE - 1 <= best)
            {
                continue;
            }
            ChunkState chunk = kv.Value;
            for (int y = ChunkState.SIZE - 1; y >= 0 && baseY + y > best; y--)
            {
                if (LayerHasSolid(chunk, y))
                {
                    best = baseY + y;
                    break;
                }
            }
        }
        return best == int.MinValue ? null : best;
    }

    private static bool LayerHasSolid(ChunkState chunk, int y)
    {
        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int z = 0; z < ChunkState.SIZE; z++)
            {
                if (chunk.Voxels[x, y, z] != Blocks.AirId)
                {
                    return true;
                }
            }
        }
        return false;
    }

    public List<EntitySimState> GetEntities(Vector3I coord)
    {
        _entities.TryGetValue(coord, out List<EntitySimState> entities);
        return entities;
    }

    // Every chunk-filed entity state across the whole world. A full-world walk —
    // intended only for infrequent world-wide operations like the day-pass spawn
    // reset (Sim.ResetSpawns), never per-frame. When the world becomes streamed
    // (a bounded resident set rather than all chunks loaded) this only sees
    // resident chunks; revisit then if a global sweep must reach evicted chunks.
    // Block-id census over every resident chunk, most common first. Answers
    // "did this material actually get placed?" — the question no amount of
    // reading the catalog or the atlas can settle, and the one that catches a
    // worldgen pass silently writing the wrong block. See CVars.worldHistogram.
    public string DescribeBlockHistogram()
    {
        var counts = new long[BlockCatalog.MAX_BLOCKS];
        long total = 0;
        foreach (ChunkState chunk in _chunks.Values)
        {
            for (int x = 0; x < ChunkState.SIZE; x++)
            {
                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    for (int z = 0; z < ChunkState.SIZE; z++)
                    {
                        int id = chunk.Voxels[x, y, z];
                        if (id == Blocks.AirId) { continue; }
                        counts[id]++;
                        total++;
                    }
                }
            }
        }

        var order = new List<int>();
        for (int id = 0; id < counts.Length; id++)
        {
            if (counts[id] > 0) { order.Add(id); }
        }
        order.Sort((a, b) => counts[b].CompareTo(counts[a]));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[world_histogram] {_chunks.Count} chunks, {total} non-air voxels");
        foreach (int id in order)
        {
            BlockData block = BlockCatalog.Active.GetById(id);
            string name = block != null ? block.blockName.ToString() : "<unknown>";
            sb.AppendLine($"  {id,2}  {name,-16} {counts[id],10}  {100.0 * counts[id] / Math.Max(total, 1),5:0.00}%");
        }

        // Overlays are a separate channel and invisible to the block counts —
        // a moss or road pass that placed nothing looks identical above.
        var overlayCounts = new long[BlockCatalog.MAX_ATLAS_LAYERS];
        long overlayTotal = 0;
        foreach (ChunkState chunk in _chunks.Values)
        {
            for (int x = 0; x < ChunkState.SIZE; x++)
            {
                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    for (int z = 0; z < ChunkState.SIZE; z++)
                    {
                        int ov = chunk.OverlayId[x, y, z];
                        if (ov == 0 || ov >= overlayCounts.Length) { continue; }
                        overlayCounts[ov]++;
                        overlayTotal++;
                    }
                }
            }
        }
        sb.AppendLine($"  overlays: {overlayTotal} voxels");
        // Per zone as well: a scatter gated by a per-zone knob can be authored
        // correctly and still land nowhere, and a global count cannot show that.
        var byZone = new Dictionary<int, long>();
        foreach (KeyValuePair<Vector3I, ChunkState> kv in _chunks)
        {
            ChunkState chunk = kv.Value;
            long n = 0;
            for (int x = 0; x < ChunkState.SIZE; x++)
            {
                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    for (int z = 0; z < ChunkState.SIZE; z++)
                    {
                        if (chunk.OverlayId[x, y, z] != 0) { n++; }
                    }
                }
            }
            byZone.TryGetValue(chunk.ZoneIndex, out long prev);
            byZone[chunk.ZoneIndex] = prev + n;
        }
        foreach (KeyValuePair<int, long> kv in byZone)
        {
            bool named = kv.Key >= 0 && kv.Key < Zones.Length && Zones[kv.Key].Data != null;
            string zoneName = named ? Zones[kv.Key].Data.ResourcePath.GetFile() : "-";
            sb.AppendLine($"    zone {kv.Key,2} {zoneName,-28} {kv.Value,10}");
        }
        for (int layer = 0; layer < overlayCounts.Length; layer++)
        {
            if (overlayCounts[layer] == 0) { continue; }
            BlockSurfaceData surface = BlockCatalog.Active.GetSurfaceByLayer(layer);
            string name = surface != null ? surface.surfaceName.ToString() : "<unknown layer>";
            sb.AppendLine($"    layer {layer,2}  {name,-16} {overlayCounts[layer],10}");
        }
        return sb.ToString();
    }

    public IEnumerable<EntitySimState> AllChunkEntities()
    {
        foreach (List<EntitySimState> list in _entities.Values)
        {
            foreach (EntitySimState state in list)
            {
                yield return state;
            }
        }
    }

    // When true, AddEntity flags added entities as PlacedAsFixture so WorldGen's
    // road pass routes around them and never clears/regrades under them. Set
    // only around WorldGen's authored fixture passes; false (the default)
    // everywhere else, including all runtime spawning.
    public bool TaggingFixtures;

    public void AddEntity(EntitySimState entity)
    {
        if (TaggingFixtures)
        {
            entity.PlacedAsFixture = true;
        }
        Vector3I coord = Sim.WorldToChunkCoord(entity.WorldPosition);
        if (!_entities.TryGetValue(coord, out List<EntitySimState> entities))
        {
            entities = new List<EntitySimState>();
            _entities[coord] = entities;
        }
        entities.Add(entity);
    }

    // Wholesale replacement of a chunk's filed entity states — the editor's
    // undo/redo restoring a snapshot of the bucket. Add/RemoveEntity can't
    // express that: they re-derive the bucket from an entity's CURRENT
    // position, which isn't necessarily the chunk it was filed under.
    public void ReplaceChunkEntities(Vector3I coord, List<EntitySimState> entities)
    {
        if (entities == null || entities.Count == 0)
        {
            _entities.Remove(coord);
            return;
        }
        _entities[coord] = new List<EntitySimState>(entities);
    }

    public bool RemoveEntity(EntitySimState entity)
    {
        Vector3I coord = Sim.WorldToChunkCoord(entity.WorldPosition);
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
    // the moment it's tamed (see Sim.PromoteCompanionToPersistent).
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
        Vector3I centerCoord = Sim.WorldToChunkCoord(position);
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
        Vector3I centerCoord = Sim.WorldToChunkCoord(position);
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
        Vector3I centerCoord = Sim.WorldToChunkCoord(position);
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
