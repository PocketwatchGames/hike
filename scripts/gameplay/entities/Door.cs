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
        ApplyOcclusion(_worldData, _interactiveState, changed);
        if (changed.Count > 0)
        {
            _world.UpdateLighting(changed);
        }
    }

    // Writes the doorway column to match the door's state: Barrier while closed
    // (opaque to sunlight, block light and navigation), Opening while open.
    // Shared by the load-time stamp (DoorOcclusionStamper) and the runtime toggle
    // so the two can never disagree about which cells a door owns. Cells actually
    // changed are appended to `changed` for the caller's relight.
    public static void ApplyOcclusion(WorldState world, DoorSimState data, List<Vector3I> changed)
    {
        // Active == closed. Door.Create derives its own _open the same way.
        //
        // Open writes Opening, not Air. Opening is empty in every sense that
        // matters here — passable, invisible, transparent to light — but the
        // ceiling cutaway reads it as wall, so the masonry above a doorway stays
        // put instead of appearing and disappearing as the door swings.
        //
        // This is NOT a substitute for authoring the aperture. Apertures are
        // painted Opening in the editor, every doorway and window, whatever its
        // width — a door only ever owns a single column (see ResolveOccluderBase),
        // so it could not carry a wider one anyway. What this write buys is that
        // a door can never DESTROY the painted aperture by swinging open, which
        // is what would happen if the open state reverted to plain Air.
        VoxelType voxel = data.Active ? VoxelType.Barrier : VoxelType.Opening;
        Vector3I baseCell = ResolveOccluderBase(world, data);
        int height = Mathf.Max(1, GetOccluderHeight(data.Scene));
        for (int i = 0; i < height; i++)
        {
            var cell = new Vector3I(baseCell.X, baseCell.Y + i, baseCell.Z);
            VoxelType existing = world.GetVoxelWorld(cell.X, cell.Y, cell.Z);
            // Only ever write empty cells or the door's own markers. Authored
            // geometry is never overwritten: a seat position that resolved onto
            // a floor or wall block would otherwise erase it on open and leave a
            // hole the player falls through. Opening has to be accepted here as
            // well as Air — it is what the open state writes, so rejecting it
            // would let a door stamp Opening once and then never be able to close
            // back to Barrier. An Opening an AUTHOR painted is equally fine to
            // take over: both states the door writes block the cutaway anyway,
            // which is exactly what that author was asking for.
            if (existing != VoxelType.Air && existing != VoxelType.Barrier && existing != VoxelType.Opening)
            {
                continue;
            }
            if (existing == voxel)
            {
                continue;
            }
            world.SetVoxelWorld(cell.X, cell.Y, cell.Z, voxel);
            changed?.Add(cell);
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
            VoxelType v = world.GetVoxelWorld(x, y, z);
            if (!VoxelTypeInfo.IsSolid(v) || v == VoxelType.Barrier)
            {
                break;
            }
            y++;
        }
        return new Vector3I(x, y, z);
    }

    // Per-scene occluder height for callers that hold only a PackedScene — the
    // load-time stamp runs before any door node exists. Instantiated once per
    // scene and freed, mirroring FoliageOccluderCache.
    private static readonly Dictionary<string, int> _occluderHeightByScene = new();

    public static int GetOccluderHeight(PackedScene scene)
    {
        const int FallbackHeight = 2;
        if (scene == null)
        {
            return FallbackHeight;
        }
        string key = scene.ResourcePath;
        if (!string.IsNullOrEmpty(key) && _occluderHeightByScene.TryGetValue(key, out int cached))
        {
            return cached;
        }
        int height = FallbackHeight;
        if (scene.Instantiate() is Node root)
        {
            if (root is Door door)
            {
                height = door._occluderHeight;
            }
            root.Free();
        }
        if (!string.IsNullOrEmpty(key))
        {
            _occluderHeightByScene[key] = height;
        }
        return height;
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
        // the world file are already reconciled by DoorOcclusionStamper, and
        // their voxels persist in WorldState across chunk load/unload.
        bool needsStamp = !data.OccluderBase.HasValue;
        instance._baseWorldPos = ResolveOccluderBase(sim.WorldState, data);
        sim.AddChild(instance);

        instance._open = !data.Active;
        instance._blockCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = instance._open;
        instance.UpdateVisuals(false);

        if (needsStamp)
        {
            var changed = new List<Vector3I>();
            ApplyOcclusion(sim.WorldState, data, changed);
            if (changed.Count > 0)
            {
                sim.UpdateLighting(changed);
            }
        }

        return instance;
    }

}
