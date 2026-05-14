using Godot;

// A stone inscribed in some language. Reading it grants every concept on
// `_concepts` AND shows the inscription via the same HUD panel as Signpost —
// the text flips from scrambled to legible the moment the player has all
// `_inscriptionLanguage` components. Subsequent reads silently re-show the
// legible text.
//
// The concepts and the inscription language are intentionally decoupled:
// most stones teach their own inscription language (a LanguageTeachable for
// `_inscriptionLanguage` lives in `_concepts`), but a stone written in a
// language the player already knows can teach something else (a recipe, a
// map region) — the inscription field still drives the scramble while the
// concepts drive the grant. NPCs follow the same pattern through the
// LearnConcept ItemEvent on a Talk action's completionEvents.
//
// Learning happens INSIDE Complete() rather than via a LearnConcept event in
// the action's completionEvents because the read-and-reveal flow is tightly
// coupled: we need an unambiguous "learn first, then display" ordering even
// on a zero-duration interactive where every completionEvent fires in the
// same call stack. Doing the learn inline guarantees that ordering without
// depending on event-array iteration quirks. The LearnConcept ItemEvent
// flag still exists for sources that can use the event-driven flow (scrolls,
// NPC dialogue) — they don't have a parent Complete() to host the work.
[GlobalClass]
public partial class KnowledgeStone : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _hudNode;
    // Authored interactive actions. The default action only needs an
    // OpenInteractive event in its completionEvents — Complete() handles
    // the learning + fx in code, before showing the inscription.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Inscription shown on Read. Pre-learning it scrambles through
    // TextScrambler keyed on `_inscriptionLanguage`; post-learning it shows raw.
    [Export(PropertyHint.MultilineText)] private string _text = "";
    // Language the inscription is written in (drives the TextScrambler
    // gating on display). Separate from what the stone teaches — most
    // stones teach their own inscription language via a LanguageTeachable
    // in `_concepts`, but the two fields are independent so a stone can
    // teach a recipe / region while still presenting as inscribed text.
    [Export] private LanguageData _inscriptionLanguage;
    // Concepts granted on read. Polymorphic — language pieces, recipes,
    // map-region locations, future skills. Each concept's Teach() is called
    // in order; _firstLearnEffect fires once if ANY return true (so a stone
    // that teaches two concepts the player already has one of still gets
    // the celebration when the second one lands).
    [Export] private Godot.Collections.Array<TeachableConcept> _concepts = new();
    // FX spawned on the player the first time this read newly grants any
    // concept. Subsequent reads — including re-reads of the same stone, or
    // reads of a stone whose entire concept set the player already knows —
    // are silent.
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
        // Learn first, then display. Each concept's Teach() returns true
        // only when it newly granted something, so the firstLearnEffect
        // gates on the OR across the concept array — a stone teaching two
        // things the player already knows one of still gets the
        // celebration when the second one lands.
        bool learnedSomething = false;
        if (player != null && _concepts != null)
        {
            for (int i = 0; i < _concepts.Count; i++)
            {
                TeachableConcept concept = _concepts[i];
                if (concept != null && concept.Teach(player))
                {
                    learnedSomething = true;
                }
            }
        }
        if (learnedSomething && _firstLearnEffect != null)
        {
            Fx.Create(_firstLearnEffect, player, Vector3.Zero);
        }
        // Display scramble is gated on the inscription language only — the
        // other concept types don't affect text legibility. A stone teaching
        // only a recipe / region renders its inscription scrambled until
        // some other source teaches the inscription language.
        ELanguageComponents missing = player == null || _inscriptionLanguage == null
            ? ELanguageComponents.None
            : ELanguageComponents.All & ~player.GetLearnedComponents(_inscriptionLanguage);
        string display = missing == ELanguageComponents.None
            ? _text
            : TextScrambler.Scramble(_text, _inscriptionLanguage, missing);
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
        if (data.InscriptionLanguage != null)
        {
            instance._inscriptionLanguage = data.InscriptionLanguage;
        }
        // SimState concept overrides REPLACE the scene's authored set —
        // worldgen / world-file placements drive the full teach list. The
        // scene's authored _concepts stays in place when the SimState
        // doesn't carry an override (null/empty), so authored-only stones
        // (those placed by hand in a scene file with no SimState mutation)
        // keep working.
        if (data.Concepts != null && data.Concepts.Count > 0)
        {
            instance._concepts = data.Concepts;
        }
        world.AddChild(instance);
        return instance;
    }
}
