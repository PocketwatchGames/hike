using Godot;

// Tuning for BehaviorWanderFollow: a companion dog that orbit-wanders around
// the player. It picks a point within wanderRadius of the player, trots there,
// sniffs for a beat, then re-picks around the player's updated position. When
// the player holds still the dog pads over to a spot close beside them and
// lies down (idle), and only gets back up once the player has wandered farther
// than getUpRadius away.
[GlobalClass]
public partial class WanderFollowBehaviorData : BehaviorData
{
    // Radius around the player within which wander destinations are picked.
    [Export] public float wanderRadius = 8.0f;

    // Lower bound on a wander leg so each pick actually moves the dog rather
    // than landing back under its own feet.
    [Export] public float minWanderDistance = 3.0f;

    // How long the dog pauses to "sniff" at each wander point, in seconds — a
    // random value in [X, Y] is rolled per stop.
    [Export] public Vector2 sniffTimeRange = new Vector2(1f, 3f);

    // Normalized move speed (fraction of MobData.maxSpeed) for both wandering
    // and padding over to rest — an ambling trot, not a full chase.
    [Export(PropertyHint.Range, "0,1,0.01")] public float moveSpeed = 0.5f;

    // When the player has stopped, the dog settles at a point within this
    // distance of them before lying down.
    [Export] public float restApproachRadius = 2.5f;

    // Closest the rest spot may be picked to the player, so the dog doesn't try
    // to lie down on top of them.
    [Export] public float restMinDistance = 1.0f;

    // Once lying down, the dog only gets up when the player has moved farther
    // than this from the dog. Kept larger than restApproachRadius so a few idle
    // steps from the player don't disturb a resting dog.
    [Export] public float getUpRadius = 6.0f;

    // If the player gets farther than this from the dog, the dog abandons its
    // current wander / sniff / rest and beelines to the player, only resuming
    // its wander once back within wanderRadius (the hysteresis release). Kept
    // larger than wanderRadius so reaching the far edge of a normal wander leg
    // doesn't trip it.
    [Export] public float catchUpRadius = 12.0f;

    // Normalized move speed (fraction of MobData.maxSpeed) while catching up —
    // faster than the amble so the dog can actually close on a moving player.
    [Export(PropertyHint.Range, "0,1,0.01")] public float catchUpSpeed = 1.0f;

    // The player counts as "moving" until they've stayed within this radius of
    // a fixed anchor for stopGraceSeconds — debounces tiny position jitter.
    [Export] public float playerStillRadius = 0.4f;

    // How long the player must hold still (within playerStillRadius) before the
    // dog treats them as stopped and heads in to rest.
    [Export] public float stopGraceSeconds = 0.75f;

    // Path-success radius handed to the navigator for wander / rest goals.
    [Export] public float arrivalDistance = 0.6f;

    public override BehaviorBase CreateRuntime() => new BehaviorWanderFollow(this);
}
