using Godot;

// Polymorphic effect applied by an ApplyEffect event. Subclasses define what
// "apply" means (heal, buff, light a fire). Resource-based so effects compose
// via .tres authoring; same pattern as BehaviorTransitionData / ActionRequirement.
[GlobalClass]
public partial class ItemEffect : Resource
{
	public virtual void Apply(IActionActor actor, in ActionContext context)
	{
		GD.PushError($"ItemEffect subclass {GetType().Name} must override Apply.");
	}
}
