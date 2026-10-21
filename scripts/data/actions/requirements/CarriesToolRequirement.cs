using Godot;

// Passes while the actor carries a sound tool of `kind` (Inventory.FindTool).
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class CarriesToolRequirement : ActionRequirement
{
	[Export] public EToolKind kind = EToolKind.None;

	public override bool Evaluate(IActionActor actor, in ActionContext context)
	{
		return actor is Player player && player.Inventory?.FindTool(kind, out _) != null;
	}
}
