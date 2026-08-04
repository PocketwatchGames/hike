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

    // Faction that "owns" this hazard, fed to the shared
    // ItemEventHandlers.CanDamage rule at enter time. Only consulted when
    // friendlyFire is false — a player-spawned AoE (rain of arrows, bomb) gets
    // the firing actor's team via GasCloud so it spares the player and allies
    // while still hitting enemies. Environmental hazards leave friendlyFire
    // true and ignore this.
    [Export] public ETeam attackerTeam = ETeam.Neutral;

    // When true (default), the zone ignores team allegiance and damages every
    // HurtBox that enters — what every environmental hazard wants (fire trap,
    // campfire, poison cloud). Actor-spawned weapon AoEs set this false (via
    // GasCloud) so the CanDamage team rule applies and they don't friendly-fire
    // the player or allies.
    [Export] public bool friendlyFire = true;

    // When true, a hurtbox is only damaged if an unobstructed line exists from
    // the zone center to the target — solid terrain/props block the hit, so a
    // tall blast can't reach enemies through a wall or an intervening floor.
    // Opt-in: environmental clouds meant to seep around corners leave it false.
    // Evaluated per-hit (not just on enter) so it re-checks as targets move.
    [Export] public bool requireLineOfSight = false;

    // Height above the zone origin the LOS ray starts from, so a blast resting
    // on the ground doesn't begin inside the floor voxel and self-block every
    // target. Only consulted when requireLineOfSight is true.
    [Export(PropertyHint.Range, "0,3,0.1,or_greater")] public float losOriginHeight = 0.5f;

    private readonly List<HurtBox> _hurtBoxes = new();
    private float[] _intervalTimers;
    private HitInfo[] _intervalHits;
    private bool _active = true;
    private bool _intervalsBuilt = false;

    public override void _Ready()
    {
        // Debris rides along so blasts scatter loose loot. A zone hits
        // everything overlapping it, so this can't cost an actor its damage.
        CollisionMask |= (uint)(ECollisionLayer.HurtBox | ECollisionLayer.Debris);
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
        if (radius > 0f && _shape != null)
        {
            // Duplicate before resizing — Godot shares Shape3D resources
            // across instances of the same .tscn, so an in-place radius
            // change would bleed into every other live AoE that used the
            // same scene. Only the radius is overridden; a cylinder keeps its
            // authored height (the vertical reach that lets a ground blast
            // catch airborne targets).
            if (_shape.Shape is SphereShape3D sphere)
            {
                SphereShape3D copy = (SphereShape3D)sphere.Duplicate();
                copy.Radius = radius;
                _shape.Shape = copy;
            }
            else if (_shape.Shape is CylinderShape3D cylinder)
            {
                CylinderShape3D copy = (CylinderShape3D)cylinder.Duplicate();
                copy.Radius = radius;
                _shape.Shape = copy;
            }
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
        // healthDamage pre-scaled by delta. Fires when EITHER healthDamage
        // or a non-empty buildups list is authored — a pure-buildup cloud
        // (poison gas: no per-frame chip, just meter accrual toward the
        // status apply) still needs the per-frame Hit to land its
        // buildupAmountMultiplier-scaled contributions.
        if (damageContinuous != null
            && (damageContinuous.healthDamage > 0f
                || (damageContinuous.buildups != null && damageContinuous.buildups.Count > 0)))
        {
            for (int i = _hurtBoxes.Count - 1; i >= 0; i--)
            {
                HurtBox hb = _hurtBoxes[i];
                if (!IsInstanceValid(hb))
                {
                    _hurtBoxes.RemoveAt(i);
                    continue;
                }
                HitInfo hit = new HitInfo(damageContinuous, this, dt, attackerTeam: attackerTeam);
                hit.friendlyFire = friendlyFire;
                TryHit(hb, hit);
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
                    TryHit(hb, hit);
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
            _intervalHits[i] = new HitInfo(entry?.damage, this, attackerTeam: attackerTeam);
            // Override the per-DamageData friendlyFire with the zone-level
            // policy so the receiver's CanHit filter judges each tick against
            // the hazard's own ally rule.
            _intervalHits[i].friendlyFire = friendlyFire;
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
        // No team filtering here — every overlapping hurtbox is tracked and
        // the per-tick TryHit consults the receiver's CanHit against the real
        // HitInfo, so allies are spared at apply time.
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
                TryHit(hb, _intervalHits[i]);
                _intervalTimers[i] = entry.tickInterval;
            }
        }
    }

    // Apply a hit only if the receiver's HurtBox.CanHit filter accepts it
    // (team allegiance etc.) and — when requireLineOfSight is set — nothing
    // solid occludes the target. The hazard's gate lives here, per tick,
    // against the actual HitInfo — there is no enter-time team filter.
    private void TryHit(HurtBox hb, in HitInfo hit)
    {
        if (!hb.CanBeHit(hit))
        {
            return;
        }
        if (requireLineOfSight && !HasLineOfSight(hb))
        {
            return;
        }
        // A zone damages whatever stands in it from no particular side, so its
        // HitInfo carries no direction — fine for actors, useless for loose
        // debris, which has nowhere to be thrown. Give debris a radial push out
        // of the zone so a bomb actually scatters the items it goes off next to.
        if ((hb.CollisionLayer & (uint)ECollisionLayer.Debris) != 0
            && hit.hitDirection.LengthSquared() < 0.0001f)
        {
            Vector3 away = hb.GlobalPosition - GlobalPosition;
            away.Y = 0f;
            if (away.LengthSquared() > 0.0001f)
            {
                HitInfo debrisHit = hit;
                debrisHit.hitDirection = away.Normalized();
                hb.Hit(debrisHit);
                return;
            }
        }
        hb.Hit(hit);
    }

    // Raycast from the zone center to the target's hurtbox; blocked by solid
    // terrain/props so a blast can't reach through walls or floors. Mirrors the
    // perception LOS query (ECollisionLayer.Solid, bodies only). Areas are
    // ignored so the HurtBox areas themselves don't register as occluders.
    private bool HasLineOfSight(HurtBox hb)
    {
        World3D world = GetWorld3D();
        if (world == null)
        {
            return true;
        }
        Vector3 from = GlobalPosition + Vector3.Up * losOriginHeight;
        Vector3 to = hb.GlobalPosition;
        using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Solid);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        return world.DirectSpaceState.IntersectRay(query).Count == 0;
    }

    private void OnAreaExited(Area3D area)
    {
        if (area is HurtBox hb)
        {
            _hurtBoxes.Remove(hb);
        }
    }
}
