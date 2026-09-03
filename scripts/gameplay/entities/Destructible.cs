using System;
using Godot;

// Reusable "struck and it's gone" component. Drop it into any prop or
// interactive scene next to a HurtBox and hits become a permanent removal:
// spawn an effect, eject authored loot, then drop the entity from the world AND
// from its persistent sim state, so a chunk reload doesn't bring it back.
// Movement collision and path-blocker cells release with the node, so whatever
// the prop was blocking opens up for pathing the moment it dies.
//
// Composition, not inheritance: the owning root keeps whatever script it
// already has (PropInstance for tall grass, BerryTree, a future pot), and
// anything it wants to do at the moment of death — eject a per-instance
// payload, swap in a broken model — hangs off Destroyed. That split is why the
// authored `drops` list and the event both exist: a pot's contents are a
// property of the pot's scene, a bush's berry count is per-instance sim state.
//
// The node's own transform is the authored anchor for the effect and for
// ejected loot, so a tall prop can throw its debris from the middle rather than
// its feet.
[GlobalClass]
public partial class Destructible : Node3D
{
    // Where hits arrive. Wire the scene's HurtBox child; this component takes
    // over its OnHit / PredictHit, so the owning root must not also claim them.
    [Export] private HurtBox _hurtBox;

    // Damage TYPES that break this. A hit destroys only if its tags overlap —
    // so grass and bushes are `Physical | Fire` (a blade fells them, a torch
    // burns them away) while a web glob or a lightning bolt lands and leaves
    // them standing. `None` means any hit at all does it.
    //
    // An allow-list rather than an immunity list because the object's own
    // vulnerability is the authored fact: a clay pot takes Physical, an ice
    // sculpture might take Fire alone. Stated as types, not as Melee/Ranged —
    // delivery says nothing about what a hit is made of.
    [Export, CompactFlags] private EStat _destroyedBy = EStat.Physical;

    // Hits needed to destroy. 1 (the default) means the first strike does it —
    // grass, pots and barrels have no health pool. Only qualifying hits count.
    [Export(PropertyHint.Range, "1,20,1")] private int _hitsToDestroy = 1;

    // Spawned into the world at this node's position. Parented to the Sim
    // rather than to us, since we are freed in the same breath.
    [Export] private PackedScene _destroyEffect;

    // Loot ejected on destruction. Authored per scene, so it is what this KIND
    // of thing drops; a per-instance payload rides on Destroyed instead.
    [Export] private ItemCountRange[] _drops;

    // Launch speed of ejected drops, thrown at 45° along a random heading.
    [Export(PropertyHint.Range, "0,20,0.5")] private float _dropSpeed = 6f;

    // Fires immediately before the entity leaves the world, while the scene is
    // still intact and its transforms are still valid.
    public event Action Destroyed;

    private int _hitsTaken;
    private bool _destroyed;

    public override void _Ready()
    {
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnHurtBoxHit;
            _hurtBox.PredictHit = _ => new HitPrediction(EHitResult.Object, EDamageTriggerFlags.None);
        }
    }

    private void OnHurtBoxHit(HitInfo hit)
    {
        if (_destroyed || !Breaks(hit))
        {
            return;
        }
        _hitsTaken++;
        if (_hitsTaken >= Mathf.Max(1, _hitsToDestroy))
        {
            Destroy();
        }
    }

    // Whether this hit is of a kind that breaks us. Deliberately checked HERE
    // and not in HurtBox.CanHit: a refused CanHit means the hurtbox isn't there
    // for that attack at all (Projectile excludes it and the shot flies on), and
    // a lightning bolt should still strike the bush — it just shouldn't fell it.
    private bool Breaks(in HitInfo hit)
    {
        return _destroyedBy == EStat.None || (hit.tags & _destroyedBy) != 0;
    }

    // Destroy now, whatever the hit count. Also the entry point for scripted
    // destruction (a quest clearing a path, an explosion levelling a shelf).
    // Safe to call more than once.
    public void Destroy()
    {
        if (_destroyed)
        {
            return;
        }
        _destroyed = true;

        Sim sim = Sim.Current;
        Vector3 origin = GlobalPosition;

        if (_destroyEffect != null && sim != null)
        {
            // ToLocal because Fx.Create takes a position in the parent's space,
            // and nothing guarantees the Sim node sits at the world origin.
            Fx.Create(_destroyEffect, sim, sim.ToLocal(origin));
        }

        EjectDrops(sim, origin);

        Destroyed?.Invoke();

        if (sim != null)
        {
            sim.DestroyEntity(this);
        }
    }

    private void EjectDrops(Sim sim, Vector3 origin)
    {
        if (sim == null || _drops == null)
        {
            return;
        }
        var rng = new Random();
        for (int i = 0; i < _drops.Length; i++)
        {
            ItemCountRange drop = _drops[i];
            if (drop?.item == null)
            {
                continue;
            }
            int count = drop.Resolve(rng).count;
            for (int n = 0; n < count; n++)
            {
                sim.SpawnLoot(origin, RandomEjectImpulse(rng, _dropSpeed), drop.item);
            }
        }
    }

    // One drop's launch impulse: full speed at 45° up, along a random heading.
    // Static and public so an owner ejecting its own per-instance payload from
    // Destroyed scatters it the same way at its own authored speed.
    public static Vector3 RandomEjectImpulse(Random rng, float speed)
    {
        float angle = (float)(rng.NextDouble() * Mathf.Pi * 2f);
        float horizontal = speed * Mathf.Cos(Mathf.Pi / 4f);
        float vertical = speed * Mathf.Sin(Mathf.Pi / 4f);
        return new Vector3(horizontal * Mathf.Cos(angle), vertical, horizontal * Mathf.Sin(angle));
    }
}
