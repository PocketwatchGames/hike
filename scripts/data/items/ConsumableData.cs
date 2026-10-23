using Godot;

// A carried consumable — a potion, food, a treasure map — used up from the
// hotbar. Its payload is a flat list of ItemEffects applied to the player on use
// (a health potion's heal, mud's camo, lantern oil's refill). Author
// self-contained buff/heal effects here (HealEffect, ApplyStatusEffect,
// RefillLanternOilEffect); effects that read runtime item state off the action
// context (SummonPetEffect) run from the actionProfile timeline instead.
//
// A meal (meal_*) is a consumable with a cooking recipe (ItemData.recipe) whose
// effect is an EEffectCategory.Meal status, so eating it
// replaces whatever meal the character last ate.
//
// With an actionProfile the consumable takes time: the hotbar runs that timeline
// (a potion's drinking pose) and its UseConsumable event applies the
// payload. Without one it is spent the instant it's used.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class ConsumableData : ItemData, IInstantUseItem, IUsableItem
{
	// Applied in order to the player on use, via ItemEffect.Apply.
	[Export] public Godot.Collections.Array<ItemEffect> effects = new();

	// Optional one-shot fx spawned on the player as the payload lands (the
	// sparkle when a potion finishes). Null = no extra cue.
	[Export] public PackedScene useEffect;

	// Use timeline. Null = instant use; set = used only from the hotbar, where
	// the press / hold drives it.
	[Export] public ItemActionProfile actionProfile;
	public ItemActionProfile ActionProfile => actionProfile;
	public bool CanUseInstantly => actionProfile == null;

	// Stages of hunger eating this takes away (one stage is
	// PlayerData.hungerHoursPerStage); 0 = not food. Raw
	// forage and meat set it too, authored with categoryOverride = Material so
	// they stay backpack ingredients that can also be eaten.
	[Export(PropertyHint.Range, "0,1,0.01")] public float satiation;

	protected override EItemCategory ComputeCategory() => EItemCategory.Usable;

	public bool UseOn(Player player)
	{
		if (player == null || !CanUseInstantly)
		{
			return false;
		}
		ApplyTo(player);
		return true;
	}

	// The payload, however the use was reached — instantly, or by a timed
	// consumable's UseConsumable event.
	public void ApplyTo(Player player)
	{
		if (player == null)
		{
			return;
		}
		player.RestoreNeed(ENeed.Hunger, satiation);
		ItemEffect.ApplyToSelf(player, effects);
		if (useEffect != null)
		{
			Fx.Create(useEffect, player, Vector3.Zero);
		}
	}
}
