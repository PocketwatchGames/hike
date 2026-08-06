using System.Collections.Generic;
using Godot;

public enum EDartDeployerState
{
    Idle,
    Warning,
    Cooldown,
}

// Dart-firing behavior, decoupled from any particular trigger. Receives
// Trigger(Node) from a TriggerSource (the tripwire) or any other firer and runs
// warning -> fire volley -> cooldown -> idle. Each muzzle fires one Projectile,
// aimed at the intruder's torso captured from the firing TriggerSource's
// BodiesInArea; with no body list (a non-TriggerSource firer, e.g. a chest
// open) the darts fall back to firing straight down each muzzle's forward.
//
// The trap owns the perception/interact policy through its host Trap; this
// node is purely the effect, so it implements ITriggerable + IDisarmable and
// nothing else.
[GlobalClass]
public partial class DartDeployer : Node3D, ITriggerable, IDisarmable
{
    [Export] public DartTrapData data;

    // Launch points: one dart per muzzle. Each fires along its own -Z (Godot
    // forward) when there's no aim target; author them on the launcher face at
    // torso height. Empty = a single dart from this deployer's own transform.
    [Export] private Godot.Collections.Array<Node3D> _muzzles = new();
    [Export] private Discoverable _discoverable;

    public EDartDeployerState State => _state;

    private EDartDeployerState _state = EDartDeployerState.Idle;
    private float _stateTimer;
    private Node _activeSource;

    public override void _Ready()
    {
        // Push the authored armed prominence onto the host Discoverable so the
        // trap placement (DartTrapData) owns it rather than the scene.
        if (_discoverable != null && data != null)
        {
            _discoverable.prominence = data.armedProminence;
        }
        // An armed, untriggered trap does no per-frame work — the TriggerSource
        // Area3D wakes it via Trigger(). Only tick while a cycle is in flight.
        SetPhysicsProcess(false);
    }

    public void Trigger(Node source)
    {
        if (_state != EDartDeployerState.Idle)
        {
            return;
        }
        _activeSource = source;
        SetPhysicsProcess(true);
        EnterWarning();
    }

    public void Disarm()
    {
        _state = EDartDeployerState.Idle;
        _stateTimer = 0f;
        _activeSource = null;
        SetPhysicsProcess(false);
        if (data?.disarmEffect != null)
        {
            Fx.Create(data.disarmEffect, this, Vector3.Zero);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        switch (_state)
        {
            case EDartDeployerState.Warning:
                _stateTimer -= dt;
                if (_stateTimer <= 0f)
                {
                    Fire();
                }
                break;
            case EDartDeployerState.Cooldown:
                _stateTimer -= dt;
                if (_stateTimer <= 0f)
                {
                    EnterIdle();
                }
                break;
        }
    }

    private void EnterWarning()
    {
        _state = EDartDeployerState.Warning;
        _stateTimer = data?.warningDelay ?? 0f;
        if (data?.warningEffect != null)
        {
            Fx.Create(data.warningEffect, this, Vector3.Zero);
        }
    }

    private void Fire()
    {
        _state = EDartDeployerState.Cooldown;
        _stateTimer = data?.resetTime ?? 0f;
        // A sprung trap is conspicuous — bump prominence and force the immediate
        // discovery so the now-obvious launcher stays easy to notice afterward.
        if (_discoverable != null && data != null)
        {
            _discoverable.prominence = data.firedProminence;
        }
        _discoverable?.ForceDiscover();

        if (data?.dartScene == null || data.damageData == null)
        {
            return;
        }
        // Projectiles live under the sim so they outlast a chunk-unload of the
        // trap itself; fall back to our parent if the sim isn't up.
        Node parent = (Node)Sim.Current ?? GetParent();
        if (parent == null)
        {
            return;
        }

        // Aim at the intruder captured at trigger time: torso of the nearest
        // body still in the tripwire's area. Null when a non-TriggerSource
        // firer poked us (no body list) — each dart then flies straight forward.
        Vector3? aimPoint = ResolveAimPoint();

        var impact = new ProjectileImpact
        {
            environment = data.impactEnvironmentEffect,
            health = data.impactHitEffect,
            armor = data.impactHitEffect,
            lethal = data.impactHitEffect,
        };

        if (_muzzles != null && _muzzles.Count > 0)
        {
            for (int i = 0; i < _muzzles.Count; i++)
            {
                LaunchFrom(_muzzles[i], parent, aimPoint, impact);
            }
        }
        else
        {
            LaunchFrom(this, parent, aimPoint, impact);
        }
    }

    private Vector3? ResolveAimPoint()
    {
        if (_activeSource is not TriggerSource ts)
        {
            return null;
        }
        IReadOnlyList<Node3D> bodies = ts.BodiesInArea;
        Node3D nearest = null;
        float nearestSq = float.MaxValue;
        Vector3 here = GlobalPosition;
        for (int i = 0; i < bodies.Count; i++)
        {
            Node3D body = bodies[i];
            if (body == null || !GodotObject.IsInstanceValid(body))
            {
                continue;
            }
            float distSq = here.DistanceSquaredTo(body.GlobalPosition);
            if (distSq < nearestSq)
            {
                nearestSq = distSq;
                nearest = body;
            }
        }
        if (nearest == null)
        {
            return null;
        }
        return nearest.GlobalPosition + Vector3.Up * data.aimHeight;
    }

    private void LaunchFrom(Node3D muzzle, Node parent, Vector3? aimPoint, ProjectileImpact impact)
    {
        if (muzzle == null)
        {
            return;
        }
        Transform3D xform = muzzle.GlobalTransform;
        Vector3 origin = xform.Origin;
        // Aim at the captured target, or fall back to the muzzle's own forward
        // (Godot forward is -Z of the basis).
        Vector3 dir = aimPoint.HasValue
            ? (aimPoint.Value - origin)
            : -xform.Basis.Z;
        if (dir.LengthSquared() < 1e-6f)
        {
            dir = -xform.Basis.Z;
        }
        dir = dir.Normalized();
        Vector3 velocity = dir * data.dartSpeed;
        Projectile.Launch(
            parent,
            data.dartScene,
            data.dartLifetimeSeconds,
            origin,
            velocity,
            data.damageData,
            this,
            (uint)ECollisionLayer.HurtBox,
            null,
            null,
            impact,
            attackerTeam: ETeam.Hostile,
            friendlyFire: data.damageData.friendlyFire);
        if (data.fireEffect != null)
        {
            Fx.Create(data.fireEffect, muzzle, Vector3.Zero);
        }
    }

    private void EnterIdle()
    {
        _state = EDartDeployerState.Idle;
        _stateTimer = 0f;
        _activeSource = null;
        // Cycle complete: go dormant until the next Trigger().
        SetPhysicsProcess(false);
    }
}
