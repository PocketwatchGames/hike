using Godot;

// Refuses the tier when the actor isn't under sufficient open sky — i.e.
// indoors, under an overhang, or in a cave entrance. Reads the non-leaky
// vertical SkyExposure field via WorldState.IsOutside (no per-press raycast);
// because it's the column-scan value, NOT the BFS spread, a cave mouth's
// horizontal light leak doesn't falsely satisfy the requirement — only
// genuine open sky overhead does. Sky-only verbs (bird's-eye overlook, future
// summons that need open air) put this on every tier so the runner's
// AnyTierCouldFire pass rejects the press at t=0 and plays the rejectEffect.
[GlobalClass]
public partial class NoCeilingRequirement : ActionRequirement
{
	// Minimum fraction of MAX_LIGHT of vertical sky exposure the sample must
	// meet to pass. 1.0 = unobstructed sky directly overhead (strict); lower
	// values let the verb fire under a partial canopy. SkyExposure uses integer
	// levels, so anything < 1/MAX_LIGHT effectively means "any open sky at all"
	// (matches the rain-outdoor probe).
	[Export(PropertyHint.Range, "0,1,0.05")] public float minSkyLight = 1f;

	public override bool Evaluate(IActionActor actor, in ActionContext context)
	{
		WorldState ws = Sim.Current?.WorldState;
		if (ws == null)
		{
			// Sim not loaded — fail closed so the action doesn't fire from
			// a half-initialised state. Same conservative default as the
			// other requirement subclasses' null branches.
			return false;
		}
		Vector3 head = actor.ActorWorldPosition + Vector3.Up * GameCamera.EYE_HEIGHT;
		return ws.IsOutside(head, minSkyLight);
	}
}
