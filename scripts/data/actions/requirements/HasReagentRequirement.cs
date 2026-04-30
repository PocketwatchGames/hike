using Godot;

// Requires that the action's supportingItems include at least one ItemState
// of the named ItemData with sufficient stack. Used for "lockpick a chest"
// (reagent = lockpick) and "cook with these ingredients" (reagent = onion).
[GlobalClass]
public partial class HasReagentRequirement : ActionRequirement
{
	[Export] public ItemData reagent;
	[Export] public int amount = 1;

	public override bool Evaluate(IActionActor actor, in ActionContext context)
	{
		if (reagent == null || context.supportingItems == null)
		{
			return false;
		}
		int total = 0;
		foreach (ItemState item in context.supportingItems)
		{
			if (item != null && item.data == reagent)
			{
				total += item.stackCount;
				if (total >= amount)
				{
					return true;
				}
			}
		}
		return false;
	}
}
