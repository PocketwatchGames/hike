using Godot;

// Camera-parented rain visuals: falling streaks above the view + ground splashes
// around the player. SkyController.Apply() calls SetIntensity() every frame with
// WeatherData.rainIntensity (already lerp-blended), so transitions in/out via
// LerpToWeather fade smoothly without this node needing to know about weather
// presets or blending state.
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
    // push WeatherData.rainIntensity. A NodePath/[Export] from SkyController
    // would be cleaner in principle, but Godot 4's C# binding fails the Node3D→
    // RainEffect cast when a scene is instanced in another scene (the root
    // arrives at property-set time typed as plain Node3D because the script
    // association runs later), so the editor silently strips the NodePath
    // wiring. A static ref avoids that binding path entirely.
    public static RainEffect Current { get; private set; }

    [Export] public GpuParticles3D fallingParticles;
    [Export] public GpuParticles3D splashParticles;
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

    private float _intensity;
    private float _splashBudget;
    private RandomNumberGenerator _rng = new RandomNumberGenerator();
    // Runtime copy of the falling particles' process material. We mutate this
    // each frame to re-tilt rain as wind lerps — mutating the scene's shared
    // .tres directly would persist to disk on the next editor save.
    private ParticleProcessMaterial _fallProcRuntime;

    // Public runtime material handles and cached baseline values. SkyController's
    // ApplyPrecipitation() scales these by WeatherData.rainWeight every frame;
    // stashing the baseline here (instead of re-reading the authored .tres) lets
    // that scaling be a pure write and keeps the authored values untouched on
    // disk. Duplication happens in _Ready so the writes never leak back to the
    // scene's shared resources.
    public ParticleProcessMaterial FallProcRuntime => _fallProcRuntime;
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
    // WeatherData.rainWeight at all — the manager drives the number, the
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
        WorldState ws = World.Current?.WorldState;
        SkyController sky = SkyController.Current;
        if (ws == null || sky == null || sky.weather == null) { return; }

        WeatherData weather = sky.weather;
        Vector3 windDir = ws.WindDirection;
        Vector2 windXZ = new Vector2(windDir.X, windDir.Z);
        if (windXZ.LengthSquared() < 1e-4f) { return; }
        windXZ = windXZ.Normalized();

        // Current gust wave, in [0, 1]. Same two-octave sum SkyController.Apply
        // uses for its wind_amplitude global, so rain tilt and grass sway
        // agree on "how gusty is right now".
        float gustWave = Mathf.Sin(sky.gustPhase) * 0.7f
                       + Mathf.Sin(sky.gustPhase * 1.7f + 1.3f) * 0.3f;
        float gust01 = Mathf.Clamp((gustWave + 1f) * 0.5f, 0f, 1f);

        float gustedSpeed = weather.windSpeed + gust01 * weather.gustStrength;
        // WindTiltScale = 1 / rainWeight (written by SkyController). Heavy drops
        // cut the wind effect; drizzle amplifies it. Max-tilt clamp still runs
        // so extreme weight values can't rotate rain past physically readable.
        float tiltDeg = Mathf.Min(gustedSpeed * tiltDegPerMps * WindTiltScale, maxWindTiltDegrees);
        float tiltRad = Mathf.DegToRad(tiltDeg);

        // Rain direction is straight-down rotated toward windXZ by tiltRad.
        // Magnitude = 1 by construction (sin² + cos² = 1 across the components).
        Vector3 rainDir = new Vector3(
            windXZ.X * Mathf.Sin(tiltRad),
            -Mathf.Cos(tiltRad),
            windXZ.Y * Mathf.Sin(tiltRad));
        rainIncomingDir = rainDir;

        if (_fallProcRuntime != null)
        {
            _fallProcRuntime.Direction = rainDir;
        }
    }

    // Called by SkyController.Apply() every frame. `intensity` is the already-
    // lerped WeatherData.rainIntensity — this node just consumes it.
    public void SetIntensity(float intensity)
    {
        _intensity = Mathf.Clamp(intensity, 0f, 1f);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        World world = World.Current;
        WorldState ws = world?.WorldState;
        bool worldReady = world != null && ws != null && world.player != null;

        // Anchor the emitter above the PLAYER, not the camera. The iso camera sits
        // ~65m above the player; parenting rain to the camera means emission happens
        // far above where rain needs to land, and particles never reach the visible
        // area within their lifetime. The node is still a child of MainCamera for
        // scene-structure convenience, but we override its world position here so
        // the emission box sits a fixed distance above the player.
        if (worldReady)
        {
            Vector3 pp = world.player.GlobalPosition;
            GlobalPosition = new Vector3(pp.X, pp.Y + AnchorHeightAbovePlayer, pp.Z);
        }
        // Kill any inherited rotation from the pitched camera so the emission box
        // stays world-axis-aligned.
        GlobalRotation = Vector3.Zero;

        // Outdoors gate: sample sunlight at the PLAYER's voxel. The voxel sun
        // mask only propagates within the world's Y bounds; the anchor can
        // easily sit above the top of the world (returning 0) even when the
        // player is in open sky.
        bool outdoors = false;
        if (worldReady)
        {
            Vector3 pp = world.player.GlobalPosition;
            int psX = Mathf.FloorToInt(pp.X);
            int psY = Mathf.FloorToInt(pp.Y);
            int psZ = Mathf.FloorToInt(pp.Z);
            outdoors = ws.GetSunlightWorld(psX, psY, psZ) > 0;
        }

        // Falling rain emits everywhere; the shader on its draw pass clips
        // per-fragment against the voxel sun mask + camera_clip, so drops under
        // roofs and inside the cutaway zone simply don't render. This preserves
        // rain visible through doorways/windows while the player is indoors.
        if (fallingParticles != null)
        {
            fallingParticles.AmountRatio = _intensity;
        }

        // Re-tilt rain to match the current (already-lerped) wind. Must run
        // every frame — weather lerps continuously and rain should visibly
        // respond. Only the process material's Direction is updated; existing
        // particles keep their velocity, newly-spawned ones use the new angle.
        UpdateWindDrivenRainDirection();

        if (outdoors && _intensity > 0f && splashParticles != null)
        {
            _splashBudget += _intensity * splashesPerSecond * dt;
            int spawnsThisFrame = 0;
            Vector3 playerPos = world.player.GlobalPosition;

            while (_splashBudget >= 1f && spawnsThisFrame < maxSplashesPerFrame)
            {
                _splashBudget -= 1f;
                spawnsThisFrame++;
                TrySpawnSplash(playerPos);
            }

            if (_splashBudget > maxSplashesPerFrame)
            {
                _splashBudget = maxSplashesPerFrame;
            }
        }
    }

    private void TrySpawnSplash(Vector3 playerPos)
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
        var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Environment);
        var hit = spaceState.IntersectRay(query);
        if (hit.Count == 0) { return; }

        Vector3 normal = (Vector3)hit["normal"];
        if (normal.Y < SplashNormalUpThreshold) { return; }

        Vector3 point = (Vector3)hit["position"];
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
