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

	// LearnLanguage fields. `language` is the LanguageData to grant on.
	// `languageComponents` is the bitset of pieces (Grammar / Numbers /
	// Glyphs / Spelling) this event teaches — default All matches the
	// pre-component "whole language" behavior so existing consumables/
	// dialogue events keep working. `firstLearnEffect` plays on the actor
	// only when this firing actually adds at least one new component to
	// the player's learned-set; re-triggers on a fully-known language are
	// silent.
	[Export] public LanguageData language;
	[Export, CompactFlags] public ELanguageComponents languageComponents = ELanguageComponents.All;
	[Export] public PackedScene firstLearnEffect;

	// LearnConcept field. Polymorphic resource ref — any TeachableConcept
	// subclass (LanguageTeachable, RecipeTeachable, RegionTeachable, ...).
	// Shares `firstLearnEffect` with the LearnLanguage path: handler plays
	// it on the actor when concept.Teach returns true (newly added).
	[Export] public TeachableConcept concept;

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

	// SpawnAreaEffect: Node3D scene spawned at the actor's aim point (or
	// position when no aim cursor is active). Authored area-of-effect scenes
	// carry their own DamageZone + particle loop and own their lifetime —
	// see scenes/projectiles/rain_of_arrows_aoe.tscn for the canonical use.
	[Export] public PackedScene areaEffectScene;

	// Projectile fields. Spawned by DoProjectile at the actor's position,
	// flying along the actor's forward (with the tier's accuracy spread
	// applied via accuracySpread01). Damage on impact comes from the
	// event's damageData (or the firing weapon's damageData if null).
	// Authored inline rather than via a ProjectileData sub-resource because
	// brand-new [GlobalClass] C# Resources don't reliably bind to typed
	// [Export] slots in Godot 4.6 — the same fields a sibling sub-resource
	// would carry just live on the event itself, matching how `meleeRange`
	// / `hitScanRange` are authored.
	[Export] public PackedScene projectileScene;
	[Export] public float projectileSpeed = 25f;
	// Hard cap on flight time before the projectile despawns; the reticle
	// derives effective range as projectileSpeed * projectileLifetimeSeconds.
	// When projectileArcing is true, this is the EXACT flight time — the
	// projectile despawns at the cursor at exactly this many seconds after
	// launch, and projectileSpeed is ignored (velocity is solved for).
	[Export] public float projectileLifetimeSeconds = 1f;
	// Optional looping audio-visual cue parented to the projectile for the
	// duration of its flight (fire trail, shockwave, magic glow).
	[Export] public PackedScene projectileLoopEffect;
	// Arcing projectile: flies on a ballistic arc that lands at the player's
	// positional aim cursor after exactly projectileLifetimeSeconds, with NO
	// in-flight collision (passes through walls and mobs). On despawn, fires
	// `impactEvent` at the landing position. Use for delivery-style attacks
	// (rain of arrows, thrown explosive, smoke bottle). Requires the firing
	// tier to use Positional aim — without an aim cursor the projectile won't
	// spawn. projectileSpeed is ignored; the handler solves for initial
	// velocity from origin, cursor, gravity, and lifetime.
	private bool _projectileArcing;
	[Export] public bool projectileArcing
	{
		get => _projectileArcing;
		set
		{
			if (_projectileArcing == value) { return; }
			_projectileArcing = value;
			// Same deferred-refresh pattern as `type`'s setter — toggling the
			// flag swaps which of {projectileSpeed, projectileGravity} is
			// visible. EmitChanged so an inline sub-resource view re-renders.
			CallDeferred(MethodName.NotifyPropertyListChanged);
			EmitChanged();
		}
	}
	// Visual-only gravity override for arcing projectiles, in m/s² downward.
	// 0 = fall back to the world's player-physics gravity (which is calibrated
	// for the player and usually too weak to give a satisfying arc at short
	// flight times). Higher values produce a taller, snappier arc at the same
	// lifetime — for a flat-ground shot, the peak height above origin is
	// roughly `gravity * lifetime² / 8`. Ignored when projectileArcing is
	// false; flat projectiles don't accumulate gravity.
	[Export] public float projectileGravity = 0f;
	// Optional event fired at the projectile's despawn position. Runs through
	// a position-aware sub-dispatcher (currently SpawnAreaEffect; other handlers
	// require an actor / action context and would no-op here). The classic use
	// is "arcing arrow lands → spawn AoE": author the projectile event with
	// projectileArcing=true and impactEvent=<sub-event with SpawnAreaEffect
	// flagged and areaEffectScene set>. Fires regardless of how the projectile
	// ended (lifetime, env hit, hurtbox hit).
	[Export] public ItemEvent impactEvent;

	public override void _ValidateProperty(Dictionary property)
	{
		string name = property["name"].AsString();
		// Arcing-vs-flat split: projectileSpeed only applies to flat flight
		// (arcing solves velocity from cursor + lifetime + gravity);
		// projectileGravity only applies to arcing. Hide whichever doesn't
		// apply to the current mode, even when the Projectile flag is set.
		// Falls through to the flag-based hide below for the Projectile-off
		// case (both stay hidden).
		if ((_type & EItemEventType.Projectile) != 0)
		{
			if ((name == nameof(projectileSpeed) && _projectileArcing)
				|| (name == nameof(projectileGravity) && !_projectileArcing))
			{
				HideProperty(property);
				return;
			}
		}
		EItemEventType requiredFlags = GetRequiredFlags(name);
		if (requiredFlags == 0) { return; }
		if ((_type & requiredFlags) != 0) { return; }
		// Mask out the Editor bit so the field is hidden from the inspector
		// when its owning flag isn't selected. Storage is preserved, so a
		// previously-authored value comes back when the flag is re-enabled.
		HideProperty(property);
	}

	private static void HideProperty(Dictionary property)
	{
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
			nameof(language) or nameof(languageComponents) => EItemEventType.LearnLanguage,
			nameof(firstLearnEffect) => EItemEventType.LearnLanguage | EItemEventType.LearnConcept,
			nameof(concept) => EItemEventType.LearnConcept,
			nameof(damageData)
				or nameof(impactMissEffect)
				or nameof(impactEnvironmentEffect)
				or nameof(impactHealthEffect)
				or nameof(impactArmorEffect)
				or nameof(impactLethalEffect) => EItemEventType.Melee | EItemEventType.Hitscan | EItemEventType.Projectile,
			nameof(projectileScene)
				or nameof(projectileSpeed)
				or nameof(projectileLifetimeSeconds)
				or nameof(projectileLoopEffect)
				or nameof(projectileArcing)
				or nameof(projectileGravity)
				or nameof(impactEvent) => EItemEventType.Projectile,
			nameof(areaEffectScene) => EItemEventType.SpawnAreaEffect,
			_ => 0,
		};
	}
}
