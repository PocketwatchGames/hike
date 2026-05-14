using Godot;
using Godot.Collections;

// Single timeline event fired during an action's Charging or Active phase.
// `type` is a bitmask — a single event can fire several behaviors at once
// (e.g. ApplyEffect | DecrementStack on a healing potion's release tick).
// Per-flag fields are unioned on the resource — handlers test each flag and
// read only the fields relevant to that flag. New flags append bits rather
// than fork the resource so existing .tres files keep loading.
//
// The inspector hides fields whose owning flag isn't selected (see
// `_ValidateProperty`). Storage is preserved while hidden, so toggling a
// flag off and back on doesn't lose previously-authored values. `[Tool]`
// is required for `_ValidateProperty` to fire in the editor.
[Tool]
[GlobalClass]
public partial class ItemEvent : Resource
{
	[Export] public ushort time;

	private EItemEventType _type;
	[Export, CompactFlags] public EItemEventType type
	{
		get => _type;
		set
		{
			if (_type == value) { return; }
			_type = value;
			// Defer the property-list rebuild to idle so a custom editor that
			// triggered this set (e.g. addons/data_ed/FlagsPropertyEditor) isn't
			// torn down mid-callback. Without the defer, toggling a flag from
			// the dropdown can leave the menu dispatching to a destroyed editor
			// and cause neighbouring flags to flip.
			CallDeferred(MethodName.NotifyPropertyListChanged);
			// EmitChanged so an inline sub-resource view (the common case for
			// ItemEvent inside an ItemAction's events array) re-renders too.
			EmitChanged();
		}
	}

	// Melee fields
	[Export] public float meleeRange = 1f;
	[Export] public float meleeRadius = 2f;

	// Hitscan fields
	[Export] public float hitScanRange = 20f;

	// ApplyEffect fields. Multi-effect so a single event can fire several
	// effects (heal + cleanse, light + buff). Each is applied to the actor.
	[Export] public Array<ItemEffect> effects = new();

	// PlayAnim fields. Routed through IActionActor.PlayAnim
	// animName uses the EAnimation
	// enum so the inspector shows a typo-proof dropdown — non-PlayAnim event
	// types ignore the field, so the default (Attack=0) is harmless on them.
	[Export] public EAnimation animName;

	// ToggleMovingLight: no extra fields. Handler flips ConsumableState.isActive
	// on the action's primaryItem and attaches/detaches a MovingLight.

	// OpenInteractive: handler calls Complete() on context.primaryInteractive
	// and (if `fx` is non-null) spawns a one-shot at the interactive's node
	// position. The fx field is the per-event audiovisual signature — the
	// "the chest creaks open" cue lives on the OpenInteractive event in the
	// chest's action, not on the chest's C# class, so each interactive's
	// authored action carries its own completion effect.
	[Export] public PackedScene fx;

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

	// ApplyMotion fields. Speed in m/s and duration in seconds describe the
	// motion phase the actor should enter; the actor resolves direction
	// (input/facing/etc) and any per-actor scaling (e.g. swim speed). When
	// freezeGravity is true, the actor zeros vertical velocity and suppresses
	// gravity for the duration — the dash hang. Sword-lunge style events
	// leave it false so gravity still applies.
	[Export] public float motionSpeed = 30f;
	[Export] public float motionDuration = 0.2f;
	[Export] public bool motionFreezeGravity = true;

	// LearnLanguage fields. `language` is the LanguageData to add to the
	// learner's known set. `firstLearnEffect` plays on the actor only the
	// first time the language is learned (Player.LearnLanguage returns true);
	// a re-trigger on an already-known language is silent.
	[Export] public LanguageData language;
	[Export] public PackedScene firstLearnEffect;

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

	public override void _ValidateProperty(Dictionary property)
	{
		string name = property["name"].AsString();
		EItemEventType requiredFlags = GetRequiredFlags(name);
		if (requiredFlags == 0) { return; }
		if ((_type & requiredFlags) != 0) { return; }
		// Mask out the Editor bit so the field is hidden from the inspector
		// when its owning flag isn't selected. Storage is preserved, so a
		// previously-authored value comes back when the flag is re-enabled.
		PropertyUsageFlags usage = property["usage"].As<PropertyUsageFlags>() & ~PropertyUsageFlags.Editor;
		property["usage"] = (int)usage;
	}

	private static EItemEventType GetRequiredFlags(string fieldName)
	{
		return fieldName switch
		{
			nameof(meleeRange) or nameof(meleeRadius) => EItemEventType.Melee,
			nameof(hitScanRange) => EItemEventType.Hitscan,
			nameof(effects) => EItemEventType.ApplyStatusEffect,
			nameof(animName) => EItemEventType.PlayAnim,
			nameof(fx) => EItemEventType.OpenInteractive,
			nameof(reagent) or nameof(consumeAmount) => EItemEventType.ConsumeFromInventory,
			nameof(motionSpeed) or nameof(motionDuration) or nameof(motionFreezeGravity) => EItemEventType.ApplyMotion,
			nameof(language) or nameof(firstLearnEffect) => EItemEventType.LearnLanguage,
			nameof(damageData)
				or nameof(impactMissEffect)
				or nameof(impactEnvironmentEffect)
				or nameof(impactHealthEffect)
				or nameof(impactArmorEffect)
				or nameof(impactLethalEffect) => EItemEventType.Melee | EItemEventType.Hitscan,
			_ => 0,
		};
	}
}
