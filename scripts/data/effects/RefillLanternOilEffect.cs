using Godot;

// Tops up the player's lantern oil — the payload of the Lantern Oil consumable,
// an oil droplet and an oil fountain. Fills the equipped lantern first, then the
// carried oil flasks (Player.AddLanternOil).
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class RefillLanternOilEffect : ItemEffect
{
	// Oil restored per use (a lantern holds 100). Ignored when fillCompletely.
	[Export(PropertyHint.Range, "0,1000,1")] public float amount = 50f;

	// Fill the lantern and every oil flask to the brim (an oil fountain).
	[Export] public bool fillCompletely;

	// Optional one-shot fx spawned on the player (a refuel cue).
	[Export] public PackedScene effectScene;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (actor is Player player)
		{
			if (fillCompletely)
			{
				player.RefillAllOil();
			}
			else
			{
				player.AddLanternOil(amount);
			}
		}
		if (effectScene != null)
		{
			ItemEventHandlers.SpawnOnActor(actor, effectScene);
		}
	}
}
