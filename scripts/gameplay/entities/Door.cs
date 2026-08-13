using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class Door : Node3D, IInteractive, IWorldEntity
{
    [Export] private StaticBody3D _blockCollider;
    // Pivot placed at the leaf's hinge edge with the door mesh parented under
    // it; the leaf swings about this node's Y.
    [Export] private Node3D _hinge;
    [Export(PropertyHint.Range, "0,180,1")] private float _openAngleDeg = 95f;
    [Export] private float _openSeconds = 0.35f;
    // Height in voxels of the doorway column a CLOSED door makes opaque,
    // measured up from the doorway base. Authored per door scene rather than
    // assumed: the editor's Door brush carves an opening of its own tunable
    // doorHeight, and an occluder shorter than the opening leaves lit, walkable
    // cells above the leaf.
    [Export(PropertyHint.Range, "1,8,1")] private int _occluderHeight = 2;
    [Export] private HurtBox _hurtBox;
    [Export] private Node3D _hudNode;
    // Authored interaction list. Doors are typically instant Open
    // (durationSeconds=0); add a Lockpick entry for locked doors.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    // Doors are solid walls — their block collider is a plain StaticBody3D on
    // Environment (not a PorousBody) so smell, sound, sight, and flight are all
    // stopped, unlike porous interactives (chests, wells).

    private bool _open;
    private DoorSimState _interactiveState;
    private WorldState _worldData;
    private Sim _world;
    private Vector3I _baseWorldPos;

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
    }

    public void OnSpawned(Sim sim) { }

    public bool CanInteract()
    {
        return true;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    private void UpdateVisuals(bool animate)
    {
        if (_hinge == null)
        {
            return;
        }
        float target = Mathf.DegToRad(_open ? _openAngleDeg : 0f);
        if (animate)
        {
            CreateTween().TweenProperty(_hinge, "rotation:y", target, _openSeconds)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
        else
        {
            Vector3 r = _hinge.Rotation;
            r.Y = target;
            _hinge.Rotation = r;
        }
    }

    public void Complete(int actionIndex)
    {
        _open = !_open;
        _interactiveState.Active = !_open;

        // Toggle movement blocker
        _blockCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = _open;

        UpdateVisuals(true);

        // Doorway voxels follow the new state, then relight what they changed.
        // Lighting only — a Barrier has no geometry (Density.TypeDensity skips
        // it), and terrain sun now comes from the light volume rather than the
        // vertex bake, so nothing about the chunk mesh has moved.
        var changed = new List<Vector3I>();
        EntityVoxelStamper.Apply(_worldData, _interactiveState.ResolveStamp(_worldData), changed);
        if (changed.Count > 0)
        {
            _world.UpdateLighting(changed);
        }
    }

    // Bottom cell of the doorway, resolved once and cached on the sim state.
    // Caching is not just for speed: once a closed door has stamped its
    // Barrier, re-deriving would step up THROUGH that barrier and land a cell
    // too high.
    public static Vector3I ResolveOccluderBase(WorldState world, DoorSimState data)
    {
        if (data.OccluderBase.HasValue)
        {
            return data.OccluderBase.Value;
        }
        Vector3I cell = ResolveDoorwayBase(world, data.WorldPosition);
        data.OccluderBase = cell;
        return cell;
    }

    // The seat position comes from a raycast against the MESHED surface, which
    // sits fractionally below the cell boundary (4.996 for a floor whose top is
    // at y=5), so flooring it alone lands on the floor block. Step up out of
    // solid to the first empty cell — the same rule the editor's door brush
    // uses (WorldEditor.FindFloorY). Barrier doesn't count as solid here: it is
    // an invisible light/nav marker with no geometry (Density.TypeDensity skips
    // it too), so a doorway that already holds one must not push the base up.
    private static Vector3I ResolveDoorwayBase(WorldState world, Vector3 position)
    {
        const int MaxStepUp = 2;
        int x = Mathf.FloorToInt(position.X);
        int y = Mathf.FloorToInt(position.Y);
        int z = Mathf.FloorToInt(position.Z);
        for (int i = 0; i < MaxStepUp; i++)
        {
            int v = world.GetBlockWorld(x, y, z);
            if (!Blocks.IsSolid(v) || v == Blocks.BarrierId)
            {
                break;
            }
            y++;
        }
        return new Vector3I(x, y, z);
    }

    // Per-scene occluder height for callers that hold only a PackedScene — the
    // load-time stamp runs before any door node exists. Must match the [Export]
    // above in both name and default, since a door scene sitting on the default
    // stores no value to read.
    private const int DEFAULT_OCCLUDER_HEIGHT = 2;
    private static readonly ScenePropertyCache _occluderHeights =
        new ScenePropertyCache("_occluderHeight", DEFAULT_OCCLUDER_HEIGHT);

    public static int GetOccluderHeight(PackedScene scene)
    {
        return _occluderHeights.Get(scene);
    }

    public static Door Create(Sim sim, DoorSimState data)
    {
        var instance = data.Scene.Instantiate<Door>();
        data.SeatTransform(instance);
        instance._interactiveState = data;
        instance._worldData = sim.WorldState;
        instance._world = sim;
        // An unresolved base means no load-time stamp has seen this door — it
        // was placed live in the editor, so it has to stamp itself. Doors from
        // the world file are already reconciled by EntityVoxelStamper, and their
        // voxels persist in WorldState across chunk load/unload.
        bool needsStamp = !data.OccluderBase.HasValue;
        instance._baseWorldPos = ResolveOccluderBase(sim.WorldState, data);
        sim.AddChild(instance);

        instance._open = !data.Active;
        instance._blockCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = instance._open;
        instance.UpdateVisuals(false);

        if (needsStamp)
        {
            var changed = new List<Vector3I>();
            EntityVoxelStamper.Apply(sim.WorldState, data.ResolveStamp(sim.WorldState), changed);
            if (changed.Count > 0)
            {
                sim.UpdateLighting(changed);
            }
        }

        return instance;
    }

}
