using System.Collections.Generic;
using Godot;

// A single lightning strike at a world position. Spawned by
// WeatherLightningSpawner (weather-driven) or by any caller that wants
// to drop a strike at an arbitrary location (a weapon, a scripted event,
// the debug CVar). Lives long enough to run a three-phase lifecycle —
// warning → strike → fade — then frees itself.
//
// All authored content (bolt sprite, warning fx, strike fx, damage,
// flash amplitude, etc.) lives on a LightningData resource passed in
// at Create time. The C# class is just the runtime conductor.
//
// During the warning phase the strike WANDERS horizontally — a
// continuous Perlin noise field drives a 2 m/s meander so the preview
// hunts erratically across the ground. If a player or mob crosses
// inside `targetingRadiusMeters` the wander locks onto the closest
// target instead, biasing the strike toward whatever stepped into
// the kill zone. Position changes propagate to the warning fx (a
// child node) and the bolt automatically, so the visible preview
// tracks the moving strike point.
[GlobalClass]
public partial class LightningStrike : Node3D
{
    // Path to the strike scene. The scene root has this script
    // attached and a child "Bolt" MeshInstance3D (the y-billboarded
    // bolt visual). Warning/strike fx are NOT children of the scene
    // — they're per-strike PackedScenes on LightningData spawned via
    // Fx.Create at runtime so a different LightningData can swap
    // them without re-authoring the strike scene.
    private const string SCENE_PATH = "res://scenes/effects/lightning_strike.tscn";

    // Vertical raycast envelope used to keep the wandering strike
    // snapped to the ground each tick. Generous on both sides so
    // small terrain steps (single-voxel ledges) and overhangs don't
    // make the strike lose its ground sample.
    private const float GROUND_RAY_HEIGHT_OFFSET = 40f;
    private const float GROUND_RAY_DEPTH_OFFSET = 40f;

    // Bolt visibility uses MeshInstance3D.Transparency (0 = opaque,
    // 1 = invisible) — built-in per-instance fade that doesn't
    // require touching the shared material.
    [Export] private MeshInstance3D _bolt;

    private LightningData _data;
    private World _world;
    private float _phaseTimer;
    private EPhase _phase;
    private Fx _warningFx;
    // Low-intensity rumble registered with GameCamera.Shake during the
    // warning window. Parented to `this` so the source's GlobalPosition
    // tracks the wandering strike automatically — distance falloff
    // against the player updates each frame in the shake driver.
    // Freed on FireStrike so the rumble doesn't bleed past the crack.
    private ContinuousCameraShake _warmupShake;
    private FastNoiseLite _wanderNoise;
    private float _wanderTime;
    // Per-instance scratch list reused each wander tick so target
    // queries don't allocate. List itself is small (mob density per
    // 8m XZ disk is rarely more than a handful).
    private readonly List<Mob> _mobBuffer = new();

    private enum EPhase
    {
        Warning,    // warning fx running, bolt hidden
        Visible,    // bolt at full opacity post-strike
        Fading,     // bolt opacity lerping to 0
    }

    // Spawn a strike at the given world position. The strike spends
    // `warningDurationSeconds` telegraphing itself, then fires
    // (flash + damage + screen flash + bolt visible), then fades and
    // frees. Returns null if data is missing or the scene fails to
    // load — strikes are best-effort, never a hard error.
    public static LightningStrike Create(World world, Vector3 position, LightningData data)
    {
        if (world == null || data == null)
        {
            return null;
        }
        var scene = GD.Load<PackedScene>(SCENE_PATH);
        if (scene == null)
        {
            GD.PushWarning($"LightningStrike: failed to load {SCENE_PATH}");
            return null;
        }
        var strike = scene.Instantiate<LightningStrike>();
        strike._data = data;
        strike._world = world;
        world.AddChild(strike);
        strike.GlobalPosition = position;
        return strike;
    }

