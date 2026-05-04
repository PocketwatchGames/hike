using Godot;

[GlobalClass]
public partial class Signpost : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _hudNode;
    // Authored interaction list. Author one entry with completionEvents
    // containing an OpenInteractive event so Complete() fires on press.
    // durationSeconds is typically 0 — signposts pop their text instantly.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Fallback text for signposts placed directly in a scene (test rigs,
    // prefab variants). The SimState's Text overrides this when the signpost
    // is spawned by the world loader.
    [Export(PropertyHint.MultilineText)] private string _text = "";

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    private SignpostSimState _interactiveState;

    public void OnSpawned(World world) { }

    public bool CanInteract() => true;
    public bool CanActorInteract(Player player) => CanInteract();

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        Hud hud = GameClient.Current?.hud;
        if (hud != null)
        {
            hud.ShowSignpost(_text);
        }
    }

    public static Signpost Create(World world, SignpostSimState data)
    {
        var instance = data.Scene.Instantiate<Signpost>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        if (!string.IsNullOrEmpty(data.Text))
        {
            instance._text = data.Text;
        }
        world.AddChild(instance);
        return instance;
    }
}
