using System;
using System.Collections.Generic;
using Godot;

// Reusable "struck and it breaks" component. Drop it into any prop or
// interactive scene next to a HurtBox: hits spawn an effect, eject loot, and
// leave the entity lying as RUBBLE until its schedule stands it up again
// (ERestoreSchedule). The broken flag lives on the entity's sim state
// (BreakState), so rubble survives a chunk reload and a save.
//
// The scene is authored as two branches. `_intact` is everything the standing
// object is — its model, its movement collider, the HurtBox — and `_rubble` is
// what is left. Only one survives a spawn: the other is freed before the Sim
// rasterizes path blockers, so rubble blocks nothing unless its own branch
// carries a Solid collider. Breaking re-spawns the entity rather than toggling
// it in place, so what you see the moment it breaks is exactly what a reload
// shows.
//
// Composition, not inheritance: the owning root keeps whatever script it
// already has (PropInstance, BerryTree, ExplodingBarrel) and implements
// IBreakableEntity; anything it does at the moment of breaking — a berry
// payload, a blast — hangs off Destroyed.
//
// The node's own transform is the authored anchor for the effect and for
// ejected loot, so a tall prop can throw its debris from the middle rather than
// its feet.
[GlobalClass]
public partial class Destructible : Node3D
{
    // Salt for the day's loot roll (WorldState.DailyRandom), mixed with the
    // entity's cell so neighbouring crates don't roll alike.
    private const int LOOT_SALT = 0x6272_6b6c;

    // Where hits arrive. Wire the scene's HurtBox child (under `_intact`); this
    // component takes over its OnHit / PredictHit, so the owning root must not
    // also claim them.
    [Export] private HurtBox _hurtBox;

    // Damage TYPES that break this. A hit breaks only if its tags overlap —
    // so grass and bushes are `Physical | Fire` (a blade fells them, a torch
    // burns them away) while a web glob or a lightning bolt lands and leaves
    // them standing. `None` means any hit at all does it.
    //
    // An allow-list rather than an immunity list because the object's own
    // vulnerability is the authored fact: a clay pot takes Physical, an ice
    // sculpture might take Fire alone. Stated as types, not as Melee/Ranged —
    // delivery says nothing about what a hit is made of.
    [Export, CompactFlags] private EHitTag _destroyedBy = EHitTag.Physical;

    // Hits needed to break. 1 (the default) means the first strike does it —
    // grass, pots and barrels have no health pool. Only qualifying hits count,
    // and the count is not saved: a half-struck rock is whole again on reload.
    [Export(PropertyHint.Range, "1,20,1")] private int _hitsToDestroy = 1;

    // Spawned into the world at this node's position. Parented to the Sim
    // rather than to us, since the entity re-spawns in the same breath.
    [Export] private PackedScene _destroyEffect;

    // Spawned at this node's position by a hit that counts toward breaking but
    // does not break it yet, so a multi-hit object answers every swing.
    [Export] private PackedScene _hitEffect;

    // What this KIND of thing always drops — a hive's honey, a termite mound's
    // mud. Each row rolls its own chance.
    [Export] private ItemCountRange[] _drops;

    // Also drop the zone's loot (ZoneData.zoneLoot) — a
    // crate, barrel or pot holds whatever such things hold in this zone.
    [Export] private bool _fillWithZoneLoot;

    // When it stands again, and after how many rests / sunrises.
    [Export] private ERestoreSchedule _restore = ERestoreSchedule.Rests;
    [Export(PropertyHint.Range, "1,30,1,or_greater")] private int _restoreAfter = 1;

    // The standing object (model, colliders, HurtBox). Required.
    [Export] private Node3D _intact;

    // What is left lying until it restores. Optional: with none, the broken
    // entity shows nothing — and with `Never` as well, it is removed outright.
    [Export] private Node3D _rubble;

    // Fires at the moment of breaking, while the intact scene is still standing
    // and its transforms are valid. Not on a spawn that is already broken.
    public event Action Destroyed;

    private IBreakableSimState _owner;
    private int _hitsTaken;
    private bool _destroyed;

    public bool IsBroken => _owner?.Break.Broken == true;

