using Godot;

// A stone inscribed in some language. Reading it grants every concept on
// `_concepts` AND shows the inscription via the same HUD panel as Signpost —
// the text flips from scrambled to legible the moment the player has all
// `_inscriptionLanguage` components. Subsequent reads silently re-show the
// legible text.
//
// The concepts and the inscription language are decoupled: the inscription
// field drives the scramble while the concepts drive the grant, so a stone
// written in a known language can teach something else (a recipe, a map
// region).
//
// Learning happens INSIDE Complete() rather than via a LearnConcept event so
// the "learn first, then display" ordering is unambiguous even on a
// zero-duration interactive where every completionEvent fires in one call
// stack. The LearnConcept ItemEvent flag still serves sources with no parent
// Complete() (scrolls, NPC dialogue).
[GlobalClass]
public partial class KnowledgeStone : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _hudNode;
    // The default action only needs an OpenInteractive event — Complete()
    // handles the learning + fx in code, before showing the inscription.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Inscription shown on Read. Scrambles through TextScrambler keyed on
    // `_inscriptionLanguage` until learned, then shows raw.
    [Export(PropertyHint.MultilineText)] private string _text = "";
    // Language the inscription is written in (drives the TextScrambler gating).
    [Export] private LanguageData _inscriptionLanguage;
    // Concepts granted on read. Each concept's Teach() is called in order;
    // _firstLearnEffect fires once if ANY return true.
    [Export] private Godot.Collections.Array<TeachableConcept> _concepts = new();
    // FX spawned on the player the first time this read newly grants any
    // concept. Subsequent reads are silent.
    [Export] private PackedScene _firstLearnEffect;
    // Looping holy ambience (sound + rising particle column) spawned as a child
    // when the stone enters the world; freed with the stone on chunk unload.
    [Export] private PackedScene _ambientLoopEffect;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    private KnowledgeStoneSimState _simState;

    public void OnSpawned(Sim sim)
    {
        // Register what this stone teaches so its map marker can dim once the
        // party has learned all of it (SimState.IsMarkerActive). Keyed by the
        // stone's position, which matches the sibling MapMarker's (no local offset).
        sim?.WorldState?.SimState?.SetKnowledgeStoneConcepts(GlobalPosition, _concepts);
        if (_ambientLoopEffect != null)
        {
            Fx.Create(_ambientLoopEffect, this, Vector3.Zero);
        }
    }

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
        // Learn first, then display. Teach() returns true only when it newly
        // granted something, so firstLearnEffect gates on the OR across the array.
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

    public static KnowledgeStone Create(Sim sim, KnowledgeStoneSimState data)
    {
        var instance = data.Scene.Instantiate<KnowledgeStone>();
        data.SeatTransform(instance);
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
        sim.AddChild(instance);
        return instance;
    }
}
