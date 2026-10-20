using Godot;

// Fills one of the player's survival meters (Player.RestoreNeed) — a well's
// drink. Food restores hunger through ConsumableData.satiation instead, so a
// meal's filling is a stat on the item rather than an effect buried in a list.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class RestoreNeedsEffect : ItemEffect
{
	[Export] public ENeed need = ENeed.Thirst;

	// Fraction of the full meter restored; 1 fills it from empty.
	[Export(PropertyHint.Range, "0,1,0.01")] public float amount = 1f;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			player.RestoreNeed(need, amount);
		}
	}
}
