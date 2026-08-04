using System.Collections.Generic;
using Godot;

public enum ESpikeDeployerState
{
    Idle,
    Warning,
    Spiked,
    Cooldown,
}

// Spike-deploy behavior, decoupled from any particular trigger. Receives
// Trigger(Node) from a TriggerSource or any other firer and runs the authored
// timeline: warning → spikes pop → activeDuration → retract → cooldown.
//
// Damage is one-shot per cycle: every body in the source's
// TriggerSource.BodiesInArea at Spiked-enter takes one hit. A non-TriggerSource
// firer (e.g. a chest's Complete) has no body list, so the spikes pop
// cosmetically. For damage from a non-body source, chain a TriggerSource sized
// over the danger zone (chest → TriggerSource → SpikeDeployer).
[GlobalClass]
public partial class SpikeDeployer : Node3D, ITriggerable, IDisarmable
{
    // AnimationPlayer clip names (see spike_trap.tscn). Stable internal
    // identifiers, not tunables — the pose targets (rest -1.5, peek -1.25,
    // out 0) and timing live in the animations themselves.
    private const string AnimWarn = "warn";       // rest → peek
    private const string AnimDeploy = "deploy";    // peek → out
    private const string AnimRetract = "retract";  // out  → rest

    [Export] public SpikeTrapData data;
    [Export] private AnimationPlayer _animator;
    [Export] private Discoverable _discoverable;

    public ESpikeDeployerState State => _state;

    private ESpikeDeployerState _state = ESpikeDeployerState.Idle;
    private float _stateTimer;
    private Node _activeSource;

    public override void _Ready()
    {
        // Push the authored armed prominence onto the host Discoverable so the
        // trap placement (SpikeTrapData) owns it rather than the scene.
        if (_discoverable != null && data != null)
        {
            _discoverable.prominence = data.armedProminence;
        }
        // Rest fully retracted. The scene authors the Spikes node at the
        // retracted Y too; seeking the retract clip to its end guarantees the
        // pose if the entity respawns mid-world.
        SnapRetracted();
        // An armed, untriggered trap does no per-frame work — the TriggerSource
        // Area3D wakes it via Trigger(). Only tick while a cycle is in flight.
        SetPhysicsProcess(false);
    }

    public void Trigger(Node source)
    {
        if (_state != ESpikeDeployerState.Idle)
        {
            return;
        }
        _activeSource = source;
        SetPhysicsProcess(true);
        EnterWarning();
    }

    public void Disarm()
    {
        _state = ESpikeDeployerState.Idle;
        _stateTimer = 0f;
        _activeSource = null;
        // Snap (not tween) to rest — a disarmed armed trap is already
        // retracted, so playing the retract clip from its start would pop the
        // spikes up to 0 first.
        SnapRetracted();
        SetPhysicsProcess(false);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        switch (_state)
        {
            case ESpikeDeployerState.Warning:
                _stateTimer -= dt;
                if (_stateTimer <= 0f)
                {
                    EnterSpiked();
                }
                break;
            case ESpikeDeployerState.Spiked:
                _stateTimer -= dt;
                if (_stateTimer <= 0f)
                {
                    EnterCooldown();
                }
                break;
            case ESpikeDeployerState.Cooldown:
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
        _state = ESpikeDeployerState.Warning;
        _stateTimer = data?.warningDelay ?? 0f;
        _animator?.Play(AnimWarn);
        if (data?.warningEffect != null)
        {
            // Vector3.Zero: the Fx parents to this deployer, so the position arg
            // is a LOCAL offset. Passing GlobalPosition here would double the
            // world coords and fling the sound/particles far out of earshot.
            Fx.Create(data.warningEffect, this, Vector3.Zero);
        }
    }

    private void EnterSpiked()
    {
        _state = ESpikeDeployerState.Spiked;
        _stateTimer = data?.activeDuration ?? 0f;
        _animator?.Play(AnimDeploy);
        if (data?.emergeEffect != null)
        {
            Fx.Create(data.emergeEffect, this, Vector3.Zero);
        }
        // A sprung trap is conspicuous — bump prominence so the now-exposed
        // trap stays easy to notice, then force the immediate discovery.
        if (_discoverable != null && data != null)
        {
            _discoverable.prominence = data.firedProminence;
        }
        _discoverable?.ForceDiscover();
        // Body list only available for TriggerSource sources. For
        // non-body firers (chest open) the deployer pops cosmetically.
        if (_activeSource is TriggerSource ts)
        {
            IReadOnlyList<Node3D> bodies = ts.BodiesInArea;
            for (int i = 0; i < bodies.Count; i++)
            {
                HitBody(bodies[i]);
            }
        }
    }

    private void EnterCooldown()
    {
        _state = ESpikeDeployerState.Cooldown;
        _stateTimer = data?.resetTime ?? 0f;
        _animator?.Play(AnimRetract);
        if (data?.retractEffect != null)
        {
            Fx.Create(data.retractEffect, this, Vector3.Zero);
        }
    }

    private void EnterIdle()
    {
        _state = ESpikeDeployerState.Idle;
        _stateTimer = 0f;
        _activeSource = null;
        // Spikes already retracted by EnterCooldown — no pose change here.
        // Cycle complete: go dormant until the next Trigger().
        SetPhysicsProcess(false);
    }

    private void HitBody(Node3D body)
    {
        if (body == null || data?.damageData == null)
        {
            return;
        }
        HurtBox hurtBox = body.GetNodeOrNull<HurtBox>("HurtBox");
        if (hurtBox != null)
        {
            // Spike pops up under the target — knockback (if any) lifts
            // and bumps along the same axis. Y is stripped at apply time
            // for horizontal-only knockback, so Vector3.Up reads as a zero
            // horizontal impulse in practice; senders that want a horizontal
            // shove from a trap should author a different direction here.
            // No level scaling: the hit's damageData authors a hazard profile, so the
            // bite is already sized to whoever stepped on it.
            hurtBox.Hit(new HitInfo(data.damageData, this, Vector3.Up));
        }
    }

    // Jump straight to the retracted rest pose (-1.5) without tweening up.
    private void SnapRetracted()
    {
        if (_animator == null)
        {
            return;
        }
        _animator.Play(AnimRetract);
        _animator.Seek(_animator.CurrentAnimationLength, true);
    }
}
