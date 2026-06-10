using Godot;
using Godot.Collections;

[GlobalClass]
public partial class ItemCount : Resource
{
	[Export] public ItemData item;
	[Export] public int count = 1;

	// Status effects this dropped item can bestow when used, composed onto the
	// spawned ItemState (not the shared ItemData) so the menu of possibilities
	// travels per-instance with the pickup. A fairy corpse authors its candidate
	// boons here; on use the consumable applies one of them (eventually the
	// player's pick). Empty for ordinary loot, whose use-effects are baked into
	// its own action events.
	[Export] public Array<StatusEffectData> possibleStatusEffects = new();
}
