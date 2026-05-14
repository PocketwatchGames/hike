using Godot;

// A stone inscribed in some language. Reading it teaches the player the
// stone's language AND shows the inscription via the same HUD panel as
// Signpost — the text flips from scrambled to legible the moment the
// language is learned. Subsequent reads silently re-show the legible text.
//
// Learning happens INSIDE Complete() rather than via a LearnLanguage
// ItemEvent in the action's completionEvents because the read-and-reveal
// flow is tightly coupled: we need an unambiguous "learn first, then
// display" ordering even on a zero-duration interactive where every
// completionEvent fires in the same call stack. Doing the learn inline
// guarantees that ordering without depending on event-array iteration
// quirks. The LearnLanguage ItemEvent flag still exists for sources that
// can use the event-driven flow (language-teaching consumables, mob
// dialogue) — they don't have a parent Complete() to host the work.
[GlobalClass]
public partial class KnowledgeStone : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _hudNode;
    // Authored interactive actions. The default action only needs an
    // OpenInteractive event in its completionEvents — Complete() handles
    // the learning + fx in code, before showing the inscription.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Inscription shown on Read. Pre-learning it scrambles through
    // TextScrambler keyed on `_language`; post-learning it shows raw.
    [Export(PropertyHint.MultilineText)] private string _text = "";
    // Language the inscription is written in AND the language that reading
    // the stone teaches. Single field — when the player reads, this gets
    // added to their LearnedLanguages set, and the same field gates the
    // scramble of the displayed text. Per-instance override goes through
    // KnowledgeStoneSimState (worldgen / world file).
    [Export] private LanguageData _language;
    // FX spawned on the player the first time a given player reads this
    // stone (Player.LearnLanguage returns true → first add). Subsequent
    // reads — including re-reading the same stone — are silent.
    [Export] private PackedScene _firstLearnEffect;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    private KnowledgeStoneSimState _simState;

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
        if (hud == null)
        {
            return;
        }
        Player player = gc?.Player;
        // Learn first, then display. LearnLanguage returns true only on the
        // first add; gate the firstLearnEffect on that so re-reads of the
        // same stone are silent. The scramble check below then resolves to
        // the legible branch in the same call.
        if (player != null && player.LearnLanguage(_language) && _firstLearnEffect != null)
        {
            Fx.Create(_firstLearnEffect, player, Vector3.Zero);
        }
        string display = player == null || player.HasLearnedLanguage(_language)
            ? _text
            : TextScrambler.Scramble(_text, _language);
        hud.ShowSignpost(display, this);
    }

    public static KnowledgeStone Create(World world, KnowledgeStoneSimState data)
    {
        var instance = data.Scene.Instantiate<KnowledgeStone>();
        instance.Position = data.WorldPosition;
        instance._simState = data;
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
