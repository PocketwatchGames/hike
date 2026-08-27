using Godot;

// Camera-parented precipitation visuals: falling rain streaks + ground splashes
// around the player, and the snow that replaces them where the air is cold and
// the zone is dressed for it. SkyController.Apply() calls SetIntensity() every
// frame with the derived-palette rain and snow intensities (blended across the
// player's current zone mix), so transitions in/out as the player walks between
// zones fade smoothly without this node needing to know about zone blending.
//
// Rain and snow share this node rather than getting one each because everything
// hard here is common to both: the emitter anchoring above the player, the wind
// tilt, and above all the per-fragment sky-exposure occlusion that stops
// precipitation rendering under roofs and canopy. They differ only in the
// emitter tuning and the draw material. Near freezing both run at once, which is
// what sleet is.
//
// Two mechanisms hide rain in covered areas:
//   1. Falling streaks ALWAYS emit (the CPU just scales rate by rainIntensity).
//      Per-fragment occlusion is done in shaders/rain_drop.gdshader — each drop
//      samples the 3D voxel sun mask and discards when under a roof/overhang/cave,
//      and discards when its impact point is above camera_clip so rain doesn't
//      paint on cutaway-hidden ceilings. This means rain outside a building
//      still reads correctly when the player is standing indoors looking out.
//   2. Splashes raycast from HIGH ABOVE the player straight down to the ground.
//      The first hit is by definition the topmost surface at that XZ — rooftops
//      catch splashes, floors underneath them do not. The splash budget is
//      still gated on the player being outdoors so we don't waste raycasts
//      while indoors; the game can accept no splashes on outdoor ground while
//      the player is deep inside. (If you want splashes visible through a
//      doorway while the player is indoors, lift this gate and let the raycasts
//      + shader discard handle it.)
[GlobalClass]
public partial class RainEffect : Node3D
{
    // Mirror of SkyController.Current — SkyController.Apply() fetches this to
    // push the derived rain intensity. A NodePath/[Export] from SkyController
    // would be cleaner in principle, but Godot 4's C# binding fails the Node3D→
    // RainEffect cast when a scene is instanced in another scene (the root
    // arrives at property-set time typed as plain Node3D because the script
    // association runs later), so the editor silently strips the NodePath
    // wiring. A static ref avoids that binding path entirely.
    public static RainEffect Current { get; private set; }

    [Export] public GpuParticles3D fallingParticles;
    [Export] public GpuParticles3D splashParticles;
    // Falling snow. Emits on the same always-on / AmountRatio-scaled basis as
    // the rain streaks and clips per-fragment in shaders/snow_flake.gdshader.
    // Snow makes no splashes: a flake lands and stays, it does not burst.
    [Export] public GpuParticles3D snowParticles;
    // Optional diagnostic marker system. Same emit path (EmitParticle) as the
    // real splashes but with a known-good opaque material + simple mesh — if
    // these render at splash hit points while real splashes don't, the bug
    // is in the splash material/mesh, not the emission logic.
    [Export] public GpuParticles3D debugImpactMarkers;

