using Godot;

// Makes a plant's foliage react when it is disturbed: brushed by a walker
// (its PorousBody, or a BrushArea on a plant you walk through) or struck by an
// attack (its HurtBox). The canopy leans away,
// shivers, and springs back, with a rustle cue. Purely presentational (wall
// clock); processes only while disturbed.
//
// Drives two kinds of foliage: baked card canopies (FoliageMultiMesh, per
// material) and plain plant meshes on a MODEL_FOLIAGE_SWAY shader (per-instance
// uniforms, since those share one material across every plant of a kind).
[GlobalClass]
public partial class FoliageRustle : Node3D
{
    [Export] private FoliageRustleData _data;
    // Any input may be unset, but not all: a solid plant is brushed through its
    // body, a walk-through one through its brush area, and a plant nobody
    // attacks needs no hurtbox.
    [Export] private PorousBody _body;
    [Export] private BrushArea _brushArea;
    // Should sit on ECollisionLayer.Debris, so only the every-overlap attack
    // queries reach it and it never takes a hit or the reticle from a mob.
    [Export] private HurtBox _hurtBox;
    [Export] private FoliageMultiMesh[] _foliage = System.Array.Empty<FoliageMultiMesh>();
    [Export] private GeometryInstance3D[] _meshes = System.Array.Empty<GeometryInstance3D>();

    private static readonly StringName RustleLeanParam = "rustle_lean";
    private static readonly StringName RustleFlutterParam = "rustle_flutter";
    private static readonly StringName RustleFlutterHzParam = "rustle_flutter_hz";

    private const float SettleEpsilon = 0.0005f;

    private Vector3 _lean;
    private Vector3 _leanVelocity;
    private Vector3 _contactLean;
    private float _flutter01;
    private float _contactFlutter01;
    private double _contactUntil = -1.0;
    private double _nextCueAt;
    private bool _wasInContact;

    public override void _Ready()
    {
        SetProcess(false);
        if (_data == null || (_body == null && _brushArea == null && _hurtBox == null))
        {
            GD.PushError($"FoliageRustle '{GetPath()}': needs data and a body, brush area or hurtbox.");
            return;
        }
        if (_body != null)
        {
            _body.Brushed += OnBrushed;
        }
        if (_brushArea != null)
        {
            _brushArea.Brushed += OnBrushed;
        }
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnStruck;
        }
    }

    public override void _ExitTree()
    {
        if (_body != null)
        {
            _body.Brushed -= OnBrushed;
        }
        if (_brushArea != null)
        {
            _brushArea.Brushed -= OnBrushed;
        }
    }

    private static double Now()
    {
        return Time.GetTicksMsec() / 1000.0;
    }

    private void OnBrushed(Vector3 pushDir, float speed, Vector3 point)
    {
        float speed01 = Mathf.Clamp(speed / Mathf.Max(_data.fullSpeed, 0.01f), 0f, 1f);
        double now = Now();
        bool fresh = now > _contactUntil;
        if (fresh)
        {
            _contactLean = Vector3.Zero;
            _contactFlutter01 = 0f;
        }
        // Several contacts in one tick (or two walkers): keep the strongest.
        Vector3 lean = pushDir * (_data.maxLean * speed01);
        if (lean.LengthSquared() > _contactLean.LengthSquared())
        {
            _contactLean = lean;
        }
        _contactFlutter01 = Mathf.Max(_contactFlutter01, speed01);
        _contactUntil = now + _data.contactHoldSeconds;

        if (speed01 >= _data.cueMinSpeed01 && (fresh && !_wasInContact || now >= _nextCueAt))
        {
            PlayCue(point, now);
        }
        _wasInContact = true;
        SetProcess(true);
    }

    private void OnStruck(HitInfo hit)
    {
        // A lingering zone (a gas cloud) hits every physics tick; kicking on each
        // would pin the plant flat for as long as it lasts.
        if (hit.dot)
        {
            return;
        }
        Vector3 dir = hit.KnockbackDirection;
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.0001f)
        {
            // A blast centred on the plant: shiver without a lean.
            dir = Vector3.Zero;
        }
        else
        {
            dir = dir.Normalized();
        }
        _leanVelocity += dir * _data.hitKick;
        _flutter01 = 1f;
        double now = Now();
        if (now >= _nextCueAt)
        {
            PlayCue(hit.hitPoint ?? _hurtBox.Center, now);
        }
        SetProcess(true);
    }

    private void PlayCue(Vector3 point, double now)
    {
        if (_data.cueFx == null)
        {
            return;
        }
        Fx.Create(_data.cueFx, this, ToLocal(point));
        _nextCueAt = now + _data.cueCooldownSeconds;
    }

    public override void _Process(double delta)
    {
        float dt = Mathf.Min((float)delta, 1f / 20f);
        bool contact = Now() <= _contactUntil;
        _wasInContact = contact;

        Vector3 target = contact ? _contactLean : Vector3.Zero;
        // Semi-implicit Euler; dt is clamped so a hitch can't blow the spring up.
        Vector3 accel = (target - _lean) * _data.stiffness - _leanVelocity * _data.damping;
        _leanVelocity += accel * dt;
        _lean += _leanVelocity * dt;

        if (contact)
        {
            _flutter01 = Mathf.Max(_flutter01, _contactFlutter01);
        }
        else
        {
            _flutter01 = Mathf.Max(0f, _flutter01 - dt / Mathf.Max(_data.flutterDecaySeconds, 0.01f));
        }

        bool settled = !contact
            && _flutter01 <= 0f
            && _lean.LengthSquared() < SettleEpsilon * SettleEpsilon
            && _leanVelocity.LengthSquared() < SettleEpsilon * SettleEpsilon;
        if (settled)
        {
            _lean = Vector3.Zero;
            _leanVelocity = Vector3.Zero;
            SetProcess(false);
        }

        float flutter = _flutter01 * _data.flutterAmplitude;
        for (int i = 0; i < _foliage.Length; i++)
        {
            FoliageMultiMesh f = _foliage[i];
            if (f != null && IsInstanceValid(f))
            {
                f.SetRustle(_lean, flutter, _data.flutterHz);
            }
        }
        for (int i = 0; i < _meshes.Length; i++)
        {
            GeometryInstance3D m = _meshes[i];
            if (m != null && IsInstanceValid(m))
            {
                m.SetInstanceShaderParameter(RustleLeanParam, _lean);
                m.SetInstanceShaderParameter(RustleFlutterParam, flutter);
                m.SetInstanceShaderParameter(RustleFlutterHzParam, _data.flutterHz);
            }
        }
    }
}
