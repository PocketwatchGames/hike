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

    // When true, only HurtBoxes whose owner is a Mob take damage — the
    // player (and any other non-Mob hurtboxes) are filtered out at enter
    // time. Used by player-spawned AoEs (rain of arrows) that should never
    // friendly-fire. Default false matches the existing fire / poison /
    // campfire zones that damage everything that walks into them.
    [Export] public bool enemiesOnly = false;

    private readonly List<HurtBox> _hurtBoxes = new();
    private float _tickTimer;
    private HitInfo _hit;
    private bool _active = true;

    public override void _Ready()
    {
        CollisionMask |= (uint)ECollisionLayer.HurtBox;
        AreaEntered += OnAreaEntered;
        AreaExited += OnAreaExited;
        _hit = new HitInfo(damage, this);
    }

    // Toggle whether the zone deals damage. Disabled zones still track
    // entries/exits so re-enabling resumes ticking on whoever's currently
    // inside, but skip the actual Hit calls. Used by Torch/campfire to
    // turn the burn off while the fire is doused.
    public void SetActive(bool active)
    {
        _active = active;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_active || _hurtBoxes.Count == 0)
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
        if (enemiesOnly && ItemEventHandlers.FindOwningMob(hb) == null)
        {
            return;
        }
        if (_hurtBoxes.Contains(hb))
        {
            return;
        }
        _hurtBoxes.Add(hb);
        if (_active && tickOnEnter)
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
