using Godot;

// Polymorphic effect applied by an ApplyEffect event. Subclasses define what
// "apply" means (heal, buff, light a fire). Resource-based so effects compose
// via .tres authoring; same pattern as BehaviorTransitionData / ActionRequirement.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class ItemEffect : Resource
{
	public virtual void Apply(IActionActor actor, in ActionContext context)
	{
		GD.PushError($"ItemEffect subclass {GetType().Name} must override Apply.");
	}

	// Apply `effects` in order to `player` as their own use — something eaten or
	// drunk, with no weapon swing or interactive behind it.
	public static void ApplyToSelf(Player player, Godot.Collections.Array<ItemEffect> effects)
	{
		if (player == null || effects == null)
		{
			return;
		}
		var context = new ActionContext { verb = EActionVerb.Use, target = player, worldPosition = player.GlobalPosition };
		int count = effects.Count;
		for (int i = 0; i < count; i++)
		{
			effects[i]?.Apply(player, context);
		}
	}
}
