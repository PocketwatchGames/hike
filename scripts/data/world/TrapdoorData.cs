using Godot;

// Tuning for a TrapdoorPanel — the hinged floor leaf shared by the
// player-operated trapdoor, the perception-gated drop trap, and the crumbling
// floor. Authored inline on each trapdoor scene (like SpikeTrapData) so the
// scene composition owns the feel.
[GlobalClass]
public partial class TrapdoorData : Resource
{
    // Leaf swing about its hinge, degrees. Negative drops the free edge down
    // through the floor (the scene authors the hinge so this reads as "open").
    [Export(PropertyHint.Range, "-180,180,1")] public float openAngleDeg = -92f;

    // Seconds the leaf takes to swing open / fall.
    [Export] public float openSeconds = 0.18f;

    // Seconds the leaf takes to swing back shut.
    [Export] public float closeSeconds = 0.35f;

    // Seconds between a body-trigger and the leaf dropping — the telegraph
    // window (crack fx plays at its start). 0 drops instantly. Only consulted
    // on the triggered (trap) path; the manual interact/lever path is immediate.
    [Export] public float warningSeconds = 0.35f;

    // Seconds the leaf stays open after a trap trigger before swinging shut and
    // re-arming. 0 (the default) leaves it open permanently — a crumbling floor
    // never comes back.
    [Export] public float autoCloseSeconds = 0f;

    // Optional hit dealt to every body still standing on the leaf the moment it
    // drops (a lip of jagged edges, a portcullis slam). Null = the fall itself
    // is the only consequence, and whatever the author built in the pit below.
    [Export] public DamageData dropDamage;

    // Discoverable.prominence while armed and hidden vs. once sprung, mirroring
    // SpikeTrapData. Pushed onto the host Discoverable by TrapdoorPanel at spawn
    // so the placement owns them. Only used when a Discoverable is wired.
    [Export] public float armedProminence = 0.55f;
    [Export] public float firedProminence = 2f;

    // One-shot fx scenes, wired in the .tscn; any may be null.
    [Export] public PackedScene warningEffect;
    [Export] public PackedScene openEffect;
    [Export] public PackedScene closeEffect;
}