    public override void _Ready()
    {
        if (_bolt != null)
        {
            _bolt.Visible = false;
            _bolt.Transparency = 1f;
        }
        if (_data == null)
        {
            // Defensive: scene was instantiated without going through
            // Create() (e.g. dropped into a scene by an editor user).
            // Free silently rather than crashing — no data means
            // nothing meaningful to do.
            QueueFree();
            return;
        }

        _phase = EPhase.Warning;
        _phaseTimer = _data.warningDurationSeconds;
        if (_data.warningFx != null)
        {
            _warningFx = Fx.Create(_data.warningFx, this, Vector3.Zero);
        }
        if (_data.warmupShakeMagnitude > 0f && _data.cameraShakeFalloffMeters > 0f)
        {
            _warmupShake = new ContinuousCameraShake
            {
                magnitude = _data.warmupShakeMagnitude,
                range = _data.cameraShakeFalloffMeters,
            };
            AddChild(_warmupShake);
        }

        // Per-strike noise seed so two strikes that spawn on the
        // same frame wander in different directions instead of
        // marching in lockstep.
        _wanderNoise = new FastNoiseLite();
        _wanderNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        _wanderNoise.Frequency = _data.wanderNoiseFrequency;
        _wanderNoise.Seed = (int)GD.Randi();
    }

    public override void _Process(double delta)
    {
        if (_data == null)
        {
            return;
        }

        _phaseTimer -= (float)delta;

        switch (_phase)
        {
            case EPhase.Warning:
                UpdateWander((float)delta);
                if (_phaseTimer <= 0f)
                {
                    FireStrike();
                    _phase = EPhase.Visible;
                    _phaseTimer = _data.boltVisibleSeconds;
                }
                break;
            case EPhase.Visible:
                if (_phaseTimer <= 0f)
                {
                    _phase = EPhase.Fading;
                    _phaseTimer = _data.boltFadeSeconds;
                }
                break;
            case EPhase.Fading:
                if (_data.boltFadeSeconds > 0f && _bolt != null)
                {
                    float t = 1f - Mathf.Clamp(_phaseTimer / _data.boltFadeSeconds, 0f, 1f);
                    _bolt.Transparency = t;
                }
                if (_phaseTimer <= 0f)
                {
                    QueueFree();
                }
                break;
        }
    }

    private void FireStrike()
    {
        // Stop the warning fx so its rumble doesn't bleed past the
        // crack. Loop-mode Fx.Stop is the right call; for one-shot
        // warnings it's a no-op past their natural end.
        if (_warningFx != null && GodotObject.IsInstanceValid(_warningFx))
        {
            _warningFx.Stop();
        }

        // End the warmup rumble before the impulse kicks in — _ExitTree
        // on the shake source unregisters it from the driver so the
        // strike's big impulse stands alone instead of stacking on
        // residual rumble.
        if (_warmupShake != null && GodotObject.IsInstanceValid(_warmupShake))
        {
            _warmupShake.QueueFree();
            _warmupShake = null;
        }

        // Large camera impulse at the strike point. Linear distance
        // falloff against the player matches the warmup rumble's
        // range, so a strike that rumbled feebly far away also kicks
        // feebly when it cracks.
        if (_data.strikeShakeMagnitude > 0f
            && _data.strikeShakeDuration > 0f
            && _data.cameraShakeFalloffMeters > 0f)
        {
            GameCamera cam = GameCamera.Current;
            Player player = _world?.player;
            if (cam != null && player != null)
            {
                cam.Shake.AddImpulse(
                    _data.strikeShakeMagnitude,
                    _data.strikeShakeDuration,
                    GlobalPosition,
                    _data.cameraShakeFalloffMeters,
                    player.GlobalPosition);
            }
        }

        // World lights brighten via the shared flasher — same path
        // the distant horizon flashes use, so SkyController's
        // existing per-frame sample naturally picks it up.
        LightningFlasher.Current?.TriggerFlash(_data.flashAmplitude);

        // Strike-moment fx: parented to GetParent() (NOT this) so the
        // particles & thunder crack outlive the strike's own fade —
        // QueueFree on the strike would otherwise pull the fx down
        // with it mid-burst.
        if (_data.strikeFx != null)
        {
            Node parent = GetParent();
            if (parent != null)
            {
                Fx.Create(_data.strikeFx, parent, GlobalPosition);
            }
        }

        if (_bolt != null)
        {
            _bolt.Visible = true;
            _bolt.Transparency = 0f;
        }

        ApplyRadialDamage();
        TriggerScreenFlash();
    }

