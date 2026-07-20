using Godot;

// World — environmental sampling (weather + voxel-light driven). Pure queries
// over the current weather (SkyController) and the voxel sunlight BFS
// (WorldState). Live on World, not the client, because the sim (Player
// thermal/wetness, perception, scent) is the primary consumer; the debug
// `temp` CVar reads the breakdown too. See World.cs for the file split.
public partial class Sim
{
    // Sample wind speed in m/s at `worldPos`. Returns 0 when the voxel sun
    // BFS reports no skylight at all — a stand-in for "the player is in a
    // cave or under a roof", where the open-sky wind from the weather
    // system shouldn't reach them. Permissive: BFS spreads sideways from
    // open columns, so a cave mouth or doorway still seeps wind. Same
    // shape as SampleAirTemperature so callers can ignore wind whenever
    // they ignore weather.
    public float SampleWindSpeed(Vector3 worldPos)
    {
        SkyController sky = SkyController.Current;
        if (sky?.Weather == null) { return 0f; }
        float wind = sky.Weather.windSpeed;
        if (wind <= 0f) { return 0f; }

        if (_worldState != null && _worldState.GetSkyLight01(worldPos) <= 0f)
        {
            return 0f;
        }
        return wind;
    }

    // Per-component breakdown of the air-temperature sample. The `temp`
    // console CVar prints these so weather / lighting / occlusion can be
    // inspected independently. Final temperature is `Total`.
    public struct AirTemperatureSample
    {
        public float air;             // weather.airTemperature (°F, base ambient)
        public float sunTemperature;  // weather.sunTemperature (°F, max sun add)
        public float sunFactor;       // sky.SunFactor (time-of-day, 0..1)
        public float cloudCover;      // weather.cloudCover (0..1)
        public float fog;             // sky.Palette.Fog (0..1)
        public float skyTransmission; // 1 − clamp(cloudCover + fog, 0, 1)
        public float sunMask;         // sunBfs / LightEngine.MAX_LIGHT (0..1)

        public readonly float SunContribution => sunTemperature * sunFactor * skyTransmission * sunMask;
        public readonly float Total => air + SunContribution;
    }

    // Sample environmental air temperature in degrees F at `worldPos`.
    // airTemperature flows through unconditionally; sunTemperature stacks on
    // scaled by (a) sun strength now, (b) atmospheric transmission (clouds +
    // fog), and (c) the voxel sunlight BFS mask at the sample point — so
    // overhangs, caves, and foliage shade the sun's heating exactly the way
    // the world's lighting pass already classifies them. Player.cs adds its
    // own warmth-zone bonus on top of this — campfires are not sampled here
    // because the player tracks zone enter/exit directly.
    public float SampleAirTemperature(Vector3 worldPos)
    {
        return SampleAirTemperatureBreakdown(worldPos).Total;
    }

    public AirTemperatureSample SampleAirTemperatureBreakdown(Vector3 worldPos)
    {
        AirTemperatureSample s = default;
        SkyController sky = SkyController.Current;
        if (sky == null) { s.air = 64.4f; return s; }
        WeatherData weather = sky.Weather;
        if (weather == null) { s.air = 64.4f; return s; }

        s.air = weather.airTemperature;
        s.sunTemperature = weather.sunTemperature;
        s.sunFactor = sky.SunFactor;
        s.cloudCover = weather.cloudCover;
        s.fog = sky.Palette.Fog;
        // Atmospheric attenuation. Cloud cover (weather) and fog (palette,
        // derived from humidity + cool diurnal) each occlude the sun
        // independently; their sum is clamped to 1 so a fully overcast OR
        // fully foggy sky drives the multiplier to 0 without going negative
        // when both pile up.
        s.skyTransmission = 1f - Mathf.Clamp(s.cloudCover + s.fog, 0f, 1f);

        s.sunMask = 1f;
        if (_worldState != null)
        {
            s.sunMask = _worldState.GetSkyLight01(worldPos);
        }
        return s;
    }

    // Constants for the sun-reach raycast. Origin is offset off the surface so
    // a query point sitting on a face doesn't self-hit. Distance only needs to
    // clear nearby occluders (cliffs, tree trunks, cave roofs) — the sun is
    // infinitely far but a few dozen voxels of clearance is enough to know
    // whether we're in the open.
    private const float SUN_RAY_ORIGIN_OFFSET = 0.05f;
    private const float SUN_RAY_DISTANCE = 64f;

