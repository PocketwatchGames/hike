using Godot;
using Godot.Collections;

// Single timeline event fired during an action's Charging or Active phase.
// Per-type fields are unioned on the resource — handlers switch on `type`
// and read only the fields relevant to that type. New types append fields
// rather than fork the resource so existing .tres files keep loading.
[GlobalClass]
public partial class ItemEvent : Resource
{
	[Export] public ushort time;
	[Export] public EItemEventType type;

	// Melee fields
	[Export] public float meleeRange = 1f;
	[Export] public float meleeRadius = 2f;

	// Hitscan fields
	[Export] public float hitScanRange = 20f;

	// ApplyEffect fields. Multi-effect so a single event can fire several
	// effects (heal + cleanse, light + buff). Each is applied to the actor.
	[Export] public Array<ItemEffect> effects = new();

	// ComboBonus fields. Fires `bonusEvents` when the action's combo index
	// is at or above `minComboIndex` (1 = second swing in chain, etc.).
	[Export] public int minComboIndex = 1;
	[Export] public Array<ItemEvent> bonusEvents = new();

	// PlayAnim / PlaySound fields. Routed through IActionActor.PlayAnim
	// and IActionActor.PlaySound respectively. animName uses the EAnimation
	// enum so the inspector shows a typo-proof dropdown — non-PlayAnim event
	// types ignore the field, so the default (Attack=0) is harmless on them.
	[Export] public EAnimation animName;
	[Export] public StringName soundName;

	// ToggleCarrierLight: no extra fields. Handler flips ConsumableState.isActive
	// on the action's primaryItem and attaches/detaches a CarrierLight.

	// OpenInteractive: no extra fields. Handler calls Complete() on
	// context.primaryInteractive.

	// ConsumeFromInventory: identifies which supporting item to consume.
	// `reagent` matches ItemData on supportingItems entries; `consumeAmount`
	// is the stack count to decrement (default 1). Stack→0 removes the item
	// from the player's inventory.
	[Export] public ItemData reagent;
	[Export] public int consumeAmount = 1;

	// Optional per-event damage override for Melee / Hitscan. When set, the
	// combat handler uses this DamageData; otherwise it falls back to the
	// driving weapon's damageData (`primaryItem as WeaponState).data.damageData`).
	// Mob attacks set this directly on the event since mobs aren't backed by
	// a WeaponState.
	[Export] public DamageData damageData;
}
