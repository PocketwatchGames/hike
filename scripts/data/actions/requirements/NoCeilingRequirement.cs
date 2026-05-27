using Godot;

// Refuses the tier when the actor isn't under sufficient skylight — i.e.
// indoors, under an overhang, or in a cave entrance. Reads the voxel
// sunlight BFS the engine already maintains (no per-press raycast); the
// BFS attenuates as light spreads off the direct vertical column, so an
// open-sky voxel reads MAX_LIGHT while a cave one ledge in reads less.
// Sky-only verbs (bird's-eye overlook, future summons that need open air)
// put this on every tier so the runner's AnyTierCouldFire pass rejects
// the press at t=0 and plays the profile's rejectEffect.
[GlobalClass]
public partial class NoCeilingRequirement : ActionRequirement
{
	// Minimum fraction of MAX_LIGHT the sampled sunlight must meet for the
	// requirement to pass. 1.0 = unobstructed sky directly overhead (strict);
	// lower values let the verb fire one ledge-step into shade. The BFS uses
	// integer levels, so anything < 1/MAX_LIGHT effectively means "any
	// sunlight at all" (matches the rain-outdoor probe).
	[Export(PropertyHint.Range, "0,1,0.05")] public float minSkyLight = 1f;

	public override bool Evaluate(IActionActor actor, in ActionContext context)
	{
		WorldState ws = World.Current?.WorldState;
		if (ws == null)
		{
			// World not loaded — fail closed so the action doesn't fire from
			// a half-initialised state. Same conservative default as the
			// other requirement subclasses' null branches.
			return false;
		}
		Vector3 head = actor.ActorWorldPosition + Vector3.Up * GameCamera.EYE_HEIGHT;
		return ws.IsOutside(head, minSkyLight);
	}
}
