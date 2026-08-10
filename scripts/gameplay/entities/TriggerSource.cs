using System.Collections.Generic;
using Godot;

// Body-driven trigger. Any Player or Mob entering the Area3D pings every
// ITriggerable in _targets, subject to oneShot / cooldown / SetEnabled
// gating. _bodiesInArea is maintained for targets that need to know who
// to damage (SpikeDeployer reads source.BodiesInArea on Spiked-enter);
// targets that don't care (a PoisonCloudDeployer that just spawns at its
// own position) ignore it.
//
// Also implements ITriggerable itself, so a TriggerSource can be chained from
// another firer (chest open → spike trap's TriggerSource → pad runs its own
// body-detection dispatch).
//
// Cross-scene targeting is not supported. _targets is a same-scene NodePath
// array; both source and targets must load with the same chunk.
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

    // True = pressure-plate semantics: only bodies heavy enough to press a
    // plate (MobData.triggersTraps) are seen at all. False = contact
    // semantics: any Player or Mob touching the area fires it, weight
    // irrelevant — a cactus spines whatever brushes it, and a light critter is
    // exactly the thing that would blunder into one.
    [Export] public bool requiresWeight = true;

    public IReadOnlyList<Node3D> BodiesInArea => _bodiesInArea;
    public bool Enabled => _enabled;

    private readonly List<Node3D> _bodiesInArea = new();
    private bool _spent;
    private bool _enabled = true;
    private float _cooldownTimer;

    public override void _Ready()
    {
        // Player/Mob only — environment is excluded so a stray voxel collider
        // doesn't arm the trap.
        CollisionMask |= (uint)(ECollisionLayer.Player | ECollisionLayer.Mob);
        BodyEntered += OnBodyEntered;
        BodyExited += OnBodyExited;
        // Only tick while a cooldown is counting down — the rest of the time
        // the area's body signals do all the work, so an idle source costs
        // nothing per frame. Re-enabled in TryFire when a cooldown starts.
        SetPhysicsProcess(false);
    }

    public override void _PhysicsProcess(double delta)
    {
        _cooldownTimer -= (float)delta;
        if (_cooldownTimer <= 0f)
        {
            _cooldownTimer = 0f;
            SetPhysicsProcess(false);
            // Cooldown finished. A body that stayed on the pad the whole time
            // never raised a fresh BodyEntered, so re-fire here to retrigger
            // for anyone still standing in the area.
            if (_bodiesInArea.Count > 0)
            {
                TryFire();
            }
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
        // Light/small mobs (MobData.triggersTraps == false) are invisible to
        // weight-driven traps — they neither arm the trap nor end up in the
        // body list a sprung trap damages. Contact sources (requiresWeight
        // false) skip the check and take everyone.
        if (requiresWeight && body is Mob mob && mob.mobData != null && !mob.mobData.triggersTraps)
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

    // Fired by an external source. Runs the same dispatch path as a body
    // entry — targets see this TriggerSource as their source, so BodiesInArea
    // reads still resolve to who's currently standing on the pad.
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
        else if (cooldown > 0f)
        {
            _cooldownTimer = cooldown;
            SetPhysicsProcess(true);
        }
    }
}
