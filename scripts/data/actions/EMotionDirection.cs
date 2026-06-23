// How an ApplyMotion event resolves the axis its forwardSpeed drives along.
// Authored per-ItemEvent so a weapon lunge and a dash can share the event
// shape but pull their direction from different sources. The actor's physics
// layer reads this and resolves the concrete world vector (see
// IActionActor.ApplyMotion). Facing is value 0 so events that don't author
// the field default to facing-relative motion (the common case — attack
// lunges, hop-backs); only the dash opts into Movement.
public enum EMotionDirection
{
	// Drive along the actor's current facing (body yaw), ignoring move input.
	// Correct for weapon lunges / recoils: the strike commits to where the
	// actor is aiming, not where the stick happens to be pushed.
	Facing,
	// Drive along the actor's active move input, falling back to facing when
	// there's none. Lets a dash go sideways / backward independent of facing.
	Movement,
}
