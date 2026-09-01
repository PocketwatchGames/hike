using Godot;

// Flat ground decal that announces an incoming arced attack's landing point,
// then fades out and frees itself. Spawned by ItemEventHandlers when an arced
// projectile carrying a projectileTargetPreview launches (a mob's lobbed
// attack), parented to the Sim at the predicted landing point.
//
// The mark itself is a GroundDecal, which owns the stain-projector layer and
// the opacity plumbing; this node owns only the telegraph's lifetime.
//
// Purely presentational: it deals no damage and does no perception. The fade
// rides wall-clock _Process delta (like other fades) so slow-mo doesn't drag it.
[GlobalClass]
public partial class GroundDecalPreview : Node3D
{
    // Total seconds on the ground before it frees itself, including the fade
    // tail. Armed from the firing event's fuse via Initialize so the telegraph
    // lingers roughly as long as the lob is in flight.
    [Export] public float lifetimeSeconds = 0.8f;

    // Fraction of the lifetime spent fading out at the end. The decal holds full
    // strength until then, so the warning reads clearly before it dissolves.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fadeOutFraction = 0.45f;

    // The mark whose opacity is animated. Wired in the .tscn.
    [Export] private GroundDecal _decal;

    private float _age;

    // Called before AddChild to match the telegraph duration to the lob's fuse.
    // Non-positive values keep the scene-authored default.
    public void Initialize(float lifetime)
    {
        if (lifetime > 0f)
        {
            lifetimeSeconds = lifetime;
        }
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;
        if (_age >= lifetimeSeconds)
        {
            QueueFree();
            return;
        }
        if (_decal == null)
        {
            return;
        }
        float fadeStart = lifetimeSeconds * (1f - fadeOutFraction);
        if (_age <= fadeStart)
        {
            return;
        }
        float t = (_age - fadeStart) / Mathf.Max(1e-3f, lifetimeSeconds - fadeStart);
        _decal.SetOpacity(1f - Mathf.Clamp(t, 0f, 1f));
    }
}