    // Radius around the player within which splashes can spawn. Needs to cover
    // the full visible ground area at the iso zoom (~35 m wide × 22 m deep on a
    // 480×270 SubViewport), so 24 m radius gives comfortable coverage with a
    // small overshoot for camera rotation / elevation changes.
    [Export] public float splashRadius = 24f;
    // At full rainIntensity, target splashes-per-second across the visible area.
    // Scaled with splashRadius (density ≈ splashesPerSecond / π·r² per m²).
    [Export] public float splashesPerSecond = 500f;
    // Caps per-frame raycasts so a single slow frame (or a huge budget ramp)
    // can't spike physics queries. Extra budget carries into the next frame.
    [Export] public int maxSplashesPerFrame = 40;
    // Splash speed range (m/s). Each splash picks a random value in [min, max]
    // and multiplies the reflected direction by it.
    [Export] public float splashSpeedMin = 2f;
    [Export] public float splashSpeedMax = 4f;
    // 0 = perfectly reflect incoming rain direction off the surface normal.
    // 1 = fully random hemisphere around the normal. Keeps some direction
    // variety so splashes off the same flat patch don't all look identical.
    [Export(PropertyHint.Range, "0,1,0.01")] public float splashSpread = 0.35f;
    // Degrees of tilt-from-vertical applied per m/s of effective wind speed
    // (windSpeed + gust contribution). 0.85° per m/s gives a 30 m/s gale
    // gust ~25° of tilt — heavy wind without going horizontal.
    [Export(PropertyHint.Range, "0,3,0.01")] public float tiltDegPerMps = 0.85f;
    // Hard cap on rain tilt. Linear-with-wind-speed never asymptotes on its
    // own, so very high winds would spin rain past 45° and read as
    // unphysical. 45° = "blowing sideways"; 60° lets stormy still feel
    // tame at the cap. Sampled at spawn; rain re-tilts when wind weather
    // changes (call ApplyWindToFallingRain() from the change site).
    [Export(PropertyHint.Range, "0,75,0.5")] public float maxWindTiltDegrees = 45f;
    // Snow's tilt multiplier against the rain tilt computed from the same wind.
    // A flake has a huge drag-to-mass ratio, so it is carried far closer to
    // horizontal than a raindrop by the same wind — this is what makes a
    // blizzard read as driven rather than as heavy snowfall. > 1 by design; the
    // maxWindTiltDegrees clamp still applies afterward, so it saturates at
    // "blowing sideways" instead of running past it.
    [Export(PropertyHint.Range, "1,6,0.05")] public float snowWindTiltScale = 3.0f;
    // Hard cap on SNOW tilt, separately from rain's. Higher than the rain cap
    // because near-horizontal snow is a real and readable look where
    // near-horizontal rain is not.
    [Export(PropertyHint.Range, "0,89,0.5")] public float snowMaxWindTiltDegrees = 72f;
    // The direction rain is falling. Set from wind at _Ready and left static
    // after that. Splashes read this for their reflection; the falling
    // particles' process material has its Direction / InitialVelocity set
    // from the same calculation.
    [Export] public Vector3 rainIncomingDir = new Vector3(0f, -1f, 0f);
    // DIAGNOSTIC: flip on to bypass the EmitParticle pathway and let the splash
    // GPUParticles3D emit naturally from the node position (player feet). If
    // splashes become visible in this mode but NOT in normal mode, the
    // rendering pipeline is fine and the problem is with EmitParticle (flags,
    // timing, or velocity setup). If still invisible here, it's a rendering
    // issue (material, mesh, visibility_aabb, or culling).
    [Export] public bool debugForceNaturalSplashes = false;
    // DIAGNOSTIC: mirror each splash emit into the debugImpactMarkers system
    // (a plain opaque StandardMaterial3D + BoxMesh, nothing shared with the
    // splash material). Lets you visually confirm the impact positions and
    // isolate whether the splash invisibility is an emit-path issue or a
    // splash-material issue.
    [Export] public bool debugShowImpactMarkers = false;

    // World Y offset of the emission anchor above the player. Needs to be high
    // enough that FallingParticles (which has its own +15 local transform offset)
    // puts the emission plane comfortably above the player's head and the visible
    // terrain around them. Lifetime × initialVelocity determines fall distance;
    // together with this offset they decide how much vertical travel happens
    // before particles age out.
    private const float AnchorHeightAbovePlayer = 10f;
    // Height above the player from which splash raycasts start. Must exceed any
    // roof / overhang we want to detect as "covered ground".
    private const float SplashRayHeightAbovePlayer = 30f;
    private const float SplashRayMaxDistance = 60f;
    // Splash is rejected if the surface normal's Y component is below this.
    // 0.0 allows anything up to (but not including) a perfect wall — below 0 is
    // an overhang underside and would mean splashing off a ceiling, which is
    // never physically what we want. 0.05 keeps a small margin against nearly-
    // vertical slopes where the reflection reads as degenerate sideways spray.
    private const float SplashNormalUpThreshold = 0.05f;
    // Small upward nudge so the impact sky-exposure probe samples the air voxel
    // above the hit surface, not the solid voxel the ray landed on.
    private const float SplashExposureProbeUp = 0.5f;

