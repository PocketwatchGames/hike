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
// Trigger(Node) from a TriggerSource (the typical pressure-plate setup) or
// any other firer (a chest's onOpen, a mob death, etc.) and runs the
// authored timeline: warning audio → spikes pop → activeDuration → retract
// → cooldown.
//
// Damage is one-shot per cycle: every body in the source's
// TriggerSource.BodiesInArea at Spiked-enter takes one hit. If the source
// is not a TriggerSource (e.g. fired by a chest's Complete), no body list
// is available and the spikes pop without damaging anyone — the deployer
// becomes a pure cosmetic effect in that case. If you need damage from a
// non-body source, pair it with a separate TriggerSource sized over the
// danger zone and chain them (chest → TriggerSource → SpikeDeployer).
[GlobalClass]
public partial class SpikeDeployer : Node3D, ITriggerable, IDisarmable
{
    [Export] public SpikeTrapData data;
    [Export] private Sprite3D _spikesSprite;
    [Export] private Discoverable _discoverable;

    public ESpikeDeployerState State => _state;

    private ESpikeDeployerState _state = ESpikeDeployerState.Idle;
    private float _stateTimer;
    private Node _activeSource;

    public override void _Ready()
    {
        UpdateVisuals();
    }

    public void Trigger(Node source)
    {
        if (_state != ESpikeDeployerState.Idle)
        {
            return;
        }
        _activeSource = source;
        EnterWarning();
    }

    public void Disarm()
    {
        _state = ESpikeDeployerState.Idle;
        _stateTimer = 0f;
        _activeSource = null;
        UpdateVisuals();
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
        if (data?.warningEffect != null)
        {
            Fx.Create(data.warningEffect, this, GlobalPosition);
        }
    }

    private void EnterSpiked()
    {
        _state = ESpikeDeployerState.Spiked;
        _stateTimer = data?.activeDuration ?? 0f;
        if (data?.emergeEffect != null)
        {
            Fx.Create(data.emergeEffect, this, GlobalPosition);
        }
        _discoverable?.ForceDiscover();
        UpdateVisuals();
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
        if (data?.retractEffect != null)
        {
            Fx.Create(data.retractEffect, this, GlobalPosition);
        }
        UpdateVisuals();
    }

    private void EnterIdle()
    {
        _state = ESpikeDeployerState.Idle;
        _stateTimer = 0f;
        _activeSource = null;
        UpdateVisuals();
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
            HitInfo hit = new HitInfo(data.damageData, this);
            hurtBox.Hit(hit);
        }
    }

    private void UpdateVisuals()
    {
        if (_spikesSprite != null)
        {
            _spikesSprite.Visible = _state == ESpikeDeployerState.Spiked;
        }
    }
}