    // Sphere overlap query against the HurtBox layer. SphereShape3D
    // is allocated per-strike rather than cached because strikes are
    // rare (peak storm cadence is single-digit per second across the
    // whole world) and one-shot resource allocation is cheaper than
    // the dictionary book-keeping a pool would need. Hits are
    // distinct from a Projectile sweep: we don't care about
    // line-of-sight, only proximity — lightning ignores cover.
    private void ApplyRadialDamage()
    {
        if (_data.damage == null || _data.damageRadiusMeters <= 0f)
        {
            return;
        }
        World3D world3D = GetWorld3D();
        if (world3D == null)
        {
            return;
        }
        var space = world3D.DirectSpaceState;

        var shape = new SphereShape3D();
        shape.Radius = _data.damageRadiusMeters;

        using var query = new PhysicsShapeQueryParameters3D();
        query.Shape = shape;
        query.Transform = new Transform3D(Basis.Identity, GlobalPosition);
        // Lightning ignores cover, so it also ignores burrow depth — strikes
        // reach underground hurtboxes the same as surface ones. Default
        // weapons mask HurtBox only and naturally skip the BurrowedHurtBox
        // layer; lightning opts in explicitly.
        query.CollisionMask = (uint)(ECollisionLayer.HurtBox | ECollisionLayer.BurrowedHurtBox);
        query.CollideWithAreas = true;
        query.CollideWithBodies = false;

        var results = space.IntersectShape(query, 32);
        for (int i = 0; i < results.Count; i++)
        {
            var entry = results[i];
            if (!entry.TryGetValue("collider", out Variant colliderVariant))
            {
                continue;
            }
            if (colliderVariant.Obj is not HurtBox hb)
            {
                continue;
            }
            // Knockback direction is straight up — a lateral push
            // would imply the strike came from a direction, which it
            // didn't. Receivers strip Y by convention, so the actual
            // impulse ends up zero (the strike's hitstun + dizzy buildup do
            // the work).
            var hit = new HitInfo(_data.damage, this, Vector3.Up);
            hb.Hit(hit);
        }
    }

    // Horizontal drift during the warning window. Picks between two
    // movement modes per tick: SEEK the closest player/mob inside
    // `targetingRadiusMeters` if one exists, otherwise WANDER along
    // a continuous Perlin vector. Both modes move at the same speed
    // (`wanderSpeedMetersPerSecond`) so a target stepping into range
    // doesn't visibly snap the strike's pace, only its direction.
    // After horizontal move, raycast back down to keep the strike
    // glued to the terrain — without this the preview floats over
    // dips and rises.
    private void UpdateWander(float delta)
    {
        if (_data.wanderSpeedMetersPerSecond <= 0f)
        {
            return;
        }
        _wanderTime += delta;

        Vector3 dir;
        if (TryFindClosestTarget(out Vector3 targetPos))
        {
            Vector3 toTarget = targetPos - GlobalPosition;
            toTarget.Y = 0f;
            if (toTarget.LengthSquared() < 1e-4f)
            {
                return;
            }
            dir = toTarget.Normalized();
        }
        else
        {
            // Sample two well-separated rows of 2D noise so X and Z
            // components are uncorrelated — sampling (t, 0) and
            // (0, t) on a single noise field would tie the components
            // together along the diagonal.
            float nx = _wanderNoise.GetNoise2D(_wanderTime, 0f);
            float nz = _wanderNoise.GetNoise2D(_wanderTime, 1000f);
            Vector3 raw = new Vector3(nx, 0f, nz);
            if (raw.LengthSquared() < 1e-4f)
            {
                return;
            }
            dir = raw.Normalized();
        }

        Vector3 candidate = GlobalPosition + dir * _data.wanderSpeedMetersPerSecond * delta;
        if (TryFindGround(candidate, out Vector3 ground))
        {
            GlobalPosition = ground;
        }
    }

