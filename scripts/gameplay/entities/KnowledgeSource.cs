using Godot;

// A readable that teaches and stays — a knowledge stone, an open book. Reading
// it grants every concept its sim state carries AND shows the inscription via
// the same HUD panel as Signpost — the text reads as legible as the player's
// coverage of the inscription's language says.
//
// The concepts and the inscription language are decoupled: the inscription
// drives the scramble while the concepts drive the grant, so a source written
// in a known language can teach something else (a recipe, a map region). One
// teaching nothing at all is just read — a tutorial stone.
//
// Learning happens INSIDE Complete() rather than via a LearnConcept event so
// the "learn first, then display" ordering is unambiguous even on a
// zero-duration interactive where every completionEvent fires in one call
// stack. The LearnConcept ItemEvent flag still serves sources with no parent
// Complete() (scrolls, NPC dialogue).
[GlobalClass]
public partial class KnowledgeSource : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _hudNode;
    // The default action only needs an OpenInteractive event — Complete()
    // handles the learning + fx in code, before showing the inscription.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Optional looping ambience (the stone's sound + rising particle column),
    // spawned as a child when the source enters the world.
    [Export] private PackedScene _ambientLoopEffect;
    // Read on the book page (prose, plus the learn meter and the translation
    // when it teaches its language) rather than in a speech bubble over it.
    [Export] private bool _readAsPage;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    private KnowledgeSourceSimState _simState;

    public void OnSpawned(Sim sim)
    {
        // Register what this source teaches so a sibling MapMarker can dim once
        // the party has learned all of it (SimState.IsMarkerActive). Keyed by the
        // source's position, which matches the marker's (no local offset).
        sim?.WorldState?.SimState?.SetKnowledgeSourceMarker(GlobalPosition, _simState);
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

    public void Complete(InteractiveAction action)
    {
        GameClient gc = GameClient.Current;
        Hud hud = gc?.hud;
        if (hud == null || _simState == null)
        {
            return;
        }
        Player player = gc?.Player;
        InscriptionData inscription = _simState.Inscription;
        KnowledgeSourceSimState state = _simState;
        void Teach() => state.Teach(player);
        // Nothing to read: it only teaches, and each lesson announces itself.
        if (inscription == null)
        {
            Teach();
            return;
        }
        if (_readAsPage)
        {
            LanguageData taught = inscription.TeachesLanguage() ? inscription.language : null;
            hud.ShowPage(Teach, inscription.LookUpTitle(), inscription.LookUpText(), inscription.language, taught, player);
            return;
        }
        hud.ShowReadable(() =>
        {
            // Learn first, then render, so a source teaching its own tongue reads
            // legibly; the HUD holds the announcements until it is dismissed.
            Teach();
            // Display scramble is gated on the inscription language only — the
            // other concept types don't affect text legibility.
            return LanguageText.RenderBbcode(inscription.LookUpText(), inscription.language, player);
        }, this);
    }

    public static KnowledgeSource Create(Sim sim, KnowledgeSourceSimState data)
    {
        var instance = data.Scene.Instantiate<KnowledgeSource>();
        data.SeatTransform(instance);
        instance._simState = data;
        sim.AddChild(instance);
        return instance;
    }
}
