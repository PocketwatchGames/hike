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

        // Update voxel data for light blocking
        VoxelType voxel = _open ? VoxelType.Air : VoxelType.Barrier;
        SetDoorwayVoxel(_baseWorldPos, voxel);
        SetDoorwayVoxel(_baseWorldPos + Vector3I.Up, voxel);

        // Incremental light update and rebuild nearby chunk meshes
        var changed = new System.Collections.Generic.List<Vector3I>
        {
            _baseWorldPos,
            _baseWorldPos + Vector3I.Up,
        };
        _world.RebuildNearbyChunkMeshes(GlobalPosition, changed);
    }

    // Writes one of the two doorway cells, but only when it is empty or already
    // holds the door's own barrier. Authored geometry is never overwritten: a
    // seat position that resolved onto a floor or wall block would otherwise
    // erase it on open and leave a hole the player falls through.
    private void SetDoorwayVoxel(Vector3I cell, VoxelType voxel)
    {
        VoxelType existing = _worldData.GetVoxelWorld(cell.X, cell.Y, cell.Z);
        if (existing != VoxelType.Air && existing != VoxelType.Barrier)
        {
            return;
        }
        _worldData.SetVoxelWorld(cell.X, cell.Y, cell.Z, voxel);
    }

    // Bottom cell of the doorway the closed door makes opaque. The seat
    // position comes from a raycast against the MESHED surface, which sits
    // fractionally below the cell boundary (4.996 for a floor whose top is at
    // y=5), so flooring it alone lands on the floor block. Step up out of solid
    // to the first empty cell — the same rule the editor's door brush uses
    // (WorldEditor.FindFloorY).
    private static Vector3I ResolveDoorwayBase(WorldState world, Vector3 position)
    {
        const int MaxStepUp = 2;
        int x = Mathf.FloorToInt(position.X);
        int y = Mathf.FloorToInt(position.Y);
        int z = Mathf.FloorToInt(position.Z);
        for (int i = 0; i < MaxStepUp && VoxelTypeInfo.IsSolid(world.GetVoxelWorld(x, y, z)); i++)
        {
            y++;
        }
        return new Vector3I(x, y, z);
    }

    public static Door Create(Sim sim, DoorSimState data)
    {
        var instance = data.Scene.Instantiate<Door>();
        data.SeatTransform(instance);
        instance._interactiveState = data;
        instance._worldData = sim.WorldState;
        instance._world = sim;
        instance._baseWorldPos = ResolveDoorwayBase(sim.WorldState, data.WorldPosition);
        sim.AddChild(instance);

        instance._open = !data.Active;
        instance._blockCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = instance._open;
        instance.UpdateVisuals(false);

        return instance;
    }

}