    // True if a ray cast from `pos` toward the sun reaches open sky without
    // hitting environment geometry. Mirrors the directional-shadow term used
    // by the shaders; gameplay code should call this per-actor (not per-voxel)
    // because each call costs one Jolt query.
    public bool IsPointInDirectionalSun(Vector3 pos)
    {
        Vector3 toSun = -_worldState.ShadowLightDirection;
        Vector3 from = pos + toSun * SUN_RAY_ORIGIN_OFFSET;
        Vector3 to = from + toSun * SUN_RAY_DISTANCE;
        using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Solid);
        var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return result.Count == 0;
    }

    // Convenience: perceived brightness at `pos` matching the shader model,
    // including the directional-shadow term. Skip the raycast with
    // `checkDirectionalShadow = false` for cheap callers (e.g. plant growth)
    // that don't care whether direct sun is geometrically blocked.
    public float GetPerceivedLight(Vector3 pos, bool checkDirectionalShadow = true)
    {
        bool inSun = !checkDirectionalShadow || IsPointInDirectionalSun(pos);
        return _worldState.GetPerceivedLightWorld(pos, inSun);
    }

    // How much DIRECT SUN hits `pos` [0, 1+]: sun elevation factor (0 at night,
    // up to 1 at noon) × open-sky exposure (0 under a roof / canopy / in a cave, 1
    // in the open). > 0 means a sun-vulnerable mob (slime) would take sunburn here
    // — and, crucially, this is independent of cloud/fog dimming, so a cloudy-day
    // clearing that reads DIM to perceived-light still counts as a burn spot. The
    // single source of truth shared by the sunburn DoT (Mob.TickSunburn), the
    // darkness-dwell gate, and the night spawn-location gate so all three agree on
    // where the sun burns.
    public float SunBurnExposure(Vector3 pos)
    {
        float sunFactor = SkyController.Current?.SunFactor ?? 0f;
        if (sunFactor <= 0f)
        {
            return 0f;
        }
        return sunFactor * _worldState.GetSkyExposure01(pos);
    }

    // Continuous "shade" factor [0,1] at `pos`: 1 in true shade / at night (no sun
    // burns here), falling to 0 as direct-sun exposure reaches
    // SimData.sunShadeFullExposure. The natural, falloff-based counterpart to the
    // sunburn DoT — multiply it into darkness buildup and slime spawn weight so
    // both smoothly vanish exactly where the sun starts cooking slimes (weight 0 in
    // real sun = never placed there), rather than a hard boolean gate. Independent
    // of cloud/fog dimming (keys on sun elevation), so a dim-but-open cloudy noon
    // still reads as full sun and zero shade.
    public float SunShade01(Vector3 pos)
    {
        float full = _worldState.SimData?.sunShadeFullExposure ?? 0.15f;
        float exposure = SunBurnExposure(pos);
        if (full <= 0f)
        {
            return exposure > 0f ? 0f : 1f;
        }
        return 1f - Mathf.Clamp(exposure / full, 0f, 1f);
    }

    // Downward physics ray to find the ground surface under `query`: casts from
    // `heightAbove` above the point straight down to `depthBelow` below it and
    // returns the first Solid hit in `ground`. Returns false off the map / over a
    // gap (no hit) so callers can skip.
    //
    // This is the OUTDOOR surface finder: a ray from the sky returns the FIRST
    // surface from above = the terrain top, which is WRONG under a roof / in a
    // cave (it catches the overhead terrain, not the floor you're standing on).
    // Use it ONLY for open-sky-only effects (weather strikes, particle placement).
    // For any spawn / placement that must also work indoors use the nav-grid family
    // instead — NavigationGoals.CollectStandableCells / WalkabilityGrid.SampleColumn.
    public bool TryFindGroundByRaycast(Vector3 query, out Vector3 ground, float heightAbove = 40f, float depthBelow = 40f)
    {
        ground = default;
        World3D world3D = GetWorld3D();
        if (world3D == null)
        {
            return false;
        }
        Vector3 from = new Vector3(query.X, query.Y + heightAbove, query.Z);
        Vector3 to = new Vector3(query.X, query.Y - depthBelow, query.Z);
        using var rayQuery = PhysicsRayQueryParameters3D.Create(from, to);
        rayQuery.CollisionMask = (uint)ECollisionLayer.Solid;
        rayQuery.CollideWithBodies = true;
        rayQuery.CollideWithAreas = false;
        var result = world3D.DirectSpaceState.IntersectRay(rayQuery);
        if (result.Count == 0)
        {
            return false;
        }
        ground = (Vector3)result["position"];
        return true;
    }
}
