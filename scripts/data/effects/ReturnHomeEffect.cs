using Godot;

// Sends the party home to their last campfire, banks what they carried there as a
// camp would, passes the night, and wakes them into the camp screen. The Ruby
// Rosaries' payload. See GameClient.ReturnHome.
[GlobalClass]
public partial class ReturnHomeEffect : ItemEffect
{
	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is not Player)
		{
			return;
		}
		GameClient.Current?.ReturnHome();
	}
}