    public override void _Ready()
    {
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnHurtBoxHit;
            _hurtBox.PredictHit = _ => new HitPrediction(EHitResult.Object, EDamageTriggerFlags.None);
        }
        if (_rubble != null)
        {
            _rubble.Visible = false;
        }
    }

    // Called by the Sim as the entity registers, before its path blockers are
    // rasterized. Keeps the branch the state says and frees the other —
    // RemoveChild first, because the rasterizer walks the live tree this frame.
    public void Bind(IBreakableSimState owner)
    {
        _owner = owner;
        bool broken = owner?.Break.Broken == true;
        _destroyed = broken;
        Node3D gone = broken ? _intact : _rubble;
        if (gone != null)
        {
            gone.GetParent()?.RemoveChild(gone);
            gone.QueueFree();
        }
        if (broken && _rubble != null)
        {
            _rubble.Visible = true;
        }
        if (broken)
        {
            _intact = null;
        }
        else
        {
            _rubble = null;
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
            return;
        }
        PlayEffect(_hitEffect);
    }

    private void PlayEffect(PackedScene effect)
    {
        Sim sim = Sim.Current;
        if (effect != null && sim != null)
        {
            // ToLocal because Fx.Create takes a position in the parent's space,
            // and nothing guarantees the Sim node sits at the world origin.
            Fx.Create(effect, sim, sim.ToLocal(GlobalPosition));
        }
    }

    // Whether this hit is of a kind that breaks us. Deliberately checked HERE
    // and not in HurtBox.CanHit: a refused CanHit means the hurtbox isn't there
    // for that attack at all (Projectile excludes it and the shot flies on), and
    // a lightning bolt should still strike the bush — it just shouldn't fell it.
    private bool Breaks(in HitInfo hit)
    {
        return _destroyedBy == EHitTag.None || (hit.tags & _destroyedBy) != 0;
    }

    // Break now, whatever the hit count. Also the entry point for scripted
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

        PlayEffect(_destroyEffect);

        EjectDrops(sim, origin);

        Destroyed?.Invoke();

        if (sim == null)
        {
            return;
        }
        bool removeOutright = _owner == null || _intact == null
            || (_restore == ERestoreSchedule.Never && _rubble == null);
        if (_intact == null && _owner != null)
        {
            GD.PushError($"Destructible in '{Owner?.SceneFilePath}' has no _intact branch; removing it outright.");
        }
        if (removeOutright)
        {
            sim.DestroyEntity(this);
            return;
        }
        _owner.Break.MarkBroken(_restore, _restoreAfter, sim.WorldClockDays);
        // Deferred: a hit can land inside the physics flush (a hazard's
        // area-entered signal), where freeing and adding bodies is not allowed.
        Callable.From(() => sim.RespawnEntity(this)).CallDeferred();
    }

    private void EjectDrops(Sim sim, Vector3 origin)
    {
        if (sim == null)
        {
            return;
        }
        ItemCountRange[] zoneLoot = _fillWithZoneLoot ? sim.WorldState?.ZoneDataAt(origin)?.zoneLoot : null;
        if ((_drops == null || _drops.Length == 0) && (zoneLoot == null || zoneLoot.Length == 0))
        {
            return;
        }
        Random rng = LootRandom(sim, origin);
        var items = new List<ItemState>();
        ItemCountRange.ResolveAll(_drops, rng, items);
        ItemCountRange.ResolveAll(zoneLoot, rng, items);
        sim.EjectLoot(items, origin);
    }

    // The day's roll for this spot: re-breaking the same thing on the same day
    // (after a reload) yields the same loot.
    private Random LootRandom(Sim sim, Vector3 origin)
    {
        WorldState ws = sim.WorldState;
        if (ws == null)
        {
            return new Random();
        }
        Vector3 anchor = (_owner as EntitySimState)?.WorldPosition ?? origin;
        int cell = TerrainMath.DeriveSeed(TerrainMath.DeriveSeed(Mathf.FloorToInt(anchor.X), Mathf.FloorToInt(anchor.Y)),
            Mathf.FloorToInt(anchor.Z));
        return ws.DailyRandom(LOOT_SALT ^ cell);
    }
}
