using Godot;

// Completion effect for the Pray self-action: sends the player home to their last
// campfire, passes the night, and wakes them into the camp screen — without banking
// the field knowledge/items they gathered (the trade-off for the free trip home).
// Fires from an ApplyStatusEffect event on the action's completionEvents (the same
// path the Ruby Slippers' TeleportToSpawnEffect uses), so it runs only on a natural,
// fully-faded completion. All the state change reuses existing camp code — see
// GameClient.PrayReturnHome.
[GlobalClass]
public partial class PrayReturnHomeEffect : ItemEffect
{
	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is not Player)
		{
			return;
		}
		GameClient.Current?.PrayReturnHome();
	}
}
