using Godot;

// A short sideways/backward dash to slip out of an incoming projectile's path.
// Entered from the attack/encircle state via IncomingProjectileCondition; runs
// the dash, then hands control back to `resumeBehavior`. Tuning only — the dash
// physics live in Mob.ApplyDodge and the direction choice in BehaviorDodge.
[GlobalClass]
public partial class DodgeBehaviorData : BehaviorData
{
    // Entered mid-combat from the attack/encircle state, resumes the attack —
    // still an engaged posture, so it counts as danger while dodging a volley.
    public DodgeBehaviorData() { behaviorFlags = EBehaviorFlags.Engaging; }

    // Horizontal distance (meters) the dash carries the mob. Speed is derived
    // (dashDistance / dashDurationSeconds) so this stays the authored knob.
    [Export] public float dashDistance = 3f;
    // How long the dash impulse lasts. Shorter = snappier/faster slide over the
    // same distance.
    [Export] public float dashDurationSeconds = 0.2f;
    // Minimum gap between dodges (seconds). Written to Mob.ReactionReadyMs on
    // dodge so IncomingProjectileCondition can't re-trigger every tick while a
    // volley keeps arriving.
    [Export] public float reactionCooldownSeconds = 1.5f;
    // Look-ahead used to re-find the incoming shot when picking which way to
    // dodge, so the chosen direction clears the shot's actual path.
    [Export] public float threatLeadTime = 1f;
    // Behavior to resume when the dash finishes — normally the attack state the
    // dodge interrupted.
    [Export] public StringName resumeBehavior = "Attack";

    public override BehaviorBase CreateRuntime() => new BehaviorDodge(this);
}
