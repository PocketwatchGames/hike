using System.Collections.Generic;
using Godot;

// Area3D hazard that damages every HurtBox overlapping it via two
// independent damage paths:
//
// - `damageContinuous` applies every physics frame, scaled by delta. The
//   per-frame chunks route through HurtBox.Hit with HitInfo.dot = true so
//   DotHudAccumulator rolls them into one floating number per second
//   regardless of frame rate. Modifiers / status effects / hitstun /
//   knockback don't apply — for those, use an interval entry below.
//
// - `damageIntervals` is a list of (DamageData, tickInterval, tickOnEnter)
//   entries, each with its own timer. Each tick is a discrete HurtBox.Hit
//   carrying the full DamageData payload (modifiers, status stacks,
//   hitstun, knockback). Used for "fire a poison stack once a second"
//   semantics alongside a smooth continuous burn.
//
// Spawned by weapon AoEs (rain of arrows), GasClouds, and static traps
// (fire columns). Routes through HurtBox.Hit so armor / knockback / hit
// prediction match weapon hits.
[GlobalClass]
public partial class DamageZone : Area3D
{
    // Continuous, per-frame portion of the zone's damage. Optional —
    // hazards that author no smooth bleed (a pure ticking poison cloud)
    // leave it null and only run the interval entries.
    [Export] public ContinuousDamageData damageContinuous;

    // Discrete per-tick portion(s) of the zone's damage. Each entry runs
    // an independent timer; an empty / null list disables the interval
    // pass entirely.
    [Export] public Godot.Collections.Array<IntervalDamageEntry> damageIntervals;

    // Wired in the .tscn. Exposed so weapon-driven AoE spawns can resize the
    // hazard volume per-firing (see GasCloud.Initialize). Only the shape
    // resource is mutated, and only after a Duplicate(), so siblings of the
    // same .tscn template aren't disturbed.
    [Export] private CollisionShape3D _shape;

    // When true, only HurtBoxes whose owner is a Mob take damage — the
    // player (and any other non-Mob hurtboxes) are filtered out at enter
    // time. Used by player-spawned AoEs (rain of arrows) that should never
    // friendly-fire. Default false matches the existing fire / poison /
    // campfire zones that damage everything that walks into them.
    [Export] public bool enemiesOnly = false;

    private readonly List<HurtBox> _hurtBoxes = new();
    private float[] _intervalTimers;
    private HitInfo[] _intervalHits;
    private bool _active = true;
    private bool _intervalsBuilt = false;

    public override void _Ready()
    {
        CollisionMask |= (uint)ECollisionLayer.HurtBox;
        AreaEntered += OnAreaEntered;
        AreaExited += OnAreaExited;
        BuildIntervalState();
    }

    // Pre-_Ready override hook. Spawning code (GasCloud.Initialize) calls
    // this on a freshly instantiated DamageZone before AddChild so the
    // interval HitInfos built in _Ready reflect the weapon-side override.
    // Null arguments leave the .tscn-authored fields untouched, so a partial
    // override only changes the fields the weapon actually authored.
    public void OverrideAuthoring(
        ContinuousDamageData newContinuous,
        Godot.Collections.Array<IntervalDamageEntry> newIntervals,
        float radius)
    {
        if (newContinuous != null)
        {
            damageContinuous = newContinuous;
        }
        if (newIntervals != null)
        {
            damageIntervals = newIntervals;
        }
        if (radius > 0f && _shape?.Shape is SphereShape3D sphere)
        {
            // Duplicate before resizing — Godot shares Shape3D resources
            // across instances of the same .tscn, so an in-place radius
            // change would bleed into every other live AoE that used the
            // same scene.
            SphereShape3D copy = (SphereShape3D)sphere.Duplicate();
            copy.Radius = radius;
            _shape.Shape = copy;
        }
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
        // Lazily (re)build interval timers + HitInfos in case
        // OverrideAuthoring landed after _Ready or the data was reassigned
        // post-spawn. Cheap when nothing changed.
        if (!_intervalsBuilt)
        {
            BuildIntervalState();
        }

        float dt = (float)delta;

        // Continuous pass — one Hit per body per physics frame, with
        // healthDamage pre-scaled by delta.
        if (damageContinuous != null && damageContinuous.healthDamage > 0f)
        {
            for (int i = _hurtBoxes.Count - 1; i >= 0; i--)
            {
                HurtBox hb = _hurtBoxes[i];
                if (!IsInstanceValid(hb))
                {
                    _hurtBoxes.RemoveAt(i);
                    continue;
                }
                HitInfo hit = new HitInfo(damageContinuous, this, dt);
                hb.Hit(hit);
            }
        }

        // Interval pass — each entry runs its own timer + pre-built HitInfo.
        if (_intervalTimers != null)
        {
            for (int idx = 0; idx < _intervalTimers.Length; idx++)
            {
                _intervalTimers[idx] -= dt;
                if (_intervalTimers[idx] > 0f)
                {
                    continue;
                }
                IntervalDamageEntry entry = damageIntervals[idx];
                _intervalTimers[idx] = entry.tickInterval;
                HitInfo hit = _intervalHits[idx];
                for (int i = _hurtBoxes.Count - 1; i >= 0; i--)
                {
                    HurtBox hb = _hurtBoxes[i];
                    if (!IsInstanceValid(hb))
                    {
                        _hurtBoxes.RemoveAt(i);
                        continue;
                    }
                    hb.Hit(hit);
                }
            }
        }
    }

    private void BuildIntervalState()
    {
        if (damageIntervals == null || damageIntervals.Count == 0)
        {
            _intervalTimers = null;
            _intervalHits = null;
            _intervalsBuilt = true;
            return;
        }
        int n = damageIntervals.Count;
        _intervalTimers = new float[n];
        _intervalHits = new HitInfo[n];
        for (int i = 0; i < n; i++)
        {
            IntervalDamageEntry entry = damageIntervals[i];
            _intervalHits[i] = new HitInfo(entry?.damage, this);
            // tickOnEnter applies the first hit at entry time and resets the
            // timer there. Without it, wait the full interval before the
            // first tick.
            _intervalTimers[i] = (entry != null && entry.tickInterval > 0f)
                ? entry.tickInterval
                : 1f;
        }
        _intervalsBuilt = true;
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
        if (!_active)
        {
            return;
        }
        if (!_intervalsBuilt)
        {
            BuildIntervalState();
        }
        // Per-entry tickOnEnter pulse. The continuous pass picks up the new
        // body on the next physics frame so no special handling needed there.
        if (damageIntervals != null && _intervalHits != null)
        {
            for (int i = 0; i < damageIntervals.Count; i++)
            {
                IntervalDamageEntry entry = damageIntervals[i];
                if (entry == null || !entry.tickOnEnter || entry.damage == null)
                {
                    continue;
                }
                hb.Hit(_intervalHits[i]);
                _intervalTimers[i] = entry.tickInterval;
            }
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
