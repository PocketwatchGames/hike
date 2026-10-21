using Godot;

// Fills one of the player's survival meters (Player.RestoreNeed) — a well's
// drink, which also refills every water flask. Food restores hunger through
// ConsumableData.satiation instead, so a meal's filling is a stat on the item
// rather than an effect buried in a list.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class RestoreNeedsEffect : ItemEffect
{
	[Export] public ENeed need = ENeed.Thirst;

	// Stages of deprivation taken away, unless restoresFully clears it all.
	[Export(PropertyHint.Range, "0,3,0.05,or_greater")] public float amount = 1f;
	[Export] public bool restoresFully;

	// Thirst only: also fill every carried water flask (a well).
	[Export] public bool refillsWaterFlasks;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			if (restoresFully)
			{
				player.RestoreNeedFully(need);
			}
			else
			{
				player.RestoreNeed(need, amount);
			}
			if (refillsWaterFlasks && need == ENeed.Thirst)
			{
				player.RefillWaterFlasks();
			}
		}
	}
}
