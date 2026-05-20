using Godot;

// Sends the actor back to the world's authored spawn point. Used by the
// ruby slippers — the slippers stay in inventory (no DecrementStack on
// the firing event) so the same pair can be re-used forever.
[GlobalClass]
public partial class TeleportToSpawnEffect : ItemEffect
{
	[Export] public PackedScene effectScene;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is not Player player)
		{
			return;
		}
		WorldState worldState = player.World?.WorldState;
		if (worldState == null)
		{
			return;
		}
		player.TeleportTo(worldState.Spawn);
		if (effectScene != null)
		{
			ItemEventHandlers.SpawnOnActor(actor, effectScene);
		}
	}
}
