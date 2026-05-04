using System.Collections.Generic;
using Godot;

// Area3D hazard that periodically applies DamageData to every HurtBox
// overlapping it. Used for poison clouds, campfire flames, lava pools —
// anything that should damage actors who walk into it. Routes through
// HurtBox.Hit so armor / knockback / hit prediction match weapon hits.
[GlobalClass]
public partial class DamageZone : Area3D
{
    [Export] public DamageData damage;

    // Seconds between ticks while a body is inside. 0 = every physics frame.
    [Export] public float tickInterval = 1f;

    // True = first tick fires the moment a HurtBox enters. False = wait
    // tickInterval before the first hit (poison clouds typically prefer
    // immediate; a slow-burn campfire might not).
    [Export] public bool tickOnEnter = true;

    private readonly List<HurtBox> _hurtBoxes = new();
    private float _tickTimer;
    private HitInfo _hit;

    public override void _Ready()
    {
        CollisionMask |= (uint)ECollisionLayer.HurtBox;
        AreaEntered += OnAreaEntered;
        AreaExited += OnAreaExited;
        _hit = new HitInfo(damage, this);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_hurtBoxes.Count == 0)
        {
            return;
        }
        _tickTimer -= (float)delta;
        if (_tickTimer > 0f)
        {
            return;
        }
        _tickTimer = tickInterval;
        for (int i = _hurtBoxes.Count - 1; i >= 0; i--)
        {
            HurtBox hb = _hurtBoxes[i];
            if (!IsInstanceValid(hb))
            {
                _hurtBoxes.RemoveAt(i);
                continue;
            }
            hb.Hit(_hit);
        }
    }

    private void OnAreaEntered(Area3D area)
    {
        if (area is not HurtBox hb)
        {
            return;
        }
        if (_hurtBoxes.Contains(hb))
        {
            return;
        }
        _hurtBoxes.Add(hb);
        if (tickOnEnter)
        {
            hb.Hit(_hit);
            _tickTimer = tickInterval;
        }
    }

    private void OnAreaExited(Area3D area)
    {
        if (area is HurtBox hb)
        {
            _hurtBoxes.Remove(hb);
        }
    }
}
