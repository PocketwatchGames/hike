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
    // Components of `_language` this stone teaches when read. A stone
    // typically grants one of the four pieces (Grammar / Numbers / Glyphs
    // / Spelling); a hand-authored "master" stone could grant All. Per-
    // instance override flows through KnowledgeStoneSimState.Components.
    [Export, CompactFlags] private ELanguageComponents _components = ELanguageComponents.All;
    // FX spawned on the player the first time this read adds a new
    // component to the player's learned-set for `_language`. Subsequent
    // reads — including re-reads of the same stone, or reads of a stone
    // that only teaches components the player already had — are silent.
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
        // Learn first, then display. LearnLanguageComponents returns true
        // only when this read actually added a new component bit, so the
        // firstLearnEffect doesn't fire on re-reads or on a stone teaching
        // a component the player already has. The scramble check below
        // then runs against the freshly-updated learned-set.
        if (player != null && player.LearnLanguageComponents(_language, _components) && _firstLearnEffect != null)
        {
            Fx.Create(_firstLearnEffect, player, Vector3.Zero);
        }
        ELanguageComponents missing = player == null
            ? ELanguageComponents.None
            : ELanguageComponents.All & ~player.GetLearnedComponents(_language);
        string display = missing == ELanguageComponents.None
            ? _text
            : TextScrambler.Scramble(_text, _language, missing);
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
        if (data.Components != ELanguageComponents.None)
        {
            instance._components = data.Components;
        }
        world.AddChild(instance);
        return instance;
    }
}
