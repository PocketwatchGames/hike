using Godot;

// Gates a tier on the actor's physical state. Each predicate is a simple
// flag — set the one(s) the tier needs and leave the rest unchecked.
// `require*` and `forbid*` on the same predicate are author error (the
// requirement just fails); the runner doesn't validate combinations.
//
// Common patterns:
//   forbidSwimming = true    — club / two-handed melee that needs footing
//   requireAirborne = true   — ground-slam variant that only fires mid-air
//   requireGrounded = true   — heavy windup that can't start while falling
[GlobalClass]
public partial class ActorStateRequirement : ActionRequirement
{
	[Export] public bool requireSwimming;
	[Export] public bool forbidSwimming;
	[Export] public bool requireGrounded;
	[Export] public bool requireAirborne;

	public override bool Evaluate(IActionActor actor, in ActionContext context)
	{
		bool swimming = actor.IsSwimming;
		if (requireSwimming && !swimming) { return false; }
		if (forbidSwimming && swimming) { return false; }
		bool grounded = actor.IsGrounded;
		if (requireGrounded && !grounded) { return false; }
		if (requireAirborne && grounded) { return false; }
		return true;
	}
}
