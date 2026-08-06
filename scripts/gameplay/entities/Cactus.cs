using Godot;

// A stationary desert hazard plant. Touching it (Player or Mob, via the wired
// TriggerSource) or striking it (its HurtBox) erupts a ring of spines outward
// in every direction, each carrying hazard damage. Purely reactive — no
// perception, no disarm, no press-to-interact — so it composes from a
// TriggerSource (touch) + a HurtBox (struck) both routed into Fire().
//
// The burst cooldown is on the sim clock (World.GameTimeMs) and shared by both
// triggers, so a creature standing in the spines or a flurry of hits can't
// chain-fire it every frame, and it slows uniformly under slow-mo.
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
    }

    // Touch trigger: the scene's TriggerSource pings this when a Player or Mob
    // enters its area.
    public void Trigger(Node source)
    {
        Fire();
    }

    private void OnStruck(HitInfo hit)
    {
        Fire();
    }

    public void OnSpawned(Sim sim)
    {
        _sim = sim;
    }

    // Erupt one ring of spines, subject to the shared sim-clock cooldown.
    private void Fire()
    {
        if (data?.projectileScene == null || data.damageData == null)
        {
            return;
        }
        Sim sim = _sim ?? Sim.Current;
        if (sim == null)
        {
            return;
        }
        if (sim.GameTimeMs < _nextFireMs)
        {
            return;
        }
        _nextFireMs = sim.GameTimeMs + (ulong)(Mathf.Max(0f, data.cooldownSeconds) * 1000f);

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
            Fx.Create(data.burstEffect, this, Vector3.Zero);
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
