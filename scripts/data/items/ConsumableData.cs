using Godot;

// A carried consumable — a potion, food, a treasure map — used up from the
// hotbar. Its payload is a flat list of ItemEffects applied to the player on use
// (a health potion's heal, mud's camo, lantern oil's refill). Author
// self-contained buff/heal effects here (HealEffect, ApplyStatusEffect,
// RefillLanternOilEffect); effects that read runtime item state off the action
// context (SummonPetEffect) don't apply — those belong on a spell (SpellData /
// IUsableItem).
[GlobalClass]
public partial class ConsumableData : ItemData, IInstantUseItem
{
	// Applied in order to the player on pickup, via ItemEffect.Apply.
	[Export] public Godot.Collections.Array<ItemEffect> effects = new();

	// Optional one-shot fx spawned on the player as it's used (drink glug,
	// sparkle). Null = the generic Loot pickup poof is the only cue.
	[Export] public PackedScene useEffect;

	protected override EItemCategory ComputeCategory() => EItemCategory.Usable;

	public bool UseOn(Player player)
	{
		if (player == null)
		{
			return false;
		}
		// Self-targeted context — a consumable buffs/heals the user; there's no
		// weapon swing or interactive behind it.
		var context = new ActionContext { verb = EActionVerb.Use, target = player, worldPosition = player.GlobalPosition };
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
