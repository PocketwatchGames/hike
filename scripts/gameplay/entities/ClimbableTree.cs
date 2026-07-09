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
    // Leaf tint applied to this tree's canopy while a player is perched in it, so
    // the tree they climbed reads distinctly during the scout overview. Overrides
    // both per-cluster tint axes (leaf_tint_a/b) on each FoliageMultiMesh; the
    // authored tints are stashed and restored on descent.
    [Export] private Color _climbedLeafTint = new Color(1.0f, 0.72f, 0.18f);
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    // Stashed from GetActions so Complete (which only receives the action index)
    // can act on the climbing player.
    private Player _climber;

    // Saved authored leaf tints (a, b) per canopy MultiMesh so the highlight can
    // be reverted exactly. Empty when not currently highlighted.
    private readonly System.Collections.Generic.List<(FoliageMultiMesh mesh, Color a, Color b)> _stashedLeafTints = new();

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
        if (_climber == null)
        {
            return;
        }
        SetClimbedHighlight(true);
        _climber.EnterClimbableTree(this);
    }

    // Tint (or restore) this tree's canopy so the climbed tree stands out during
    // the scout overview. Idempotent: turning it on stashes each FoliageMultiMesh's
    // authored leaf tints once; turning it off restores and clears the stash.
    public void SetClimbedHighlight(bool on)
    {
        if (on)
        {
            if (_stashedLeafTints.Count > 0)
            {
                return;
            }
            CollectFoliage(this, mesh =>
            {
                if (mesh.MaterialOverride is not ShaderMaterial mat)
                {
                    return;
                }
                Color a = (Color)mat.GetShaderParameter("leaf_tint_a");
                Color b = (Color)mat.GetShaderParameter("leaf_tint_b");
                _stashedLeafTints.Add((mesh, a, b));
                mat.SetShaderParameter("leaf_tint_a", _climbedLeafTint);
                mat.SetShaderParameter("leaf_tint_b", _climbedLeafTint);
            });
        }
        else
        {
            foreach ((FoliageMultiMesh mesh, Color a, Color b) in _stashedLeafTints)
            {
                if (mesh != null && GodotObject.IsInstanceValid(mesh) && mesh.MaterialOverride is ShaderMaterial mat)
                {
                    mat.SetShaderParameter("leaf_tint_a", a);
                    mat.SetShaderParameter("leaf_tint_b", b);
                }
            }
            _stashedLeafTints.Clear();
        }
    }

    static void CollectFoliage(Node node, System.Action<FoliageMultiMesh> visit)
    {
        if (node is FoliageMultiMesh mesh)
        {
            visit(mesh);
        }
        foreach (Node child in node.GetChildren())
        {
            CollectFoliage(child, visit);
        }
    }

    public static ClimbableTree Create(World world, ClimbableTreeSimState data)
    {
        var instance = data.Scene.Instantiate<ClimbableTree>();
        instance.Position = data.WorldPosition;
        world.AddChild(instance);
        return instance;
    }
}
