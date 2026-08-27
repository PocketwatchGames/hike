using Godot;

// A stationary desert hazard plant. Touching it (Player or Mob, via the wired
// TriggerSource) or striking it (its HurtBox) bristles for a warning beat and
// then erupts a ring of spines outward in every direction, each carrying hazard
// damage. Purely reactive — no perception, no disarm, no press-to-interact — so
// it composes from a TriggerSource (touch) + a HurtBox (struck) both routed
// into Arm().
//
// The warning beat and the burst cooldown are both on the sim clock
// (Sim.GameTimeMs) and shared by both triggers, so a creature standing in the
// spines or a flurry of hits can't chain-fire it every frame, and both slow
// uniformly under slow-mo.
[GlobalClass]
public partial class Cactus : Node3D, ITriggerable, IWorldEntity
{
    [Export] public CactusData data;
    // Struck detection. Wired to the scene's HurtBox child; its OnHit fires a
    // burst. PredictHit reports Object so attacks resolve the environment impact
    // cue, matching BerryTree / Chest / Door.
    [Export] private HurtBox _hurtBox;

    private Sim _sim;
    // Sim-clock deadline before the next burst is allowed. 0 = ready.
    private ulong _nextFireMs;
    // Sim-clock deadline the armed burst fires at. Only meaningful while _armed.
    private ulong _fireAtMs;
    private bool _armed;

    public override void _Ready()
    {
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnStruck;
            _hurtBox.PredictHit = _ => new HitPrediction(EHitResult.Object, EDamageTriggerFlags.None);
            // A cactus is struck by any weapon, but NOT by another cactus's
            // spines — without this a field of them would chain-detonate each
            // other (and a spine grazing its own firer would re-trigger it).
            _hurtBox.CanHit = hit => hit.source is not Cactus;
        }
        // An untouched cactus does no per-frame work — the trigger area and the
        // hurtbox wake it, and only the warning beat needs a tick.
        SetPhysicsProcess(false);
    }

    // Touch trigger: the scene's TriggerSource pings this when a Player or Mob
    // enters its area.
    public void Trigger(Node source)
    {
        Arm();
    }

    private void OnStruck(HitInfo hit)
    {
        Arm();
    }

    public void OnSpawned(Sim sim)
    {
        _sim = sim;
    }

    // Start the warning beat. Shared entry for both triggers, gated by the burst
    // cooldown; further triggers during the beat are swallowed.
    private void Arm()
    {
        if (_armed || data == null)
        {
            return;
        }
        Sim sim = _sim ?? Sim.Current;
        if (sim == null || sim.GameTimeMs < _nextFireMs)
        {
            return;
        }
        _armed = true;
        _fireAtMs = sim.GameTimeMs + (ulong)(Mathf.Max(0f, data.warningSeconds) * 1000f);
        SetPhysicsProcess(true);
        if (data.warningEffect != null)
        {
            // Vector3.Up * launchHeight: the Fx parents to the cactus, so this
            // is a LOCAL offset that puts the bristle where the spines will
            // erupt. Passing GlobalPosition would double the world coords.
            Fx.Create(data.warningEffect, this, Vector3.Up * data.launchHeight);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        Sim sim = _sim ?? Sim.Current;
        if (sim == null || sim.GameTimeMs < _fireAtMs)
        {
            return;
        }
        _armed = false;
        SetPhysicsProcess(false);
        Fire();
    }

    // Erupt one ring of spines and open the cooldown.
    private void Fire()
    {
        Sim sim = _sim ?? Sim.Current;
        if (sim == null)
        {
            return;
        }
        // Ahead of the payload guards so a half-authored cactus still cools
        // down instead of re-arming (and re-bristling) on every touch.
        _nextFireMs = sim.GameTimeMs + (ulong)(Mathf.Max(0f, data.cooldownSeconds) * 1000f);
        if (data.projectileScene == null || data.damageData == null)
        {
            return;
        }

        // Projectiles parent to the world (the cactus's parent, the Sim), so they
        // outlive the cactus's chunk streaming out mid-flight — matching how
        // DoProjectile parents shots to the actor's parent.
        Node parent = GetParent();
        if (parent == null)
        {
            return;
        }

        Vector3 origin = GlobalPosition + Vector3.Up * data.launchHeight;
        Rid? excludeHurtBox = _hurtBox != null ? (Rid?)_hurtBox.GetRid() : null;

        int count = Mathf.Max(1, data.spineCount);
        float elevation = Mathf.DegToRad(data.spineElevationDegrees);
        float cosE = Mathf.Cos(elevation);
        float sinE = Mathf.Sin(elevation);

        for (int i = 0; i < count; i++)
        {
            float yaw = Mathf.Tau * i / count;
            var direction = new Vector3(Mathf.Cos(yaw) * cosE, sinE, Mathf.Sin(yaw) * cosE);
            Projectile.Launch(
                parent,
                data.projectileScene,
                data.spineLifetimeSeconds,
                origin,
                direction * data.spineSpeed,
                data.damageData,
                this,
                (uint)ECollisionLayer.HurtBox,
                excludeHurtBox,
                null,
                default,
                // A wild hazard hits everyone — the cactus takes no side, and
                // friendlyFire lets a spine wound both the player and any mob.
                attackerTeam: ETeam.Neutral,
                friendlyFire: true);
        }

        if (data.burstEffect != null)
        {
            Fx.Create(data.burstEffect, this, Vector3.Up * data.launchHeight);
        }
    }

    public static Cactus Create(Sim sim, CactusSimState data)
    {
        var instance = data.Scene.Instantiate<Cactus>();
        data.SeatTransform(instance);
        instance._sim = sim;
        sim.AddChild(instance);
        return instance;
    }
}
