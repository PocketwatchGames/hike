using Godot;

[GlobalClass]
public partial class Door : Node3D, IInteractive, IWorldEntity
{
    [Export] private StaticBody3D _blockCollider;
    [Export] private Sprite3D _sprite;
    [Export] private LitSpriteAnimator _animator;
    [Export] private HurtBox _hurtBox;
    [Export] private Node3D _hudNode;
    // Authored interaction list. Doors are typically instant Open
    // (durationSeconds=0); add a Lockpick entry for locked doors.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    // Doors are solid walls — their block collider is a plain StaticBody3D on
    // Environment (not a PorousBody) so smell, sound, sight, and flight are all
    // stopped, unlike porous interactives (chests, wells).

    private static readonly StringName AnimOpen = "open";
    private static readonly StringName AnimClosed = "closed";

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

    public void Complete(int actionIndex)
    {
        _open = !_open;
        _interactiveState.Active = !_open;

        // Toggle movement blocker
        _blockCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = _open;

        // Toggle visual: the open animation will only matter once authors
        // supply a real open frame; for now the door art is a single closed
        // frame, so hide the sprite when open.
        _animator.Play(_open ? AnimOpen : AnimClosed);
        _sprite.Visible = !_open;

        // Update voxel data for light blocking
        VoxelType voxel = _open ? VoxelType.Air : VoxelType.Barrier;
        _worldData.SetVoxelWorld(_baseWorldPos.X, _baseWorldPos.Y, _baseWorldPos.Z, voxel);
        _worldData.SetVoxelWorld(_baseWorldPos.X, _baseWorldPos.Y + 1, _baseWorldPos.Z, voxel);

        // Incremental light update and rebuild nearby chunk meshes
        var changed = new System.Collections.Generic.List<Vector3I>
        {
            _baseWorldPos,
            _baseWorldPos + Vector3I.Up,
        };
        _world.RebuildNearbyChunkMeshes(GlobalPosition, changed);
    }

    public static Door Create(Sim sim, DoorSimState data)
    {
        var instance = data.Scene.Instantiate<Door>();
        instance.Position = data.WorldPosition;
        instance.RotationDegrees = new Vector3(0, Mathf.RadToDeg(data.RotationY), 0);
        instance._interactiveState = data;
        instance._worldData = sim.WorldState;
        instance._world = sim;
        instance._baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        sim.AddChild(instance);

        instance._open = !data.Active;
        instance._blockCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = instance._open;
        instance._animator.Play(instance._open ? AnimOpen : AnimClosed);
        instance._sprite.Visible = !instance._open;

        return instance;
    }

}
