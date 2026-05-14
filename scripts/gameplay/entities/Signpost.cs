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
    // Language the signpost text is written in. The reader can decipher it
    // only if Player.LearnedLanguages contains this resource. Null = legible
    // to everyone (player's own language).
    [Export] private LanguageData _language;

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
        GameClient gc = GameClient.Current;
        Hud hud = gc?.hud;
        if (hud != null)
        {
            Player player = gc?.Player;
            string display = player == null || player.HasLearnedLanguage(_language)
                ? _text
                : TextScrambler.Scramble(_text, _language);
            hud.ShowSignpost(display, this);
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
        if (data.Language != null)
        {
            instance._language = data.Language;
        }
        world.AddChild(instance);
        return instance;
    }
}