    // Sky-exposure threshold a splash impact must clear to spawn — read from the
    // drop material's own `sky_exposure_threshold` so splashes use the EXACT
    // same gate as the falling drops: wherever a drop renders, a splash is
    // allowed. Fallback matches the shader default if the param can't be read.
    private float _dropSkyExposureThreshold = 0.5f;

    private float _intensity;
    private float _splashBudget;
    private RandomNumberGenerator _rng = new RandomNumberGenerator();
    // Runtime copy of the falling particles' process material. We mutate this
    // each frame to re-tilt rain as wind lerps — mutating the scene's shared
    // .tres directly would persist to disk on the next editor save.
    private ParticleProcessMaterial _fallProcRuntime;
    // Same duplicate-on-ready rationale as _fallProcRuntime: we rewrite
    // Direction every frame to re-tilt the snow into the wind, and mutating
    // the scene's shared .tres would persist that to disk on an editor save.
    private ParticleProcessMaterial _snowProcRuntime;
    private float _snowIntensity;

    // Public runtime material handles and cached baseline values. SkyController's
    // ApplyPrecipitation() scales these by the derived-palette rain weight every frame;
    // stashing the baseline here (instead of re-reading the authored .tres) lets
    // that scaling be a pure write and keeps the authored values untouched on
    // disk. Duplication happens in _Ready so the writes never leak back to the
    // scene's shared resources.
    public ParticleProcessMaterial FallProcRuntime => _fallProcRuntime;
    public ParticleProcessMaterial SnowProcRuntime => _snowProcRuntime;
    public ShaderMaterial DropMatRuntime { get; private set; }
    public ShaderMaterial SplashMatRuntime { get; private set; }
    public float BaseInitialVelocityMin { get; private set; }
    public float BaseInitialVelocityMax { get; private set; }
    public Color BaseDropAlbedo { get; private set; }
    public float BaseStreakLengthPx { get; private set; }
    public Color BaseSplashAlbedo { get; private set; }

    // 1 / rainWeight, written by SkyController.ApplyPrecipitation each frame.
    // Multiplies the wind-tilt computation in UpdateWindDrivenRainDirection so
    // heavier drops (weight > 1) are less wind-displaced and lighter drizzle
    // (weight < 1) blows harder sideways for the same wind. Held as a property
    // rather than re-derived locally so RainEffect doesn't have to know about
    // the palette's rain weight at all — the manager drives the number, the
    // consumer uses it.
    public float WindTiltScale { get; set; } = 1f;

