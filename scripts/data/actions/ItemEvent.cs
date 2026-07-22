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

	// Melee fields — the damage volume is the convex hull of two vertical
	// "cylinder" disks seen from above: a near disk of diameter `nearWidth`
	// centered `nearWidth/2` in front of the actor, and a far disk of diameter
	// `farWidth` centered `range - farWidth/2` in front. The two disks are
	// joined by their external tangents (no acute corner where the side meets a
	// disk), so the whole swept fan between them is part of the damage zone.
	// `range` is the forward reach (the far edge) and is what the reticle /
	// StatList report as Reach. See ItemEventHandlers.DoMelee.
	[Export] public float range = 2f;
	[Export] public float nearWidth = 0.5f;
	[Export] public float farWidth = 2f;
	// Full vertical height of the damage cylinders (flat top and bottom),
	// centered on the actor's chest height. Both disks share it, so the volume
	// is a flat-capped prism rather than a rounded capsule.
	[Export] public float meleeHeight = 3f;
	// Optional swing-smear visual. Spawned by DoMelee and sized to the live
	// attack shape (range / nearWidth / farWidth) via WeaponSmear.Initialize,
	// so any status effect that grows or shrinks the attack carries through to
	// the smear for free. The scene root must be a WeaponSmear. `smearClockwise`
	// picks the sweep direction (flip it between combo steps for variety).
	[Export] public PackedScene smearEffect;
	[Export] public bool smearClockwise = true;

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
	// Projectile: spawned once at the firing origin as the launch ("muzzle")
	// cue (a fire hiss, a bowstring twang) — see ItemEventHandlers.DoProjectile.
	[Export] public PackedScene fx;

	// ConsumeFromInventory: identifies which supporting item to consume.
	// `reagent` matches ItemData on supportingItems entries; `consumeAmount`
	// is the stack count to decrement (default 1). Stack→0 removes the item
	// from the player's inventory.
	[Export] public ItemData reagent;
	[Export] public int consumeAmount = 1;

	// Damage profile key the event resolves against the firing weapon's
	// `WeaponData.damageProfiles` dict. Used by Melee / Hitscan / Projectile
	// (direct hit damage) AND by SpawnAreaEffect (damage applied to anyone
	// inside the spawned hazard volume). Default `&"primary"` matches the
	// convention of authoring a baseline damage under that key; secondary
	// effects (rain-of-arrows AoE, etc.) set a different key. Mob attacks
	// don't have a WeaponState — they'll need a parallel mob-side lookup
	// when implemented.
	[Export] public StringName damageProfileKey = new("primary");

	// ApplyMotion fields. Speed in m/s and duration in seconds describe the
	// motion phase the actor should enter; the actor resolves the base
	// direction (input/facing/etc) and any per-actor scaling (e.g. swim
	// speed). motionForwardSpeed is signed along that resolved direction:
	// positive lunges forward (the default), negative drives the actor
	// backward (a hop-back / recoil) along the same axis. When freezeGravity
	// is true, the actor zeros vertical velocity and suppresses gravity for
	// the duration — the dash hang. Sword-lunge style events leave it false
	// so gravity still applies.
	[Export] public float motionForwardSpeed = 30f;
	[Export] public float motionDuration = 0.2f;
	[Export] public bool motionFreezeGravity = true;
	// Which axis motionForwardSpeed drives along. Facing (default) commits the
	// motion to the actor's body yaw — correct for weapon lunges / recoils.
	// Movement follows active move input (falling back to facing) so a dash
	// can travel independent of facing. Mobs always lunge along facing and
	// ignore this.
	[Export] public EMotionDirection motionDirection = EMotionDirection.Facing;

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

	// CameraShake fields. Magnitude is meters of camera offset at zero
	// distance; duration is the linear-decay window in seconds. Range > 0
	// applies a linear distance falloff against the player at firing time
	// (full strength at distance 0, zero past `range`). Range == 0 ignores
	// distance and fires the raw magnitude — typical for player-sourced
	// hits where the actor IS the camera target.
	[Export] public float cameraShakeMagnitude = 0.15f;
	[Export] public float cameraShakeDuration = 0.15f;
	[Export] public float cameraShakeRange = 0f;

	// ControllerRumble fields. Weak = high-frequency (buzzy) motor, strong =
	// low-frequency (heavy) motor — both in [0,1]. Duration is the linear-decay
	// window in seconds. Range > 0 applies the same distance falloff against the
	// player as cameraShakeRange; range == 0 fires full magnitude regardless of
	// where the event lives. See EItemEventType.ControllerRumble.
	[Export(PropertyHint.Range, "0,1,0.01")] public float controllerRumbleWeak = 0.3f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float controllerRumbleStrong = 0.5f;
	[Export] public float controllerRumbleDuration = 0.2f;
	[Export] public float controllerRumbleRange = 0f;

	// ScreenFlash fields. Color + peak intensity of a one-shot full-screen
	// flash; fadeSeconds <= 0 uses the ScreenEffectsController default. See
	// EItemEventType.ScreenFlash.
	[Export] public Color screenFlashColor = new Color(1f, 1f, 1f, 1f);
	[Export(PropertyHint.Range, "0,1,0.01")] public float screenFlashIntensity = 0.5f;
	[Export] public float screenFlashFadeSeconds = 0.3f;

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
	// position when no aim cursor is active). The scene is a "template" —
	// it carries its own visual (particle loop, mesh), structural collision
	// mask, and friendly-fire policy. The weapon-side fields below override
	// damage, hazard radius, and lifetime on the spawned instance via
	// GasCloud.Initialize so a single AoE scene can be reused across weapons
	// with different power profiles (a "rain of arrows" vs a stronger
	// "storm of arrows" reuse the same .tscn).
	[Export] public PackedScene areaEffectScene;
	// World-space radius (meters) of the hazard volume. 0 leaves the scene's
	// authored collision shape unchanged. Currently only SphereShape3D is
	// resized — non-spherical hazards keep their scene radius.
	[Export] public float areaRadius = 0f;

	// ApplyAreaStatusEffect only: cap on how many allies a rally cry buffs. The
	// crier always buffs itself for free and does NOT count against this — the
	// cap bounds the OTHERS. Recipients are chosen closest-first, own species
	// (same base MobData) before other species, and another species is eligible
	// only while already triggered (in a fight). 0 = no cap (every eligible ally
	// in radius). See ItemEventHandlers.DoApplyAreaStatusEffect.
	[Export] public int areaMaxTargets = 0;
	// Total time the hazard lives, in seconds. 0 leaves the scene's authored
	// lifetimeSeconds in place. Total expected damage = continuous DPS *
	// duration + sum-of-interval-DPS * duration.
	[Export] public float areaDurationSeconds = 0f;
	// Continuous (per-frame, smooth) portion of the spawned zone's damage.
	// Resolves against the firing entity's continuousProfiles dict
	// (WeaponData / MobData). Empty StringName = no continuous portion.
	[Export] public StringName areaContinuousKey = new();
	// Interval (per-tick, discrete) portion of the spawned zone's damage.
	// Each entry pairs a damage-profile key (looked up against the firing
	// entity's damageProfiles) with its own tick cadence. Multiple entries
	// stack independently — a fire+poison cloud authors one fast burn
	// entry and one slow status-stacking entry.
	[Export] public Array<AreaIntervalSpec> areaIntervals = new();

	// SummonMinion field. The composed minion to summon at the actor's aim point
	// (positional aim cursor when active, else the actor position). A descriptor
	// (not a bare MobData) so the minion carries its weapon loadout — weapons are
	// spawn composition on MobDescriptor, not a species trait. The minion spawns
	// on the player team, follows the player, and self-drains via the drain status
	// authored on its MobData. See ItemEventHandlers.DoSummonMinion.
	[Export] public MobDescriptor minionData;

	// Dig fields. The dig is centered on the player's positional aim cursor
	// when one is active, else a point `digReach` meters in front of the
	// actor. `digRadius` is how close a buried spot / burrowed mob must be to
	// the dig center to be uncovered. Authored on the shovel's Use timeline.
	[Export] public float digRadius = 1.5f;
	[Export] public float digReach = 1.5f;
	// Dig completion effects, picked by the dig's result class (see
	// Sim.TryDig / EDigResult). One-shot Fx spawned at the dig point: a sad
	// puff when the hole comes up empty, a modest burst for a common find
	// (carrot / loot), a celebratory one for treasure. Any may be null.
	[Export] public PackedScene digNothingEffect;
	[Export] public PackedScene digCommonEffect;
	[Export] public PackedScene digTreasureEffect;

	// Projectile fields. Spawned by DoProjectile at the actor's position,
	// flying along the actor's forward (with the tier's accuracy spread
	// applied via accuracySpread01). Damage on impact is resolved from the
	// firing weapon's damageProfiles dict via `damageProfileKey`.
	// Authored inline rather than via a ProjectileData sub-resource because
	// brand-new [GlobalClass] C# Resources don't reliably bind to typed
	// [Export] slots in Godot 4.6 — the same fields a sibling sub-resource
	// would carry just live on the event itself, matching how `meleeRange`
	// / `hitScanRange` are authored.
	[Export] public PackedScene projectileScene;
	[Export] public float projectileSpeed = 25f;
	// Hard cap on flight time before the projectile despawns; the reticle
	// derives effective range as projectileSpeed * projectileLifetimeSeconds.
	// When projectileArcing is true, this is the FUSE — the lob detonates this
	// many seconds after launch — and with projectileMaxRange it caps the
	// horizontal launch speed (aimDistance / lifetime, at most maxRange /
	// lifetime); projectileSpeed is ignored.
	[Export] public float projectileLifetimeSeconds = 1f;
	// How many creatures this projectile passes THROUGH before it stops. 0
	// (default) is a normal shot: it stops on the first creature it hits. 1 means
	// it punches through the first creature and stops on the second; N stops on
	// the (N+1)'th. It damages every creature along the way, adding each to its
	// own hit list so none is struck twice, and only stops (proc'ing its impact
	// fx, arrow drop/stick, and impactEvent) once the budget is spent or it meets
	// a solid surface. Weapon mods can raise this via
	// StatusEffectData.projectilePierceCount; the effective count is the max.
	// Only meaningful for flat (non-arcing) flight — arced lobs detonate at their
	// fuse and don't pierce.
	[Export] public int pierceCount = 0;
	// How many projectiles this event launches per fire. >1 fans a flat volley
	// out by re-sampling the tier's accuracy spread per shot (a twin-missile
	// swing, a shotgun blast). Ignored for arced lobs (they reuse the single
	// solved launch velocity). 1 (default) = a single shot.
	[Export] public int projectileCount = 1;
	// Arcing projectile: a fixed-shape, COLLISION-RESPECTING lob. The firing tier
	// uses Arced (ground-cursor) aim; the hump rises projectileArcRise meters
	// under projectileGravity (vertical) and lands on the aim point over the fuse
	// — horizontal launch speed = aimDistance / projectileLifetimeSeconds, capped
	// at (charge-scaled) projectileMaxRange / lifetime. The throw and the
	// reticle's ribbon (AimingReticle.SolveArcToCursor) compute the identical
	// hump, so the preview and the real throw agree by construction. It bounces
	// (projectileBounciness / projectileFriction) off solids until
	// projectileLifetimeSeconds — the fuse — where it detonates and fires
	// `impactEvent`. projectileSpeed is ignored. Use for thrown explosives.
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
	// Arced (lobbed) projectiles: peak vertical RISE in meters above the launch
	// point at the top of the hump, and the gravity (m/s²) pulling it down. These
	// fix ONLY the vertical motion (launch speed √(2·g·rise), then free fall) and
	// are independent of the throw's horizontal range. Ignored when
	// projectileArcing is false.
	[Export] public float projectileArcRise = 1.25f;
	[Export] public float projectileGravity = 14f;
	// Arced (lobbed) projectiles: the single authored range — maximum HORIZONTAL
	// (XZ) throw distance, the aim disk radius, and (over
	// projectileLifetimeSeconds) the max horizontal launch speed. The throw lands
	// on the aim point (speed = aimDistance / lifetime, aimDistance ≤ this cap);
	// the firing tier's chargedRangeScale ramp scales the cap. Decoupled from
	// gravity/rise (the vertical hump and the horizontal reach are authored
	// independently — the throw is NOT assumed to land when it returns to the
	// thrower's foot level). Ignored when projectileArcing is false.
	[Export] public float projectileMaxRange = 10f;
	// Arced projectiles bounce off solids they hit before the fuse expires.
	// projectileBounciness is the NORMAL restitution (fraction of the into-surface
	// speed kept on the rebound — higher = bouncier off walls); projectileFriction
	// is the TANGENTIAL loss (fraction of the along-surface speed shed per hit —
	// higher = rolls to a stop faster on the ground). Both ignored for flat flight.
	[Export(PropertyHint.Range, "0,1,0.05")] public float projectileBounciness = 0.5f;
	[Export(PropertyHint.Range, "0,1,0.05")] public float projectileFriction = 0.5f;
	// Optional event fired at the projectile's despawn position. Runs through
	// a position-aware sub-dispatcher (currently SpawnAreaEffect; other handlers
	// require an actor / action context and would no-op here). The classic use
	// is "arcing arrow lands → spawn AoE": author the projectile event with
	// projectileArcing=true and impactEvent=<sub-event with SpawnAreaEffect
	// flagged and areaEffectScene set>. Fires regardless of how the projectile
	// ended (lifetime, env hit, hurtbox hit) UNLESS a cause-specific event below
	// is authored for that cause.
	[Export] public ItemEvent impactEvent;
	// Cause-specific follow-up events, each overriding impactEvent for one
	// despawn cause and falling back to it when null. directHitEvent fires when
	// the shot ends on a creature (e.g. a homing missile that detonates an AoE
	// only on a direct hit); expirationEvent fires when it ends on lifetime
	// expiry (e.g. a fizzle puff). Environment clips always use impactEvent.
	[Export] public ItemEvent directHitEvent;
	[Export] public ItemEvent expirationEvent;
	// Intrinsic "Fragile": the arced lob detonates on the FIRST surface/creature
	// it meets instead of bouncing out its fuse — the authored form of the
	// Fragile weapon mod (StatusEffectData.projectilesDetonateOnContact), so a
	// mob attack or a born-fragile weapon needs no enchant. ORed with the mod at
	// fire time (ItemEventHandlers.ArcDetonatesOnContact); only meaningful with an
	// impactEvent (the payload fired where it shatters). Ignored for flat flight.
	[Export] public bool projectileFragile;
	// Ground telegraph for an arced shot: a flat decal scene (a GroundDecalPreview
	// on the stain layer) dropped at the predicted landing point when the lob
	// launches, marking where it will come down so the target can read and dodge
	// it. Lives ~the fuse then fades itself. Null = no telegraph. The classic use
	// is a mob's lobbed attack; ignored for flat flight.
	[Export] public PackedScene projectileTargetPreview;

	public override void _ValidateProperty(Dictionary property)
	{
		string name = property["name"].AsString();
		// Arcing-vs-flat split: projectileSpeed only applies to flat flight (an
		// arced lob derives its vertical motion from rise + lifetime and its
		// horizontal from the aim); projectileArcRise / projectileBounciness only
		// apply to arcing. Hide whichever doesn't apply to the current mode, even
		// when the Projectile flag is set. Falls through to the flag-based hide
		// below for the Projectile-off case (all stay hidden).
		if ((_type & EItemEventType.Projectile) != 0)
		{
			bool arcOnly = name == nameof(projectileArcRise) || name == nameof(projectileBounciness)
				|| name == nameof(projectileGravity) || name == nameof(projectileFriction)
				|| name == nameof(projectileMaxRange) || name == nameof(projectileFragile)
				|| name == nameof(projectileTargetPreview);
			bool flatOnly = name == nameof(projectileSpeed) || name == nameof(pierceCount)
				|| name == nameof(projectileCount);
			if ((flatOnly && _projectileArcing)
				|| (arcOnly && !_projectileArcing))
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
			nameof(range) or nameof(nearWidth) or nameof(farWidth) or nameof(meleeHeight)
				or nameof(smearEffect) or nameof(smearClockwise) => EItemEventType.Melee,
			nameof(hitScanRange) => EItemEventType.Hitscan,
			nameof(effects) => EItemEventType.ApplyStatusEffect | EItemEventType.ApplyAreaStatusEffect,
			nameof(animName) => EItemEventType.PlayAnim,
			nameof(fx) => EItemEventType.OpenInteractive | EItemEventType.ApplyAreaStatusEffect | EItemEventType.Projectile,
			nameof(reagent) or nameof(consumeAmount) => EItemEventType.ConsumeFromInventory,
			nameof(motionForwardSpeed) or nameof(motionDuration) or nameof(motionFreezeGravity) or nameof(motionDirection) => EItemEventType.ApplyMotion,
			nameof(language) or nameof(languageComponents) => EItemEventType.LearnLanguage,
			nameof(firstLearnEffect) => EItemEventType.LearnLanguage | EItemEventType.LearnConcept,
			nameof(concept) => EItemEventType.LearnConcept,
			nameof(damageProfileKey) => EItemEventType.Melee | EItemEventType.Hitscan | EItemEventType.Projectile,
			nameof(impactMissEffect)
				or nameof(impactEnvironmentEffect)
				or nameof(impactHealthEffect)
				or nameof(impactArmorEffect)
				or nameof(impactLethalEffect) => EItemEventType.Melee | EItemEventType.Hitscan | EItemEventType.Projectile,
			nameof(projectileScene)
				or nameof(projectileSpeed)
				or nameof(projectileLifetimeSeconds)
				or nameof(pierceCount)
				or nameof(projectileCount)
				or nameof(projectileArcing)
				or nameof(projectileArcRise)
				or nameof(projectileGravity)
				or nameof(projectileMaxRange)
				or nameof(projectileBounciness)
				or nameof(projectileFriction)
				or nameof(impactEvent)
				or nameof(directHitEvent)
				or nameof(expirationEvent)
				or nameof(projectileFragile)
				or nameof(projectileTargetPreview) => EItemEventType.Projectile,
			nameof(areaEffectScene)
				or nameof(areaDurationSeconds)
				or nameof(areaContinuousKey)
				or nameof(areaIntervals) => EItemEventType.SpawnAreaEffect,
			nameof(areaRadius) => EItemEventType.SpawnAreaEffect | EItemEventType.ApplyAreaStatusEffect,
			nameof(areaMaxTargets) => EItemEventType.ApplyAreaStatusEffect,
			nameof(cameraShakeMagnitude)
				or nameof(cameraShakeDuration)
				or nameof(cameraShakeRange) => EItemEventType.CameraShake,
			nameof(controllerRumbleWeak)
				or nameof(controllerRumbleStrong)
				or nameof(controllerRumbleDuration)
				or nameof(controllerRumbleRange) => EItemEventType.ControllerRumble,
			nameof(screenFlashColor)
				or nameof(screenFlashIntensity)
				or nameof(screenFlashFadeSeconds) => EItemEventType.ScreenFlash,
			nameof(digRadius) or nameof(digReach)
				or nameof(digNothingEffect)
				or nameof(digCommonEffect)
				or nameof(digTreasureEffect) => EItemEventType.Dig,
			nameof(minionData) => EItemEventType.SummonMinion,
			_ => 0,
		};
	}
}
