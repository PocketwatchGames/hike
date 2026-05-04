using System.Collections.Generic;
using Godot;

// Body-driven trigger. Any Player or Mob entering the Area3D pings every
// ITriggerable in _targets, subject to oneShot / cooldown / SetEnabled
// gating. _bodiesInArea is maintained for targets that need to know who
// to damage (SpikeDeployer reads source.BodiesInArea on Spiked-enter);
// targets that don't care (a PoisonCloudDeployer that just spawns at its
// own position) ignore it.
//
// Also implements ITriggerable itself, so a TriggerSource can be chained
// from another firer (chest opens → chest's onOpenTargets points at a
// nearby spike trap's TriggerSource → pad runs its own body-detection
// dispatch). This is what makes "open the chest, the spike trap in
// front of you triggers" wireable without any new code.
//
// Cross-scene targeting is not supported. _targets is a same-scene
// NodePath array; both source and targets must load with the same chunk.
[GlobalClass]
public partial class TriggerSource : Area3D, ITriggerable
{
    [Export] private Godot.Collections.Array<Node> _targets = new();

    // True = single-fire, source goes inert after firing once. Use for
    // tutorial cues or one-time room-spawn triggers.
    [Export] public bool oneShot = false;

    // Seconds after a fire before the source can re-fire. 0 = no cooldown
    // (re-fires the moment another body enters). Independent of any
    // target's internal cycle — a SpikeDeployer with a 5s reset still
    // works fine if the source has 0 cooldown, because the deployer
    // ignores Trigger() calls while it's mid-cycle.
    [Export] public float cooldown = 0.5f;

    public IReadOnlyList<Node3D> BodiesInArea => _bodiesInArea;
    public bool Enabled => _enabled;

    private readonly List<Node3D> _bodiesInArea = new();
    private bool _spent;
    private bool _enabled = true;
    private float _cooldownTimer;

    public override void _Ready()
    {
        // Same body filter the SpikeTrap had — anything that walks across
        // an authored trap fires it. Environment is excluded so a stray
        // voxel collider doesn't arm the trap.
        CollisionMask |= (uint)(ECollisionLayer.Player | ECollisionLayer.Mob);
        BodyEntered += OnBodyEntered;
        BodyExited += OnBodyExited;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_cooldownTimer > 0f)
        {
            _cooldownTimer -= (float)delta;
        }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    private void OnBodyEntered(Node3D body)
    {
        if (body == this)
        {
            return;
        }
        if (!_bodiesInArea.Contains(body))
        {
            _bodiesInArea.Add(body);
        }
        TryFire();
    }

    private void OnBodyExited(Node3D body)
    {
        _bodiesInArea.Remove(body);
    }

    // ITriggerable: fired by an external source (chest open, mob death,
    // upstream TriggerSource). Just runs the same dispatch path as a body
    // entry — targets see this TriggerSource as their source so any
    // BodiesInArea reads still resolve to who's currently standing on
    // the pad.
    public void Trigger(Node source)
    {
        TryFire();
    }

    private void TryFire()
    {
        if (!_enabled || _spent || _cooldownTimer > 0f)
        {
            return;
        }
        for (int i = 0; i < _targets.Count; i++)
        {
            if (_targets[i] is ITriggerable t)
            {
                t.Trigger(this);
            }
        }
        if (oneShot)
        {
            _spent = true;
        }
        else
        {
            _cooldownTimer = cooldown;
        }
    }
}
