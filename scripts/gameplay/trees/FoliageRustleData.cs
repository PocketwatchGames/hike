using Godot;

// How a plant answers being pushed through: a damped spring that leans the
// canopy away from the walker and wobbles back, plus a per-card shiver and a
// rustle cue. Shared by every bush scene so the feel is tuned in one place.
[GlobalClass]
public partial class FoliageRustleData : Resource
{
    // Lean at the canopy top (m) when pushed at fullSpeed or faster.
    [Export] public float maxLean = 0.18f;

    // Closing speed (m/s) into the plant that gives the full response; slower
    // contact scales lean, flutter and cue down linearly.
    [Export] public float fullSpeed = 4f;

    // Spring stiffness (1/s²) and damping (1/s). sqrt(stiffness) / TAU is the
    // wobble frequency in Hz; damping below 2*sqrt(stiffness) overshoots.
    [Export] public float stiffness = 70f;
    [Export] public float damping = 7f;

    // Per-card shiver amplitude (m) at full contact, its frequency (Hz), and
    // how long (s) it takes to die out once contact ends.
    [Export] public float flutterAmplitude = 0.035f;
    [Export] public float flutterHz = 7f;
    [Export] public float flutterDecaySeconds = 0.5f;

    // Contact is reported per physics tick; it counts as held this long (s)
    // after the last report so render frames between ticks don't drop it.
    [Export] public float contactHoldSeconds = 0.1f;

    // A strike (swing, shot, blast) is a kick rather than a held push: it sets
    // the lean moving at this speed (m/s) down the hit and shivers at full
    // flutter. Peak lean is roughly hitKick / sqrt(stiffness).
    [Export] public float hitKick = 2.5f;

    // Rustle sound / leaf burst at the contact point. Plays on first contact
    // and then at most once per cueCooldownSeconds while it continues.
    [Export] public PackedScene cueFx;
    [Export] public float cueCooldownSeconds = 0.7f;
    // Fraction of fullSpeed below which contact makes no sound (leaning on it).
    [Export(PropertyHint.Range, "0,1,0.01")] public float cueMinSpeed01 = 0.25f;
}