    // Closest player-or-mob within targetingRadiusMeters (XZ
    // distance). Returns false if targeting is disabled, the world
    // isn't available, or nothing is in range — caller falls back
    // to noise-driven wander.
    private bool TryFindClosestTarget(out Vector3 targetPos)
    {
        targetPos = default;
        if (_data.targetingRadiusMeters <= 0f || _world == null)
        {
            return false;
        }
        float radius = _data.targetingRadiusMeters;
        float bestDistSq = radius * radius;
        bool found = false;
        Vector3 origin = GlobalPosition;

        Player player = _world.player;
        if (player != null)
        {
            Vector3 p = player.GlobalPosition;
            float dx = p.X - origin.X;
            float dz = p.Z - origin.Z;
            float d2 = dx * dx + dz * dz;
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                targetPos = p;
                found = true;
            }
        }

        _mobBuffer.Clear();
        _world.MobSpatialHash?.QueryRadius(origin, radius, _mobBuffer);
        for (int i = 0; i < _mobBuffer.Count; i++)
        {
            Mob m = _mobBuffer[i];
            if (m == null || !GodotObject.IsInstanceValid(m))
            {
                continue;
            }
            // Burrowed mobs are damaged on coincidental overlap (the
            // radial query masks BurrowedHurtBox), but the wander seek
            // shouldn't actively home in on something underground — the
            // strike has no surface cue to "see" them. Skip them as
            // target candidates; the spatial hash isn't layer-aware.
            if (m.burrowed)
            {
                continue;
            }
            Vector3 p = m.GlobalPosition;
            float dx = p.X - origin.X;
            float dz = p.Z - origin.Z;
            float d2 = dx * dx + dz * dz;
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                targetPos = p;
                found = true;
            }
        }
        return found;
    }

    // Vertical ground probe used to re-snap Y after each horizontal
    // wander step. Mirrors WeatherLightningSpawner's spawn-time
    // version — same Environment mask, same downward sweep — just
    // run continuously instead of once. Returns false off the map
    // so we don't drift the strike into thin air.
    private bool TryFindGround(Vector3 query2d, out Vector3 ground)
    {
        ground = default;
        World3D world3D = _world?.GetWorld3D();
        if (world3D == null)
        {
            return false;
        }
        Vector3 from = new Vector3(query2d.X, query2d.Y + GROUND_RAY_HEIGHT_OFFSET, query2d.Z);
        Vector3 to = new Vector3(query2d.X, query2d.Y - GROUND_RAY_DEPTH_OFFSET, query2d.Z);
        using var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.CollisionMask = (uint)ECollisionLayer.Solid;
        query.CollideWithBodies = true;
        query.CollideWithAreas = false;
        var result = world3D.DirectSpaceState.IntersectRay(query);
        if (result.Count == 0)
        {
            return false;
        }
        ground = (Vector3)result["position"];
        return true;
    }

    // Screen flash intensity falls off linearly from
    // screenFlashMaxIntensity at the strike point to 0 at
    // screenFlashFalloffMeters. Routed through Hud.Current so the
    // overlay's fade-out lives on the HUD and survives the strike
    // node freeing itself.
    private void TriggerScreenFlash()
    {
        Hud hud = Hud.Current;
        Player player = _world?.player;
        if (hud == null || player == null)
        {
            return;
        }
        float falloff = _data.screenFlashFalloffMeters;
        if (falloff <= 0f)
        {
            return;
        }
        float dist = player.GlobalPosition.DistanceTo(GlobalPosition);
        float t = 1f - Mathf.Clamp(dist / falloff, 0f, 1f);
        if (t <= 0f)
        {
            return;
        }
        float intensity = _data.screenFlashMaxIntensity * t;
        hud.TriggerLightningFlash(intensity, _data.screenFlashFadeOutSeconds);
    }
}
