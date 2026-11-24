using System.Collections.Generic;
using Godot;

// The live half of a bomb wall (BombWallSimState): hurtboxes over its open faces
// and a health pool that regenerates. The wall itself is voxels, so this node
// draws nothing — the crack overlay on the rock is the whole visual.
//
// Breaking is a DPS race against `_regenPerSecond`, not a hit count: anything
// that damages it chips at the pool, and what the wall is weak to is scaled by
// `_damageTaken`. A weapon whose sustained damage stays under the regen never
// gets through, however long it swings. Health is not saved — a wall left alone
// is whole again within seconds anyway.
[GlobalClass]
public partial class BombWall : Node3D
{
    // Authored hurtbox carrying the layer and mask; its shapes are replaced by
    // slabs over the wall's open faces, one copy per face direction. A blast's
    // line-of-sight ray aims at a hurtbox's FIRST shape, so each side needs its own
    // box whose first slab sits in front of that side — one box over every face
    // would be shadowed by the wall from whichever side its first slab is not on.
    [Export] private HurtBox _hurtBox;

    [Export(PropertyHint.Range, "1,2000,1,or_greater")] private float _maxHealth = 150f;
    [Export(PropertyHint.Range, "0,500,1,or_greater")] private float _regenPerSecond = 50f;

    // Multipliers on incoming damage by hit tag, every matching one applied — a
    // bomb is Blunt AND Explosive, so both stack. Applied to health damage for
    // any tag, unlike a creature's resistances (where Blunt scales armour chip):
    // a wall has no armour, so for it a blunt blow simply IS more damage.
    [Export] private Godot.Collections.Array<TagModifier> _damageTaken = new();

    // How far the hurtbox slabs stand out of the face, in metres. They start a
    // little inside the rock so a mace pressed to the wall still reaches them.
    [Export(PropertyHint.Range, "0.1,2,0.05")] private float _slabDepth = 0.6f;
    [Export(PropertyHint.Range, "0,0.5,0.05")] private float _slabInset = 0.1f;

    // Where each side is AIMED at: a small shape this far out from the middle of
    // its face, put first so it is the hurtbox's Center. Blasts and swings test
    // line of sight to Center, and the terrain mesh smooths a voxel face into a
    // slope that bulges past it — a target on the face itself reads as behind
    // rock from every angle.
    [Export(PropertyHint.Range, "0.2,2,0.05")] private float _sightPointDistance = 0.8f;
    [Export(PropertyHint.Range, "0.05,0.5,0.05")] private float _sightPointRadius = 0.15f;

    // Played where a hit lands, and over the faces when it gives way.
    [Export] private PackedScene _hitEffect;
    [Export] private PackedScene _breakEffect;
    // The break effect plays on at most this many faces, spread over the wall.
    [Export(PropertyHint.Range, "1,32,1")] private int _breakEffectMaxCount = 8;

    private BombWallSimState _state;
    private TagModifier[] _multipliers;
    private readonly List<Vector3> _faceCenters = new();
    private float _damage;
    private ulong _damageAtMs;
    private ulong _lastHitFrame = ulong.MaxValue;
    private bool _breaking;

    private static readonly Vector3I[] FaceDirs =
    {
        Vector3I.Right, Vector3I.Left, Vector3I.Up, Vector3I.Down, Vector3I.Back, Vector3I.Forward,
    };

    public static BombWall Create(Sim sim, BombWallSimState state)
    {
        var instance = state.Scene.Instantiate<BombWall>();
        // Placed at its anchor but NOT turned: the slabs are laid out in world
        // axes from world cells, and a rotated root would swing them off the rock.
        instance.Position = state.WorldPosition;
        instance._state = state;
        instance.BuildHurtBoxes(sim.WorldState);
        sim.AddChild(instance);
        return instance;
    }

    public override void _Ready()
    {
        int count = _damageTaken?.Count ?? 0;
        _multipliers = new TagModifier[count];
        for (int i = 0; i < count; i++)
        {
            _multipliers[i] = _damageTaken[i];
        }
    }

    // One hurtbox per face direction, its slabs ordered nearest-the-middle first
    // so the line-of-sight target sits mid-wall rather than at an edge.
    private void BuildHurtBoxes(WorldState ws)
    {
        if (_hurtBox == null)
        {
            GD.PushError($"BombWall '{SceneFilePath}' has no _hurtBox.");
            return;
        }
        foreach (Node child in _hurtBox.GetChildren())
        {
            _hurtBox.RemoveChild(child);
            child.QueueFree();
        }
        var sides = new List<(Vector3I Dir, List<Vector3I> Open)>();
        foreach (Vector3I dir in FaceDirs)
        {
            List<Vector3I> open = OpenCells(ws, dir);
            if (open.Count > 0)
            {
                sides.Add((dir, open));
            }
        }
        if (sides.Count == 0)
        {
            GD.PushWarning($"BombWall at {Position}: none of its {_state.Cells.Length} cells has an open face; it cannot be hit.");
            return;
        }
        // Every copy is taken from the EMPTY template, before any side fills it —
        // a duplicate carries the children it is made from.
        var boxes = new List<HurtBox> { _hurtBox };
        for (int i = 1; i < sides.Count; i++)
        {
            var copy = (HurtBox)_hurtBox.Duplicate();
            AddChild(copy);
            boxes.Add(copy);
        }
        for (int i = 0; i < sides.Count; i++)
        {
            (Vector3I dir, List<Vector3I> open) = sides[i];
            HurtBox box = boxes[i];
            box.OnHit = OnHurtBoxHit;
            box.PredictHit = _ => new HitPrediction(EHitResult.Object, EDamageTriggerFlags.None);

            Vector3 mean = Vector3.Zero;
            foreach (Vector3I cell in open)
            {
                mean += FaceCenter(cell, dir);
            }
            mean /= open.Count;
            box.AddChild(new CollisionShape3D
            {
                Shape = new SphereShape3D { Radius = _sightPointRadius },
                Position = mean + (Vector3)dir * _sightPointDistance - Position,
            });
            foreach (Vector3I cell in open)
            {
                box.AddChild(MakeSlab(cell, dir));
                _faceCenters.Add(FaceCenter(cell, dir) - Position);
            }
        }
    }

