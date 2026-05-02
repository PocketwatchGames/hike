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

	// Per-event impact one-shots spawned by the Melee/Hitscan handlers based
	// on what the swing/ray hit. Authored on the event so a single weapon can
	// give light vs heavy attacks distinct impact signatures, and so mob
	// attacks (which don't have a WeaponState) can still pick their own.
	// Any field may be null — missing keys silently emit nothing.
	[Export] public PackedScene impactMissEffect;
	[Export] public PackedScene impactEnvironmentEffect;
	[Export] public PackedScene impactHealthEffect;
	[Export] public PackedScene impactArmorEffect;
	[Export] public PackedScene impactLethalEffect;
}
