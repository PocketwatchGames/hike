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
//      and discards when above camera_clip so rain doesn't paint on cutaway-hidden
//      ceilings. This means rain outside a building still reads correctly when
//      the player is standing indoors looking out.
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
    // real splashes but with a known-good opaque material + simple mesh —
    // if these render at splash hit points while real splashes don't, the
    // bug is in the splash material/mesh, not the emission logic.
    [Export] public GpuParticles3D debugImpactMarkers;

    // Radius around the player within which splashes can spawn. Needs to cover
    // the full visible ground area at the iso zoom (~35 m wide × 22 m deep on a
    // 480×270 SubViewport), so 24 m radius gives comfortable coverage with a
    // small overshoot for camera rotation / elevation changes.
    [Export] public float splashRadius = 24f;
    // At full rainIntensity, target splashes-per-second across the visible area.
    // Scaled with splashRadius (density ≈ splashesPerSecond / π·r² per m²).
    // 500 at r=24 ≈ 0.28 splashes/m²/sec — dense enough to read as rain on
    // the ground without being a wall of particles.
    [Export] public float splashesPerSecond = 500f;
    // Caps per-frame raycasts so a single slow frame (or a huge budget ramp)
    // can't spike physics queries. Extra budget carries into the next frame.
    [Export] public int maxSplashesPerFrame = 40;
    // Splash speed range (m/s). Each splash picks a random value in [min, max]
    // and multiplies the reflected direction by it. Lets you tune splash
    // "snappiness" without recomputing the reflection math.
    [Export] public float splashSpeedMin = 2f;
    [Export] public float splashSpeedMax = 4f;
    // 0 = perfectly reflect incoming rain direction off the surface normal.
    // 1 = fully random hemisphere around the normal. Keeps some direction
    // variety so splashes off the same flat patch don't all look identical.
    [Export(PropertyHint.Range, "0,1,0.01")] public float splashSpread = 0.35f;
    // The direction rain is falling. Straight down until we hook angled rain
    // into the weather system — at which point this gets driven from the
    // wind-biased rain velocity.
    [Export] public Vector3 rainIncomingDir = new Vector3(0f, -1f, 0f);
    // When true, prints a one-shot confirmation on ready + a throttled status
    // line each second showing intensity / outdoors / camera voxel / splash
    // stats. Flip off once the effect is tuned; kept around because the
    // emission path has several gates (weather lerp, outdoors mask, raycast)
    // and it's hard to tell from visuals alone which one is blocking.
    [Export] public bool debugLog = true;
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

    // Diagnostic counters reset every Process tick, printed every ~1 second.
    private float _debugStatusTimer;
    private int _debugSplashAttempts;
    private int _debugSplashSpawns;
    private int _debugSplashMissRay;
    private int _debugSplashMissNormal;
    private Vector3 _debugLastSplashPos;
    private int _debugMarkersEmittedThisTick;
    private bool _debugIntensityFired;

    public override void _Ready()
    {
        Current = this;
        _rng.Randomize();

        // Splash emitter is driven by EmitParticle (from splash-raycast hits) —
        // never by natural emission. But `emitting = false` appears to cause
        // Godot 4 to skip processing entirely on some GPU particle systems, so
        // EmitParticle pushes particles into a buffer that never renders.
        // Force the system active with amount_ratio = 0 so it processes but
        // spawns nothing naturally. Done here rather than in the scene file
        // because editor re-saves keep stripping the settings back to defaults.
        // debugForceNaturalSplashes switches to natural emission at the node
        // position — useful for isolating rendering issues from emit-path issues.
        // Godot 4 GPUParticles3D quirk: with AmountRatio = 0, the renderer
        // optimizes away the draw pass (no natural emission = nothing to draw),
        // and particles spawned via EmitParticle go into a buffer nothing reads.
        // Keeping AmountRatio = 1.0 preserves the full particle buffer and
        // keeps the compute/render pipeline live. The side-effect — natural
        // emission at the node's origin — is hidden by offsetting the node
        // far below the world. EmitParticle uses world-space xform.Origin
        // (Position flag) so manual spawns still land at their real hit
        // points regardless of where the emitter node sits.
        // extra_cull_margin on the scene's tscn is already 16384, well past
        // the -10000 offset, so world-space EmitParticle particles won't be
        // culled by the offset node's visibility AABB.
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

        if (debugLog)
        {
            GD.Print(
                $"[RainEffect] Ready. " +
                $"fallingParticles={(fallingParticles != null ? "wired" : "NULL")}, " +
                $"splashParticles={(splashParticles != null ? "wired" : "NULL")}, " +
                $"debugImpactMarkers={(debugImpactMarkers != null ? "wired" : "NULL")}, " +
                $"debugShowImpactMarkers={debugShowImpactMarkers}");
            if (debugImpactMarkers != null)
            {
                GD.Print(
                    $"[RainEffect] debugImpactMarkers state: " +
                    $"Emitting={debugImpactMarkers.Emitting}, AmountRatio={debugImpactMarkers.AmountRatio}, " +
                    $"Amount={debugImpactMarkers.Amount}, Lifetime={debugImpactMarkers.Lifetime}, " +
                    $"Visible={debugImpactMarkers.Visible}, ProcessMaterial={(debugImpactMarkers.ProcessMaterial != null ? "set" : "NULL")}, " +
                    $"DrawPass1={(debugImpactMarkers.DrawPass1 != null ? "set" : "NULL")}");
            }
        }
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    // Called by SkyController.Apply() every frame. `intensity` is the already-
    // lerped WeatherData.rainIntensity — this node just consumes it.
    public void SetIntensity(float intensity)
    {
        _intensity = Mathf.Clamp(intensity, 0f, 1f);
        if (debugLog && !_debugIntensityFired && _intensity > 0f)
        {
            _debugIntensityFired = true;
            GD.Print($"[RainEffect] SetIntensity first non-zero: {_intensity:F3}");
        }
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
        Vector3 anchorPos;
        if (worldReady)
        {
            Vector3 pp = world.player.GlobalPosition;
            anchorPos = new Vector3(pp.X, pp.Y + AnchorHeightAbovePlayer, pp.Z);
            GlobalPosition = anchorPos;
        }
        else
        {
            anchorPos = GlobalPosition;
        }
        // Kill any inherited rotation from the pitched camera so the emission box
        // stays world-axis-aligned.
        GlobalRotation = Vector3.Zero;

        // Outdoors gate: sample sunlight at the PLAYER's voxel, not the anchor's.
        // The voxel sun mask only propagates within the world's Y bounds; the
        // anchor can easily sit above the top of the world (returning 0) even
        // when the player is in open sky.
        int psX = 0, psY = 0, psZ = 0, playerVoxelSun = -1;
        bool outdoors = false;
        if (worldReady)
        {
            Vector3 pp = world.player.GlobalPosition;
            psX = Mathf.FloorToInt(pp.X);
            psY = Mathf.FloorToInt(pp.Y);
            psZ = Mathf.FloorToInt(pp.Z);
            playerVoxelSun = ws.GetSunlightWorld(psX, psY, psZ);
            outdoors = playerVoxelSun > 0;
        }

        // Falling rain emits everywhere; the shader on its draw pass clips
        // per-fragment against the voxel sun mask + camera_clip, so drops under
        // roofs and inside the cutaway zone simply don't render. This preserves
        // rain visible through doorways/windows while the player is indoors.
        if (fallingParticles != null)
        {
            fallingParticles.AmountRatio = _intensity;
        }

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

        if (debugLog)
        {
            _debugStatusTimer += dt;
            if (_debugStatusTimer >= 1.0f)
            {
                _debugStatusTimer = 0f;
                float falling = fallingParticles != null ? fallingParticles.AmountRatio : -1f;
                string gate = !worldReady ? "NO_WORLD" : !outdoors ? "INDOORS" : _intensity <= 0f ? "ZERO_INTENSITY" : "OK";
                string lastSplash = _debugSplashSpawns > 0
                    ? $"lastSplash=({_debugLastSplashPos.X:F1},{_debugLastSplashPos.Y:F1},{_debugLastSplashPos.Z:F1})"
                    : "lastSplash=none";
                GD.Print(
                    $"[RainEffect] gate={gate} intensity={_intensity:F2} " +
                    $"anchor=({anchorPos.X:F1},{anchorPos.Y:F1},{anchorPos.Z:F1}) " +
                    $"playerVoxel=({psX},{psY},{psZ}) playerSun={playerVoxelSun} " +
                    $"fallAmountRatio={falling:F2} " +
                    $"splashes(spawns/rayMiss/normalMiss/attempts)=" +
                    $"{_debugSplashSpawns}/{_debugSplashMissRay}/{_debugSplashMissNormal}/{_debugSplashAttempts} " +
                    $"debugMarkersEmitted={_debugMarkersEmittedThisTick} " +
                    $"{lastSplash}");
                _debugSplashSpawns = 0;
                _debugSplashMissRay = 0;
                _debugSplashMissNormal = 0;
                _debugSplashAttempts = 0;
                _debugMarkersEmittedThisTick = 0;
            }
        }
    }

    private void TrySpawnSplash(Vector3 playerPos)
    {
        _debugSplashAttempts++;
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
        if (hit.Count == 0) { _debugSplashMissRay++; return; }

        Vector3 normal = (Vector3)hit["normal"];
        if (normal.Y < SplashNormalUpThreshold) { _debugSplashMissNormal++; return; }

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
        _debugSplashSpawns++;
        _debugLastSplashPos = point;

        if (debugShowImpactMarkers && debugImpactMarkers != null)
        {
            debugImpactMarkers.EmitParticle(
                xform,
                Vector3.Zero,
                new Color(1f, 0f, 0f, 1f),
                new Color(0f, 0f, 0f, 0f),
                (uint)GpuParticles3D.EmitFlags.Position);
            _debugMarkersEmittedThisTick++;
        }
    }
}
