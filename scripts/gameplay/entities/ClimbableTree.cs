using Godot;

// A tree the player can climb to scout from above. The Climb action lifts the
// player into the bird's-eye overlook, hides their sprite, and conceals them
// from mobs (Player.EnterClimbableTree). All of that state lives on the Player,
// not here — the tree is a stateless trigger, so the player stays perched even
// if this entity's chunk streams out underneath them. The player leaves the
// canopy by ending bird's-eye (ESC) or by taking damage from any source; the
// matching restore runs in Player.OnBirdsEyeReturnComplete.
[GlobalClass]
public partial class ClimbableTree : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _hudNode;
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    // Stashed from GetActions so Complete (which only receives the action index)
    // can act on the climbing player. Mirrors Loot._picker. No other runtime
    // state — the tree is a stateless trigger.
    private Player _climber;

    public void OnSpawned(World world) { }

    public bool CanInteract()
    {
        return true;
    }

    public bool CanActorInteract(Player player)
    {
        if (player == null)
        {
            return false;
        }
        // Can't climb while already perched in a tree or otherwise in the
        // bird's-eye overlook — the climb would no-op in EnterClimbableTree
        // anyway, so suppress the prompt to keep it honest.
        return CanInteract() && !player.IsHidden && !player.IsBirdsEye;
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        _climber = player;
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        _climber?.EnterClimbableTree();
    }

    public static ClimbableTree Create(World world, ClimbableTreeSimState data)
    {
        var instance = data.Scene.Instantiate<ClimbableTree>();
        instance.Position = data.WorldPosition;
        world.AddChild(instance);
        return instance;
    }
}