    private List<Vector3I> OpenCells(WorldState ws, Vector3I dir)
    {
        var open = new List<Vector3I>();
        foreach (Vector3I cell in _state.Cells)
        {
            if (!ws.IsInBounds(cell.X, cell.Y, cell.Z) || !Blocks.HasGeometry(ws.GetBlockWorld(cell.X, cell.Y, cell.Z)))
            {
                continue;
            }
            Vector3I n = cell + dir;
            if (ws.IsInBounds(n.X, n.Y, n.Z) && !Blocks.HasGeometry(ws.GetBlockWorld(n.X, n.Y, n.Z)))
            {
                open.Add(cell);
            }
        }
        return open;
    }

    private static Vector3 FaceCenter(Vector3I cell, Vector3I dir)
    {
        return (Vector3)cell + new Vector3(0.5f, 0.5f, 0.5f) + (Vector3)dir * 0.5f;
    }

    private CollisionShape3D MakeSlab(Vector3I cell, Vector3I dir)
    {
        Vector3 axis = ((Vector3)dir).Abs();
        Vector3 size = Vector3.One - axis + axis * _slabDepth;
        Vector3 center = FaceCenter(cell, dir) + (Vector3)dir * (_slabDepth * 0.5f - _slabInset);
        return new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = size },
            Position = center - Position,
        };
    }

    private void OnHurtBoxHit(HitInfo hit)
    {
        // One blow can overlap the hurtboxes of two sides at a corner; it counts once.
        ulong frame = Engine.GetPhysicsFrames();
        if (_breaking || frame == _lastHitFrame || hit.healthDamage <= 0f)
        {
            return;
        }
        _lastHitFrame = frame;
        Sim sim = Sim.Current;
        if (sim == null)
        {
            return;
        }
        ulong now = sim.GameTimeMs;
        _damage = DamageAt(now) + hit.healthDamage * ComposeMultiplier(hit.tags);
        _damageAtMs = now;
        if (_damage >= _maxHealth)
        {
            Break(sim);
            return;
        }
        if (_hitEffect != null)
        {
            // A hit without a point (a blast) chips the face turned toward its source.
            Vector3 from = (hit.source as Node3D)?.GlobalPosition ?? GlobalPosition;
            Vector3 at = hit.hitPoint ?? (GlobalPosition + NearestFaceTo(from));
            Fx.Create(_hitEffect, sim, sim.ToLocal(at));
        }
    }

    // Damage still outstanding at `nowMs`, after the regen since the last hit.
    private float DamageAt(ulong nowMs)
    {
        float seconds = (nowMs - _damageAtMs) / 1000f;
        return Mathf.Max(0f, _damage - _regenPerSecond * seconds);
    }

    private float ComposeMultiplier(EHitTag tags)
    {
        float product = 1f;
        foreach (TagModifier m in _multipliers)
        {
            if (m != null && (tags & m.tag) != 0)
            {
                product *= m.value;
            }
        }
        return product;
    }

    private Vector3 NearestFaceTo(Vector3 worldPoint)
    {
        Vector3 local = worldPoint - GlobalPosition;
        Vector3 best = Vector3.Zero;
        float bestDist = float.MaxValue;
        foreach (Vector3 face in _faceCenters)
        {
            float dist = face.DistanceSquaredTo(local);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = face;
            }
        }
        return best;
    }

    private void Break(Sim sim)
    {
        _breaking = true;
        if (_breakEffect != null && _faceCenters.Count > 0)
        {
            int count = Mathf.Min(_breakEffectMaxCount, _faceCenters.Count);
            float stride = (float)_faceCenters.Count / count;
            for (int i = 0; i < count; i++)
            {
                Vector3 at = GlobalPosition + _faceCenters[Mathf.FloorToInt(i * stride)];
                Fx.Create(_breakEffect, sim, sim.ToLocal(at));
            }
        }
        // Deferred: a hit can land inside the physics flush (a hazard's
        // area-entered signal), where bodies may not be freed or rebuilt.
        BombWallSimState state = _state;
        Callable.From(() => sim.BreakBombWall(state)).CallDeferred();
    }
}
