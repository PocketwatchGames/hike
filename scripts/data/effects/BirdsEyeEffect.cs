using Godot;

// Lifts the camera into an overview shot when the actor uses the bird's-eye
// item. Mutation is delegated to Player.BeginBirdsEye — the effect just bridges
// the action timeline to the player-side state. GameClient subscribes to
// Player.onBirdsEye and drives the camera fly-up, motion blur, and return.
[GlobalClass]
public partial class BirdsEyeEffect : ItemEffect
{
	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			player.BeginBirdsEye();
		}
	}
}
