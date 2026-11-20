using Godot;

[GlobalClass]
public partial class Signpost : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _hudNode;
    // Authored interaction list. Author one entry with completionEvents
    // containing an OpenInteractive event so Complete() fires on press.
    // durationSeconds is typically 0 — signposts pop their text instantly.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    private SignpostSimState _interactiveState;
    // Set from the sim state; the scene carries no words of its own.
    private InscriptionData _inscription;

    public void OnSpawned(Sim sim) { }

    public bool CanInteract() => true;
    public bool CanActorInteract(Player player) => CanInteract();

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(InteractiveAction action)
    {
        GameClient gc = GameClient.Current;
        Hud hud = gc?.hud;
        if (hud != null && _inscription != null)
        {
            Player player = gc?.Player;
            InscriptionData inscription = _inscription;
            // Teach first, then render, so a sign teaching its own tongue reads
            // with what it just taught (InscriptionData.percent; most teach nothing).
            hud.ShowReadable(() =>
            {
                inscription.Teach(player);
                return LanguageText.RenderBbcode(inscription.LookUpText(), inscription.language, player);
            }, this);
        }
    }

    public static Signpost Create(Sim sim, SignpostSimState data)
    {
        var instance = data.Scene.Instantiate<Signpost>();
        data.SeatTransform(instance);
        instance._interactiveState = data;
        instance._inscription = data.Inscription;
        sim.AddChild(instance);
        return instance;
    }
}