    public override void _Ready()
    {
        Current = this;
        _rng.Randomize();

        // Splash emitter is driven by EmitParticle (from splash-raycast hits) —
        // never by natural emission. But Godot 4 GPUParticles3D with
        // AmountRatio = 0 (or emitting = false) optimizes away the draw pass
        // entirely: the compute shader doesn't run, and particles pushed via
        // EmitParticle go into a buffer nothing reads. Keeping AmountRatio = 1
        // preserves the full particle buffer and keeps the pipeline warm. The
        // side-effect — natural emission at the node's origin — is hidden by
        // offsetting the node far below the world. EmitParticle uses world-
        // space xform.Origin (Position flag) so manual spawns still land at
        // their real hit points regardless of where the emitter node sits.
        // extra_cull_margin on the scene is 16384, well past the -10000
        // offset, so world-space EmitParticle particles won't be culled by
        // the offset node's visibility AABB.
        //
        // debugForceNaturalSplashes switches to natural emission at the node
        // position — useful for isolating rendering issues from emit-path issues.
        Vector3 hiddenOffset = new Vector3(0, -10000, 0);
        if (splashParticles != null)
        {
            splashParticles.Emitting = true;
            splashParticles.AmountRatio = debugForceNaturalSplashes ? 0.5f : 1.0f;
            splashParticles.Position = debugForceNaturalSplashes ? Vector3.Zero : hiddenOffset;
        }
        if (debugImpactMarkers != null)
        {
            debugImpactMarkers.Emitting = true;
            debugImpactMarkers.AmountRatio = 1.0f;
            debugImpactMarkers.Position = hiddenOffset;
        }

        // Duplicate the falling particles' process material once so our per-frame
        // wind tilt (and SkyController's weight-scaled velocity writes) doesn't
        // mutate the scene's shared .tres on disk.
        if (fallingParticles?.ProcessMaterial is ParticleProcessMaterial fallProc)
        {
            _fallProcRuntime = (ParticleProcessMaterial)fallProc.Duplicate();
            fallingParticles.ProcessMaterial = _fallProcRuntime;
            BaseInitialVelocityMin = _fallProcRuntime.InitialVelocityMin;
            BaseInitialVelocityMax = _fallProcRuntime.InitialVelocityMax;
        }

        if (snowParticles?.ProcessMaterial is ParticleProcessMaterial snowProc)
        {
            _snowProcRuntime = (ParticleProcessMaterial)snowProc.Duplicate();
            snowParticles.ProcessMaterial = _snowProcRuntime;
        }

        // Same rationale for the drop shader material — SkyController scales its
        // albedo.a and streak_length_px by rainWeight every frame, and we don't
        // want those writes to persist back through an editor save of the shared
        // rain_drop.tres.
        if (fallingParticles?.DrawPass1 is PrimitiveMesh dropMesh
            && dropMesh.Material is ShaderMaterial dropMat)
        {
            DropMatRuntime = (ShaderMaterial)dropMat.Duplicate();
            dropMesh.Material = DropMatRuntime;
            BaseDropAlbedo = (Color)DropMatRuntime.GetShaderParameter("albedo");
            BaseStreakLengthPx = (float)DropMatRuntime.GetShaderParameter("streak_length_px");
            // Reuse the drops' own cover threshold for splash gating so the two
            // can never disagree — a splash is allowed exactly where a drop falls.
            if (DropMatRuntime.GetShaderParameter("sky_exposure_threshold").VariantType != Variant.Type.Nil)
            {
                _dropSkyExposureThreshold = (float)DropMatRuntime.GetShaderParameter("sky_exposure_threshold");
            }
        }

        // Splash material follows the same pattern — only its albedo.a gets
        // weight-scaled, so a 1/3-weight drizzle produces ground splashes that
        // read as fainter than a heavy downpour at the same intensity.
        if (splashParticles?.DrawPass1 is PrimitiveMesh splashMesh
            && splashMesh.Material is ShaderMaterial splashMat)
        {
            SplashMatRuntime = (ShaderMaterial)splashMat.Duplicate();
            splashMesh.Material = SplashMatRuntime;
            BaseSplashAlbedo = (Color)SplashMatRuntime.GetShaderParameter("albedo");
        }
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    // Sample wind and update the falling rain's direction + the splash
    // reflection vector. Called every frame from _Process so rain re-tilts
    // as weather lerps (wind speed/direction/gust continuously change as
    // SkyController blends weather presets). We mutate the pre-duplicated
    // _fallProcRuntime in place rather than re-duplicating each frame.
    private void UpdateWindDrivenRainDirection()
    {
        WorldState ws = Sim.Current?.WorldState;
        SkyController sky = SkyController.Current;
        WeatherData weather = sky?.Weather;
        if (ws == null || sky == null || weather == null) { return; }

        Vector3 windDir = ws.WindDirection;
        Vector2 windXZ = new Vector2(windDir.X, windDir.Z);
        if (windXZ.LengthSquared() < 1e-4f) { return; }
        windXZ = windXZ.Normalized();

        // Current gust wave, in [0, 1]. Same two-octave sum SkyController.Apply
        // uses for its wind_amplitude global, so rain tilt and grass sway
        // agree on "how gusty is right now". Gust amplitude (GustStrength)
        // is derived by WeatherDerivation from cloudCover+windSpeed, so we
        // read it from the palette rather than a raw weather field.
        float gustWave = Mathf.Sin(sky.gustPhase) * 0.7f
                       + Mathf.Sin(sky.gustPhase * 1.7f + 1.3f) * 0.3f;
        float gust01 = Mathf.Clamp((gustWave + 1f) * 0.5f, 0f, 1f);

        float gustedSpeed = weather.windSpeed + gust01 * sky.Palette.GustStrength;
        // WindTiltScale = 1 / rainWeight (written by SkyController). Heavy drops
        // cut the wind effect; drizzle amplifies it. Max-tilt clamp still runs
        // so extreme weight values can't rotate rain past physically readable.
        float tiltDeg = Mathf.Min(gustedSpeed * tiltDegPerMps * WindTiltScale, maxWindTiltDegrees);
        rainIncomingDir = TiltedFallDirection(windXZ, tiltDeg);

        if (_fallProcRuntime != null)
        {
            _fallProcRuntime.Direction = rainIncomingDir;
        }

        // Snow off the SAME wind, but far more of it: a flake has nothing like
        // a drop's terminal velocity, so it is pushed toward horizontal where a
        // drop is barely deflected. Its own clamp, deliberately looser.
        if (_snowProcRuntime != null)
        {
            float snowTiltDeg = Mathf.Min(
                gustedSpeed * tiltDegPerMps * snowWindTiltScale, snowMaxWindTiltDegrees);
            _snowProcRuntime.Direction = TiltedFallDirection(windXZ, snowTiltDeg);
        }
    }

    // Straight-down rotated toward `windXZ` by `tiltDeg`. Magnitude is 1 by
    // construction (sin² + cos² = 1 across the components).
    private static Vector3 TiltedFallDirection(Vector2 windXZ, float tiltDeg)
    {
        float tiltRad = Mathf.DegToRad(tiltDeg);
        float s = Mathf.Sin(tiltRad);
        return new Vector3(windXZ.X * s, -Mathf.Cos(tiltRad), windXZ.Y * s);
    }

    // Called by SkyController.Apply() every frame. Both values are the already-
    // blended derived intensities — this node just consumes them. They are
    // independent rather than one value plus a phase, so near freezing both can
    // be non-zero at once and the sky genuinely sleets.
    public void SetIntensity(float rainIntensity, float snowIntensity)
    {
        _intensity = Mathf.Clamp(rainIntensity, 0f, 1f);
        _snowIntensity = Mathf.Clamp(snowIntensity, 0f, 1f);
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("RainEffect.Process");
        float dt = (float)delta;
        Sim sim = Sim.Current;
        WorldState ws = sim?.WorldState;
        bool worldReady = sim != null && ws != null && sim.player != null;

        // Anchor the emitter above the PLAYER, not the camera. The iso camera sits
        // ~65m above the player; parenting rain to the camera means emission happens
        // far above where rain needs to land, and particles never reach the visible
        // area within their lifetime. The node is still a child of MainCamera for
        // scene-structure convenience, but we override its world position here so
        // the emission box sits a fixed distance above the player.
        if (worldReady)
        {
            Vector3 pp = sim.player.GlobalPosition;
            GlobalPosition = new Vector3(pp.X, pp.Y + AnchorHeightAbovePlayer, pp.Z);
        }
        // Kill any inherited rotation from the pitched camera so the emission box
        // stays world-axis-aligned.
        GlobalRotation = Vector3.Zero;

        // Falling rain emits everywhere; the shader on its draw pass clips
        // per-fragment against the sky-exposure field + camera_clip, so drops
        // under roofs / canopy and inside the cutaway zone simply don't render.
        // This preserves rain visible through doorways/windows while the player
        // is indoors.
        if (fallingParticles != null)
        {
            fallingParticles.AmountRatio = _intensity;
        }
        // Snow rides the identical always-emitting / ratio-scaled path, and
        // clips per-fragment in its own shader against the same sky-exposure
        // field, so it stops at a cave mouth exactly where the rain does.
        if (snowParticles != null)
        {
            snowParticles.AmountRatio = _snowIntensity;
        }

        // Re-tilt rain to match the current (already-lerped) wind. Must run
        // every frame — weather lerps continuously and rain should visibly
        // respond. Only the process material's Direction is updated; existing
        // particles keep their velocity, newly-spawned ones use the new angle.
        UpdateWindDrivenRainDirection();

        // Splash spawning is gated only on "is it raining" — NOT on the player's
        // own cover. Each candidate is sky-exposure-tested at its own impact
        // point inside TrySpawnSplash, so splashes keep landing on rained-on
        // ground (e.g. visible through a doorway) while the player stands inside,
        // and never land on covered floor even when the player is outdoors.
        if (worldReady && _intensity > 0f && splashParticles != null)
        {
            _splashBudget += _intensity * splashesPerSecond * dt;
            int spawnsThisFrame = 0;
            Vector3 playerPos = sim.player.GlobalPosition;

            while (_splashBudget >= 1f && spawnsThisFrame < maxSplashesPerFrame)
            {
                _splashBudget -= 1f;
                spawnsThisFrame++;
                TrySpawnSplash(ws, playerPos);
            }

            if (_splashBudget > maxSplashesPerFrame)
            {
                _splashBudget = maxSplashesPerFrame;
            }
        }
    }

    private void TrySpawnSplash(WorldState ws, Vector3 playerPos)
    {
        // Uniform-disc sample via sqrt of r so density is flat across the disc.
        float r = splashRadius * Mathf.Sqrt(_rng.Randf());
        float theta = _rng.Randf() * Mathf.Tau;
        float dx = r * Mathf.Cos(theta);
        float dz = r * Mathf.Sin(theta);

        float rayY = playerPos.Y + SplashRayHeightAbovePlayer;
        Vector3 from = new Vector3(playerPos.X + dx, rayY, playerPos.Z + dz);
        Vector3 to = from + new Vector3(0f, -SplashRayMaxDistance, 0f);

        var spaceState = GetWorld3D().DirectSpaceState;
        using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Solid);
        var hit = spaceState.IntersectRay(query);
        if (hit.Count == 0) { return; }

        Vector3 normal = (Vector3)hit["normal"];
        if (normal.Y < SplashNormalUpThreshold) { return; }

        Vector3 point = (Vector3)hit["position"];

        // Splash only where a drop would actually fall — same sky-exposure gate
        // the falling drops use (threshold sourced from their material), tested
        // at THIS impact point. So it's purely "is there rain here", independent
        // of player cover: outdoor ground keeps splashing while the player is
        // inside, and covered floor never splashes even when they're outside.
        // Probe a touch above the surface to sample the air voxel, not the solid.
        float impactExposure = ws.GetSkyExposure01(point + Vector3.Up * SplashExposureProbeUp);
        if (impactExposure < _dropSkyExposureThreshold) { return; }

        Transform3D xform = Transform3D.Identity;
        xform.Origin = point;

        // Reflect the rain's incoming direction across the surface normal —
        // that's the physically-correct splash direction for this hit.
        // r = d - 2(d·n)n
        Vector3 incoming = rainIncomingDir.Normalized();
        Vector3 reflected = incoming - 2f * incoming.Dot(normal) * normal;

        // Optionally blend toward a random hemisphere-of-normal direction so
        // splashes off a shared flat patch aren't all identical. Sample a
        // random unit vector and flip it to live in the normal's hemisphere.
        Vector3 jitter = new Vector3(
            _rng.Randf() * 2f - 1f,
            _rng.Randf() * 2f - 1f,
            _rng.Randf() * 2f - 1f).Normalized();
        if (jitter.Dot(normal) < 0f) { jitter = -jitter; }
        Vector3 direction = reflected.Lerp(jitter, splashSpread).Normalized();

        float speed = splashSpeedMin + _rng.Randf() * (splashSpeedMax - splashSpeedMin);
        Vector3 splashVel = direction * speed;

        // Position + Velocity both overridden: position is the exact hit point,
        // velocity is the reflected spray. The process material's Initial
        // Velocity Min/Max / Direction / Spread are ignored for manual emits
        // (they only drive the hidden-offset natural emission used to keep
        // the pipeline warm).
        splashParticles.EmitParticle(
            xform,
            splashVel,
            new Color(1f, 1f, 1f, 1f),
            new Color(0f, 0f, 0f, 0f),
            (uint)(GpuParticles3D.EmitFlags.Position | GpuParticles3D.EmitFlags.Velocity));

        if (debugShowImpactMarkers && debugImpactMarkers != null)
        {
            debugImpactMarkers.EmitParticle(
                xform,
                Vector3.Zero,
                new Color(1f, 0f, 0f, 1f),
                new Color(0f, 0f, 0f, 0f),
                (uint)GpuParticles3D.EmitFlags.Position);
        }
    }
}
