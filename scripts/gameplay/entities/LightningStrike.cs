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

    // Bolt visibility uses MeshInstance3D.Transparency (0 = opaque,
    // 1 = invisible) — built-in per-instance fade that doesn't
    // require touching the shared material.
    [Export] private MeshInstance3D _bolt;

    private LightningData _data;
    private World _world;
    private float _phaseTimer;
    private EPhase _phase;
    private Fx _warningFx;

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
        query.CollisionMask = (uint)ECollisionLayer.HurtBox;
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
            // impulse ends up zero (the strike's hitstun + stun do
            // the work).
            var hit = new HitInfo(_data.damage, this, Vector3.Up);
            hb.Hit(hit);
        }
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
