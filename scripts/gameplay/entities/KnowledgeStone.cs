using Godot;

// A stone inscribed in some language. Reading it grants every concept on
// `_concepts` AND shows the inscription via the same HUD panel as Signpost —
// the text reads as legible as the player's coverage of the inscription's
// language says.
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
    // Concepts granted on read, each concept's Teach() called in order.
    [Export] private Godot.Collections.Array<TeachableConcept> _concepts = new();
    // Looping holy ambience (sound + rising particle column) spawned as a child
    // when the stone enters the world; freed with the stone on chunk unload.
    [Export] private PackedScene _ambientLoopEffect;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    private KnowledgeStoneSimState _simState;
    // Set from the sim state; the scene carries no words of its own.
    private InscriptionData _inscription;

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

    public void Complete(InteractiveAction action)
    {
        GameClient gc = GameClient.Current;
        Hud hud = gc?.hud;
        if (hud == null)
        {
            return;
        }
        Player player = gc?.Player;
        hud.ShowReadable(() =>
        {
            // Learn first, then render, so a stone teaching its own tongue reads
            // legibly; the HUD holds the announcements until it is dismissed.
            if (player != null && _concepts != null)
            {
                int count = _concepts.Count;
                for (int i = 0; i < count; i++)
                {
                    _concepts[i]?.Teach(player);
                }
            }
            // Display scramble is gated on the inscription language only — the
            // other concept types don't affect text legibility. A stone teaching
            // only a recipe / region renders its inscription scrambled until
            // some other source teaches the inscription language.
            return _inscription == null ? string.Empty
                : LanguageText.RenderBbcode(_inscription.text, _inscription.language, player);
        }, this);
    }

    public static KnowledgeStone Create(Sim sim, KnowledgeStoneSimState data)
    {
        var instance = data.Scene.Instantiate<KnowledgeStone>();
        data.SeatTransform(instance);
        instance._simState = data;
        instance._inscription = data.Inscription;
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
