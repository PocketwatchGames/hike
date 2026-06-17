using Godot;

// Escape despawn: the mob detaches from physics and combat, rises a fixed
// distance into the sky while its visual fades, then removes itself
// permanently from the world (active node AND persistent sim state, so it
// never respawns). Triggered by BehaviorFairyEscape once a fairy has fled far
// enough from the player. Kept generic — any mob that should exit by rising
// and fading rather than dying can use it: wire a node implementing IVanishFade
// into _vanishFade for the fade, or leave it null for a silent rise.
public partial class Mob
{
    // Optional visual driven during the vanish (the fairy orb). Cast to
    // IVanishFade each tick; null on mobs that never vanish.
    [Export] private Node3D _vanishFade;

    private bool _vanishing;
    private float _vanishElapsed;
    private float _vanishDuration;
    private float _vanishAscent;
    private Vector3 _vanishStartPosition;

    public bool IsVanishing => _vanishing;

    // Begin the escape ascent. ascentHeight = metres risen; durationSeconds =
    // time to rise and fully fade. Idempotent — a second call while already
    // vanishing is ignored.
    public void BeginVanish(float ascentHeight, float durationSeconds)
    {
        if (_vanishing)
        {
            return;
        }
        _vanishing = true;
        _vanishElapsed = 0f;
        _vanishAscent = ascentHeight;
        _vanishDuration = Mathf.Max(0.01f, durationSeconds);
        _vanishStartPosition = GlobalPosition;

        // Detach from physics and all targeting/collision so the rise is a
        // clean scripted motion and nothing can hit it or path against it.
        Freeze = true;
        CollisionLayer = 0;
        CollisionMask = 0;
        if (_hurtBox != null)
        {
            _hurtBox.CollisionLayer = 0;
        }
    }

    // Immediately and permanently remove this mob — active node AND persistent
    // sim state, so it never respawns — with no death cascade (no loot, kill
    // credit, or death fx). The silent counterpart to Die(). Used to destroy
    // summoned minions when the summoner recycles them past its cap or the
    // weapon is unequipped/removed. Safe to call more than once; QueueFree
    // guards the node.
    public void Despawn()
    {
        _world?.RemoveEntity(this);
        _world?.WorldState?.RemoveEntity(_simState);
        QueueFree();
    }

    // Advance the vanish by one physics step. Returns true while vanishing so
    // the caller short-circuits the normal AI / locomotion path. On completion
    // the mob is dropped from the active scene AND from persistent sim state,
    // then freed — an escaped fairy is gone for good, not a corpse and not a
    // respawn candidate.
    private bool TickVanish(float delta)
    {
        if (!_vanishing)
        {
            return false;
        }
        _vanishElapsed += delta;
        float t = Mathf.Clamp(_vanishElapsed / _vanishDuration, 0f, 1f);
        // Ease-out rise: fast off the ground, settling as it fades to nothing.
        float rise = Mathf.Sin(t * Mathf.Pi * 0.5f);
        GlobalPosition = _vanishStartPosition + Vector3.Up * (_vanishAscent * rise);
        (_vanishFade as IVanishFade)?.SetFade(1f - t);

        if (t >= 1f)
        {
            _world?.RemoveEntity(this);
            _world?.WorldState?.RemoveEntity(_simState);
            QueueFree();
        }
        return true;
    }
}
