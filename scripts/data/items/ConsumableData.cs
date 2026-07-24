using Godot;

// A found consumable — used up on the spot when picked up out of the world (a
// chest drop, ground loot) rather than carried in the pack. Its payload is a
// flat list of ItemEffects applied to the player at pickup (a health potion's
// heal, mud's camo, lantern oil's refill). Author self-contained buff/heal
// effects here (HealEffect, ApplyStatusEffect, RefillLanternOilEffect); effects
// that read runtime item state off the action context (SummonPetEffect) don't
// apply — those belong on a spell (SpellData / IUsableItem). This is the
// pickup-loot kind of item; SpellData and LanternData are the equipped
// action-item kinds and share nothing with it beyond ItemData.
[GlobalClass]
public partial class ConsumableData : ItemData, IApplyOnPickup
{
	// Applied in order to the player on pickup, via ItemEffect.Apply.
	[Export] public Godot.Collections.Array<ItemEffect> effects = new();

	// Optional one-shot fx spawned on the player as it's used (drink glug,
	// sparkle). Null = the generic Loot pickup poof is the only cue.
	[Export] public PackedScene useEffect;

	// Equipment (not Material) so field pickup requires an interact instead of
	// auto-grabbing on contact — using it is a deliberate action.
	protected override EItemCategory ComputeCategory() => EItemCategory.Equipment;

	public bool ApplyOnPickup(Player player)
	{
		if (player == null)
		{
			return false;
		}
		// Self-targeted context — a found consumable buffs/heals the taker; there's
		// no weapon swing or interactive behind it.
		var context = new ActionContext { verb = EActionVerb.Use, target = player };
		if (effects != null)
		{
			for (int i = 0; i < effects.Count; i++)
			{
				effects[i]?.Apply(player, context);
			}
		}
		if (useEffect != null)
		{
			Fx.Create(useEffect, player, Vector3.Zero);
		}
		return true;
	}
}
