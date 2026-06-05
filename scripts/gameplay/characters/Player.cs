using Godot;
using System;
using System.Collections.Generic;

public enum EWaterState
{
	None,
	Shallow,
	Swimming,
}

[GlobalClass]
public partial class Player : CharacterBody3D
{
	[Export] public PlayerData data;
	// The character's display name (stats panel, etc.), set from
	// PlayerSpawnData.playerName at Initialize. Defaults to a placeholder so the
	// UI always has something to show even before / without spawn data.
	public string PlayerName { get; private set; } = "Wyatt Anderson";
	[Export] public Area3D interactArea;
	// World-space anchor (head height) used to project a screen-space point
	// above the player for HUD elements that float over the character — e.g.
	// the transient status-effect notification. Mirrors Mob.HudAnchor.
	[Export] public Node3D HudAnchor;
	[Export] private HurtBox _hurtBox;
	// Per-gender base model packages, keyed by (int)EGender. Each value is a
	// PlayerModelPackage scene (model + ModelAnimator + HeldItemVisual +
	// PixelSnap, all wired inside it). Initialize instances exactly the spawned
	// gender's package and reads its drivers — so only one rig is ever built,
	// not every gender hidden behind one. A gender with no entry falls back to
	// Female. Adding a body type = author a package scene + add a dictionary
	// entry; no new fields or code branches.
	[Export] private Godot.Collections.Dictionary<int, PackedScene> _modelPackages = new();
	// The live model package instanced for the spawned gender (child of Player),
	// and its animator — resolved in Initialize once the spawn gender is known.
	// The rest of the class drives animation through _animator.
	private PlayerModelPackage _modelPackageInstance;
	private ModelAnimator _animator;
	// The visual root of the live model subtree — toggled by SetModelVisible for
	// hide / birds-eye.
	private Node3D _activeVisual;
	[Export] private AudioListener3D _audioListener;
	[Export] private AimingReticle _aimingReticle;
	// Drives the in-hand 3D model of the wielded weapon / used consumable.
	// Updated event-side from TryStartWeaponAction (weapon swap) and per-tick
	// from UpdateHeldItemVisual (consumable show, weapon conceal). Optional —
	// null on rigs without the socket wired.
	[Export] private HeldItemVisual _heldVisual;
	// Status effect applied while the player is in water or in unsheltered
	// rain. Authored data lives on the resource (duration, displayName, icon);
	// TickWetEffect arms / pauses the timer so the 30s dry-out only counts
	// while the player is actually drying.
	[Export] private StatusEffectData _wetEffectData;
	// Item-side wet status used for equipped armor's per-piece meter. Has the
	// same icon / arm-disarm shape as `_wetEffectData` but NO modifier list —
	// cold/heat threshold shifts are an actor-side concern and only the
	// player's Wet effect carries them. The cascade in TickWetEffect reads
	// each equipped armor's wet-clothes buildup and contributes that into
	// the player's Wet meter, which is what actually arms the modifier-
	// bearing effect on the wearer. Falls back to `_wetEffectData` if left
	// null in the .tscn so the system still works without authoring.
	[Export] private StatusEffectData _wetClothesEffectData;

	// Status effect armed while the player wears a fully-grimy piece of armor.
	// Carries the Scent modifier (and the HUD icon) — TickDirtyEffect drives
	// its ContinuousArm meter to track the dirtiest worn piece, so the smell
	// penalty turns on once a garment is dirty and off when it's washed clean.
	[Export] private StatusEffectData _dirtyEffectData;
	// Item-side grime status used for each armor piece's per-piece meter. Has
	// the same arm/disarm shape as `_dirtyEffectData` but NO modifier list —
	// the Scent penalty is an actor-side concern that folds once, on the
	// player, when their meter arms (mirrors the wet-clothes split). Falls back
	// to `_dirtyEffectData` if left null in the .tscn.
	[Export] private StatusEffectData _dirtyClothesEffectData;

	[ExportGroup("FX")]
	// One-shot blood splatter spawned at the player's position on a non-lethal
	// damage hit. Spawned in world space so the puff stays put as the player
	// runs through it, matching the footstep effect convention.
	[Export] private PackedScene _bloodDamageFx;
	// One-shot death blood spawned the moment _health crosses to zero.
	[Export] private PackedScene _deathFx;
	// One-shot splash spawned when the player first enters a water trigger
	// (overlap count goes 0 → 1 in WaterAreaEntered).
	[Export] private PackedScene _waterEnterSplashFx;
	// Continuous loop scenes (see Fx._loop). Parented to the player
	// so they follow the body; held alive while in the matching state and
	// stopped when leaving so the trailing audio + particles wind down cleanly.
	[Export] private PackedScene _waterMovementLoopFx;
	[Export] private PackedScene _foliageMovementLoopFx;
	// Speed-line streak loop spawned during a dash burst (held alive while
	// _dashTimeRemaining > 0). A trailing particle effect parented to the body
	// so it tracks/rotates with the dash.
	[Export] private PackedScene _dashSpeedLinesFx;
	// Per-ground-type foot-puff loop spawned while sliding / skating /
	// skidding. Tracks the body. Keys must match what GroundTypeResolver
	// returns at the player's position; missing keys silently emit nothing
	// for that surface (so a grass-only authoring pass still works on grass
	// even if Stone is unwired). Re-resolved each tick — walking from grass
	// onto stone mid-skate swaps the loop scene wholesale.
	[Export] private Godot.Collections.Dictionary<EGroundType, PackedScene> _slideLoopFx = new();
	// One-shots for vertical motion. Jump fires the moment input takes the
	// player off the floor. Land fires on every floor reacquisition unless
	// the inbound vertical speed exceeded LandHardSpeedThreshold, in which
	// case landHard takes its place — a heavier impact deserves dust + a
	// harder hit.
	[Export] private PackedScene _jumpFx;
	[Export] private PackedScene _landFx;
	[Export] private PackedScene _landHardFx;
	// Wall jump: foot fx spawns at the player's position (particle + scuff
	// sound at the kicking foot), effort fx layers a voice grunt. Both fire
	// from TryWallJump alongside the velocity reset and Jump one-shot anim.
	[Export] private PackedScene _wallJumpFootFx;
	[Export] private PackedScene _wallJumpEffortFx;
	// High-speed water entry. Picked over the standard splash when inbound
	// vertical speed at WaterAreaEntered exceeds WaterPlungeSpeedThreshold.
	[Export] private PackedScene _waterPlungeFx;
	// VO that plays in tandem with _bloodDamageFx / _deathFx on
	// the same hit. Separate scenes so the per-actor voice clips can ride on
	// top of the shared impact / death-splat audio without authoring per-
	// actor blood scenes.
	[Export] private PackedScene _hurtVoFx;
	[Export] private PackedScene _deathVoFx;
	// Armor lifecycle one-shots. Depleted plays the moment armor hits zero
	// from damage; rechargeStart plays when the post-hit recharge delay
	// elapses and the bar starts climbing again; recoverStart replaces it
	// when the recharge follows a full depletion (longer recover delay).
	[Export] private PackedScene _armorDepletedFx;
	[Export] private PackedScene _armorRechargeStartFx;
	[Export] private PackedScene _armorRecoverStartFx;
	// One-shot "out of breath" pant spawned the moment stamina is exhausted
	// (crosses from positive to <= 0 via sprint / swim / dash / wall-jump
	// spend). Parented to the player so the gasp audio + breath puff track the
	// body as they keep moving. Re-arms only after stamina climbs back above
	// zero, so it fires once per exhaustion rather than every frame at the floor.
	[Export] private PackedScene _outOfBreathFx;
	// Per-anim-state loops. UpdateAnimation maps the picked loopAnim down to
	// one of these scenes; only one (or none) is active at a time. Slots can
	// be left null in the .tscn — the actor falls silent for that state,
	// which is the current player default until per-character idle / run /
	// swim_idle audio is authored.
	[Export] private PackedScene _idleLoopFx;
	[Export] private PackedScene _runLoopFx;
	[Export] private PackedScene _swimIdleLoopFx;

	[ExportGroup("Footsteps & Footprints")]
	// Per-ground-type one-shot effect played at the player's feet on each
	// footfall. Authored in the player .tscn; missing keys silently emit
	// nothing.
	[Export] private Godot.Collections.Dictionary<EGroundType, PackedScene> _footstepEffects;
	// Splash effect emitted on footfall while running through Shallow water.
	// Bypasses the per-ground dict because shallow-water detection lives in
	// _waterState (an Area-trigger flag), not the EGroundType resolver — a
	// thin film of water over grass should still trigger water audio.
	[Export] private PackedScene _shallowWaterFootstepFx;
	// Minimum horizontal speed² to count as "moving" for loop-FX gating
	// (water swim loop, tall-grass rustle). Footstep cadence itself is
	// frame-driven and ignores this.
	[Export] private float _movingMinSpeedSq = 0.25f;
	// Per-character footprint texture projected onto the ground on each
	// footfall. The shared player vs mob footprint scenes (and per-ground
	// tints) live on SimData; this is the only print authoring that varies
	// per character.
	[Export] private Texture2D _footprintTexture;
	// World-space size (meters) of the projected footprint decal — X is the
	// print's width (perpendicular to facing), Y is its length (along facing).
	[Export] private Vector2 _footprintSize = new(0.3f, 0.4f);

	public Action<Node3D> onHighlightChanged;
	public Action<IInteractive> onInteractChanged;
	public Action<Player> OnWaterEnter;
	public Action<Player> OnWaterExit;
	// Fired once on the alive→dead transition (any source: damage, status
	// tick). GameClient subscribes to drive the death screen + audio fade.
	// Re-hits on the corpse do NOT re-fire — gating lives at the call site
	// in OnHurtBoxHit / ApplyStatusHealthDelta, since those are the only
	// places that drop _health.
	public Action<Player> onDied;

	// Fired when bird's-eye view begins (true) or the return animation begins
	// (false). GameClient subscribes to drive the camera fly-up/down and the
	// post-process motion blur. Movement stays locked from the begin firing
	// until GameClient calls OnBirdsEyeReturnComplete() once the camera has
	// landed back on the player.
	public Action<bool> onBirdsEye;
	bool _birdsEye;
	public bool IsBirdsEye => _birdsEye;

	// Mirrors the mob's `burrowed` flag, but for the player: while true the
	// player is unperceivable. MobAI's mob-perceives-player tick zeroes every
	// sense contribution when this is set, so any standing aggro decays and the
	// triggered alert resets — exactly how a burrowed mob drops off the
	// player's own perception. Set true while perched in a [[ClimbableTree]];
	// the climb also hides the model and lifts the camera into bird's-eye.
	bool _hidden;
	public bool IsHidden => _hidden;

	public void BeginBirdsEye()
	{
		if (_birdsEye)
		{
			return;
		}
		_birdsEye = true;
		onBirdsEye?.Invoke(true);
	}

	// Asks GameClient to begin the fly-down. The movement lock is held until
	// OnBirdsEyeReturnComplete fires from the camera driver.
	public void RequestEndBirdsEye()
	{
		if (!_birdsEye)
		{
			return;
		}
		onBirdsEye?.Invoke(false);
	}

	public void OnBirdsEyeReturnComplete()
	{
		_birdsEye = false;
		// A tree climb rides the bird's-eye lifecycle: the camera landing back
		// on the player is also when the player drops out of the canopy, so
		// restore the model and clear concealment here. Exit can be triggered
		// by ESC or by taking damage (see OnHurtBoxHit) — both route through
		// the fly-down, so this single restore covers every path.
		if (_hidden)
		{
			_hidden = false;
			SetModelVisible(true);
		}
	}

	// Entered from ClimbableTree.Complete. Conceals the player (hidden from
	// mobs + model hidden) and lifts into the bird's-eye overlook. The matching
	// restore lives in OnBirdsEyeReturnComplete, driven by the bird's-eye
	// fly-down — there is no explicit "descend" call, the player leaves the tree
	// by ending bird's-eye (ESC) or by taking damage.
	public void EnterClimbableTree()
	{
		if (_hidden || _birdsEye)
		{
			return;
		}
		_hidden = true;
		SetModelVisible(false);
		BeginBirdsEye();
	}

	// Toggles the player's model subtree visibility (hide / birds-eye).
	void SetModelVisible(bool visible)
	{
		if (_activeVisual != null)
		{
			_activeVisual.Visible = visible;
		}
	}

	World _world;
	IInteractive _curInteractive;
	// Companion to _curInteractive — names which entry in the interactive's
	// GetActions() list the player has committed to. Future radial-menu UI
	// will overwrite this between highlight and commit so the player can
	// pick Lockpick/Break/Open on a chest.
	int _curInteractiveActionIndex;
	IInteractive _highlightInteractive;
	// Rideable vehicle the player is currently aboard (boat now, mounts later).
	// While non-null the normal locomotion in _PhysicsProcess is suspended — the
	// player is parented under the vehicle's seat anchor and the vehicle drives
	// the transform. See Mount / Dismount.
	IRideable _mount;
	// The player's parent before mounting, restored on dismount so the rider
	// returns to the world layer rather than leaking under the (possibly
	// streamed-out) vehicle.
	Node _preMountParent;
	// Latched at AttackContextSensitive press time so the release routes to
	// the same weapon slot even if the player releases Aim mid-attack.
	EInventorySlot? _contextSensitiveAttackSlot;
	// Press received while the weapon's cooldown is still ticking. Each tick
	// ProcessInput re-checks: button no longer held → discard; cooldown now
	// elapsed and runner free → convert to a real start. Lets the player hold
	// the button through the tail of the cooldown to chain attacks without
	// frame-perfect timing. The action-name is stored alongside the slot so
	// the polling check can re-read the input the player is actually holding.
	EInventorySlot? _pendingWeaponPressSlot;
	string _pendingWeaponPressActionName;
	readonly List<IInteractive> _interactiveCollisions = new();
	readonly List<Foliage> _foliageCollisions = new();
	float _terrainSpeed = 1f;
	bool _grounded;
	bool _aiming;
	bool _sneaking;
	// Active while Dash is held past the initial dash burst with move input
	// and positive stamina. Drains stamina continuously and rearms the
	// recharge delay each tick. Blocks aim, ends on Dash release / stamina
	// depletion / attack press / weapon Active. Set each frame in ProcessInput
	// from current conditions — not latched.
	bool _sprinting;
	// Latched true when sprint ended because stamina ran out while Dash was
	// still held. Prevents the held button from auto-re-engaging sprint the
	// moment stamina refills — the player has to release Dash and press it
	// again. Cleared when Dash is released.
	bool _sprintLockout;
	EWaterState _waterState = EWaterState.None;
	float _waterSurfaceY;
	int _waterOverlapCount;
	readonly WaterRippleEmitter _rippleEmitter = new();
	// Breadcrumb-based scent trail. Constructed in Initialize once `_world`
	// and PlayerData are known. Mobs read Scent.Crumbs in their
	// mob-perceives-player tick.
	ScentEmitter _scent;
	// Active loop instances. Null when the matching state isn't held; created
	// on the first frame state becomes active and Stop()'d when it ends. We
	// drop the reference at Stop() so the next activation creates a fresh
	// node rather than racing with the trailing-audio teardown.
	Fx _waterMovementLoop;
	Fx _foliageMovementLoop;
	Fx _slideLoop;
	Fx _dashLoop;
	// Tracked active slide-loop scene so per-ground-type swaps avoid
	// recreating the Fx every tick. Same shape as _animLoopScene.
	PackedScene _slideLoopScene;
	// Live "in contact with a steep slope" flag. Set in UpdateSlideState
	// after MoveAndSlide based on slide-collision normals or a Down probe;
	// drives the slide puff Fx and serves as the gate for skate initiation.
	bool _sliding;
	// True when the current contact is in the extended skate band
	// (any surface from slideSurfaceMinNormalY up to skateContinueMaxNormalY).
	// Superset of _sliding — includes walkable ramps that are still steep
	// enough to keep skate momentum alive. Used by UpdateSkating to decide
	// whether to keep skating; _sliding stays strictly steep-only for FX.
	bool _onSkateSurface;
	// Most recent skate-surface normal — meaningful while _onSkateSurface is
	// true. ApplySkatingMotion projects gravity onto this normal to derive
	// the slope-tangent acceleration each tick.
	Vector3 _slideNormal = Vector3.Up;
	// Skating mode flag. Lifts the runSpeed clamp, applies gravity along the
	// slope tangent, converts move input into steering. Set in TryStartSkating
	// when the player lands on a slide surface with momentum aligned downhill;
	// cleared by the exit conditions in UpdateSkating.
	bool _skating;
	// Grounded "skid" state — true when the gap between the input-target
	// velocity and the actual velocity exceeds moveSpeed (player is making
	// a sharp direction change). Drives the same puff Fx as sliding/skating.
	// Hysteresis: enters at |gap| > moveSpeed, exits at |gap| < 0.5*moveSpeed.
	// Set only from the grounded ApproachXZ sub-branch; cleared in every
	// other velocity branch (dash, knockback, skating, airborne, swim).
	bool _skidding;
	// Game-time at which slide AND ground contact were both first lost while
	// skating. Skating tolerates a brief loss of contact (jumping ridges,
	// voxel face transitions) and only exits once the deadline elapses.
	// Cleared whenever contact is reacquired.
	ulong _skateContactLostMs;
	// Grace window after losing all surface contact while skating before
	// skating exits. Sized to swallow single-tick face-transition gaps.
	const ulong SkateContactGraceMs = 200;
	// Single active anim-loop reference + the scene it was created from.
	// Swapped wholesale on transitions instead of cross-fading.
	Fx _animLoopFx;
	PackedScene _animLoopScene;
	ulong _coyoteTimeEndMs;
	// Ring buffer of recent grounded positions — teleport target for the
	// stuck-in-crevice recovery below. The recovery uses the OLDEST entry
	// so the player respawns ~0.5s back along their path, well clear of
	// the cliff edge that launched them in. Using the most recent grounded
	// position would land them on the edge tile itself and they'd fall
	// straight back into the crevice. Initialized to spawn so recovery has
	// a sane fallback even if the player wedges before ever touching ground.
	const int SafeGroundedHistorySize = 30; // ~0.5s at 60Hz physics
	readonly Vector3[] _safeGroundedHistory = new Vector3[SafeGroundedHistorySize];
	int _safeGroundedHistoryWriteIdx;
	// Position at the end of the previous physics tick. Used by the stuck-
	// recovery to measure actual displacement-per-tick — Velocity can't be
	// trusted in this code path because MoveAndSlide's slide projection
	// barely damps Y against near-vertical wall normals, so Velocity.Y
	// grows to large terminal values even when the body isn't moving.
	Vector3 _lastTickPosition;
	// Deadline (game-time ms) after which the stuck-recovery fires. Pushed
	// forward every airborne tick the player is actually moving; only
	// elapses when displacement has stalled (wedged geometry, pinched capsule).
	// Zero when grounded or when the deadline hasn't been armed yet.
	ulong _stuckCheckDeadlineMs;
	bool _jumpHeld;
	// Seconds remaining in the wall-jump air-control blend window. Set to
	// data.wallJumpAirControlTime by TryWallJump; while > 0 and airborne, the
	// input-driven velocity rebuild lerps from current velocity toward the
	// input target rather than snapping, preserving the kick arc. Decays each
	// physics tick regardless of which velocity branch wins and is cleared on
	// landing so a touch-down between wall jumps doesn't carry residual blend.
	float _wallJumpAirControlTimer;
	Inventory _inventory;

	// Slope diagnostics published by UpdateSlopeDebug when CVars.debugSlopes
	// is on. Static so DiagnosticsOverlay can read without a Player ref. NaN
	// floor angle = airborne. Wall fields hold the most recent unwalkable
	// upward-facing slope hit; HasWallHit gates whether they've ever been set.
	public static float DebugFloorAngleDeg = float.NaN;
	public static float DebugLastWallAngleDeg;
	public static Vector3 DebugLastWallNormal;
	public static Vector3 DebugLastWallPosition;
	public static ulong DebugLastWallHitMs;
	public static bool DebugHasWallHit;
	float _debugLastLoggedWallAngle = float.NaN;
	ulong _debugLastWallLogMs;
	// Languages the player has partially or fully learned this run. Keyed by
	// the shared LanguageData resource instance; value is the set of
	// components learned (Grammar/Numbers/Glyphs/Spelling). A missing key
	// means the language is fully unknown — all four components scramble.
	// TextScrambler reads the per-language gap to decide which transforms
	// to apply at display time.
	readonly Dictionary<LanguageData, ELanguageComponents> _learnedLanguages = new();
	ActionRunner _runner;
	float _health;
	float _armor;
	float _maxArmor;
	// Per-hit flinch state. _hitstunTime counts down each physics tick while
	// > 0 and holds the hitstun anim's effect on UpdateAnimation; _knockbackTime
	// mirrors it for the knockback lockout window. Independent of any dizzy
	// meter (which the player doesn't have today) — every hit that authors
	// hitstun flinches. _knockbackVelocity is the horizontal velocity forced
	// onto Velocity each physics tick while _knockbackTime > 0 (distance/time
	// at hit time), so the body covers exactly the authored distance.
	float _hitstunTime;
	float _knockbackTime;
	Vector3 _knockbackVelocity;
	// Game-time at which armor recharge can begin. Set to (now + rechargeDelay)
	// on every armor-absorbing hit, and to (now + recoverTime) on the hit that
	// drops armor to zero — the longer recover window is what _armorDepleted
	// tracks so the recharge-begin oneshot can pick the recover variant.
	ulong _armorRechargeStartMs;
	bool _armorRecharging;
	bool _armorDepleted;
	float _stamina;
	// Latched while stamina sits at/below zero so the out-of-breath one-shot
	// fires once on the positive→exhausted crossing rather than every frame at
	// the floor. Cleared when stamina recovers above zero (see TickStaminaExhaustion).
	bool _staminaExhausted;
	// Game-time at which stamina recharge can begin. Set to (now + rechargeDelay)
	// on every ConsumeStamina call; TickStamina is a no-op until now reaches it.
	ulong _staminaRechargeStartMs;
	// "Blood mana" drain — HP paid by an action via TryDrainBlood that
	// refunds itself over time. Modeled on armor: a single shared
	// _bloodRegenStartMs is pushed forward on every drain, after which
	// _drainedHealth pays back at PlayerData.bloodRegenSpeed HP/sec. HUD
	// reads DrainedHealth and renders it as a darker red region anchored
	// to the right edge of the health bar.
	float _drainedHealth;
	ulong _bloodRegenStartMs;
	public float DrainedHealth => _drainedHealth;
	// Dash state machine. Seeded by Player.ApplyMotion (driven by an
	// ApplyMotion event in the dash action profile). While
	// _dashTimeRemaining > 0, _PhysicsProcess overrides the input-driven
	// horizontal velocity with _dashDir * _dashSpeed (terrain-scaled) and,
	// if _dashFreezeGravity, zeros Y and skips gravity. When the timer
	// hits zero, EndDash clamps velocity in-place to a context-dependent
	// cap (sprintSpeed on ground, swimSprintSpeed in water, uncapped in
	// air) so the dash hands off into normal motion without a separate
	// glide window. _dashCooldownEndMs gates re-activation.
	Vector3 _dashDir;
	float _dashSpeed;
	float _dashTimeRemaining;
	bool _dashFreezeGravity;
	ulong _dashCooldownEndMs;
	// Status effects (poison, heal-over-time, hot, wet, ...). Multiple
	// instances of the same StatusEffectData stack — each AddStatusEffect
	// appends a fresh state and ticks independently. The HUD groups by data
	// when rendering. Wired in Initialize once `_world` is known.
	StatusEffectController _statusEffects;
	// Live handle to the player's wet effect (null when dry). Reused across
	// re-wettings so the HUD shows a single Wet stack rather than rolling a
	// fresh icon every time the player enters/leaves rain. Wetness storage
	// lives on the StatusEffectController's buildup meter for _wetEffectData
	// (the Wet effect is authored as EBuildupBehavior.ContinuousArm) — the
	// controller arms / releases the effect via armThreshold / disarmThreshold
	// hysteresis whenever the meter is updated.
	// Count of overlapping active warmth zones (campfires). > 0 suppresses
	// wet entirely and clears any in-flight wet timer. Counter (not bool) so
	// two adjacent campfires don't release the player from one's overlap
	// when they leave the other's.
	int _warmthZoneCount;
	// Sum of warmingTemperature across every active warmth zone the player
	// is standing inside. Added to the GameClient-sampled environmental
	// temperature when computing bodyTemperature drift each tick.
	float _warmthBonus;
	// Smoothed perceived temperature in degrees F. Drifts toward the sampled
	// environment + warmth bonus at PlayerData.temperatureAcclimationSpeed
	// so a brief gust through a cold patch doesn't trigger Cold.
	float _bodyTemperature = 70f;
	// Live handles to the cold / hot statuses (null when not afflicted).
	// We keep the reference so the safe-band timer arms / pauses on the
	// EXISTING state instead of stacking icons. (Wet uses the controller's
	// ContinuousArm buildup meter for the same effect — no Player-side
	// handle needed.)
	StatusEffectState _coldState;
	StatusEffectState _hotState;
	// Rolls up HitInfo.dot per-frame damage / heal into one onDamage /
	// onHeal invocation per second so a burn or poison zone emits a single
	// floating HUD number per second instead of one per physics frame.
	// Non-DoT hits bypass this and fire onDamage / onHeal immediately.
	readonly DotHudAccumulator _dotHud = new();
	MovingLight _movingLight;
	// One-shot animation latch — holds the resolved clip name currently playing
	// as a one-shot (attack / jump / die / hitstun / weapon block); default = no
	// one-shot, fall back to the state-driven loop. `_oneShotIsHitstun` gates the
	// special hitstun timer release; `_oneShotOverridesCharge` keeps a block
	// reaction alive over a held weapon charge (every other one-shot yields to a
	// charge pose).
	StringName _oneShotClip;
	bool _oneShotIsHitstun;
	bool _oneShotOverridesCharge;
	// The weapon whose WeaponAnimSet currently drives the player's stance / charge
	// / attack poses — the last weapon popped into the hand (swing or aim). Null
	// until the player first draws a weapon; then its set overrides the unarmed
	// clips. Charge/attack poses prefer the runner's in-flight weapon (authoritative
	// during an action); this field is the fallback for the idle/run stance between
	// actions.
	WeaponState _wieldedWeapon;
	// Lazily-built WeaponState wrapping PlayerData.unarmedWeapon — the fists the
	// melee attack falls back to when the WeaponLeft slot is empty. Cached so the
	// unarmed weapon keeps its own runtime state (combo chain, exp/level) across
	// swings instead of being rebuilt each press. Lives on the player, never in
	// the inventory. See GetMeleeWeaponOrUnarmed.
	WeaponState _unarmedWeapon;
	// Wall-clock time at which the player most recently lost ground contact.
	// Drives the fall-anim grace window — running up/down hills momentarily
	// lifts off, and we don't want a one-frame !_grounded to spike the fall
	// animation. 0 means currently grounded (or never run a frame yet).
	ulong _airborneStartMs;


	public float visibility = 1f;
	// Individual factors that compose into `visibility`, exposed for the
	// mob-perceives-player debug HUD (CVars.debugMobPerception). Written each
	// UpdateVisibility tick alongside the composite.
	public float visibilityLight = 1f;
	public float visibilitySpeed = 1f;
	public float visibilityCamouflage = 1f; // 1 - max(grass.camouflage)
	public ScentEmitter Scent => _scent;
	// Current movement-noise output, in decibels. Sampled by mobs in their
	// mob-perceives-player tick to add a hearing contribution to perception.
	// 0 = silent (stationary); peaks at PlayerData.runDecibels at moveSpeed.
	// Mapped from Velocity in UpdateVisibility once per frame to keep the
	// per-mob perception tick a plain field read.
	public float CurrentDecibels { get; private set; }
	public bool IsAiming => _aiming;
	public bool IsSneaking => _sneaking;
	public bool IsDashing => _dashTimeRemaining > 0f;
	public bool IsGrounded => _grounded;
	public bool IsSliding => _sliding;
	public bool IsSkating => _skating;
	public bool IsSprinting => _sprinting;
	public bool IsDead => _health <= 0f;
	public EWaterState WaterState => _waterState;
	// IActionActor — surfaces the swim state through a flat bool so action
	// requirements don't take a hard dependency on EWaterState.
	public bool IsSwimming => _waterState == EWaterState.Swimming;
	public World World => _world;
	public Inventory Inventory => _inventory;
	public IReadOnlyDictionary<LanguageData, ELanguageComponents> LearnedLanguages => _learnedLanguages;
	// Returns the components the player has learned for `language`. Null
	// language is treated as universally readable — All. Otherwise, an
	// unseen language returns None.
	public ELanguageComponents GetLearnedComponents(LanguageData language)
	{
		if (language == null) { return ELanguageComponents.All; }
		return _learnedLanguages.TryGetValue(language, out var c) ? c : ELanguageComponents.None;
	}
	public bool HasLearnedLanguage(LanguageData language) => GetLearnedComponents(language) == ELanguageComponents.All;
	// OR `components` into the player's learned-set for `language`. Returns
	// true only when this call newly added at least one component bit — used
	// by stones / consumables to gate the one-shot firstLearnEffect. Fires
	// onLanguageLearned with the bits that were NEWLY added (combined &
	// ~existing) so listeners can describe exactly what was gained ("Vyeshal
	// Grammar" rather than the player's full known set).
	public Action<LanguageData, ELanguageComponents> onLanguageLearned;
	public bool LearnLanguageComponents(LanguageData language, ELanguageComponents components)
	{
		if (language == null || components == ELanguageComponents.None) { return false; }
		_learnedLanguages.TryGetValue(language, out var existing);
		ELanguageComponents combined = existing | components;
		if (combined == existing) { return false; }
		ELanguageComponents added = combined & ~existing;
		_learnedLanguages[language] = combined;
		onLanguageLearned?.Invoke(language, added);
		return true;
	}
	public ActionRunner Runner => _runner;
	public float Health => _health;
	public float MaxHealth => (data?.maxHealth ?? 100f) + ComposeStat(EStat.MaxHealth);
	public float Armor => _armor;
	public float MaxArmor => _maxArmor + ComposeStat(EStat.MaxArmor);
	public float Stamina => _stamina;
	public float MaxStamina => (data?.maxStamina ?? 0f) + ComposeStat(EStat.MaxStamina);
	public IReadOnlyList<StatusEffectState> StatusEffects => _statusEffects.StatusEffects;

	// Save/load passthroughs for the per-effect buildup meters — the only
	// status-effect state currently serialized. Active StatusEffectState
	// instances (per-stack expiry, etc.) aren't covered here; that's a
	// separate concern with its own format. Item-side controllers (per-armor
	// wetness, etc.) likewise need inventory serialization to plug in.
	public IEnumerable<(StatusEffectData data, float amount)> EnumerateStatusBuildupsForSave() =>
		_statusEffects.EnumerateBuildupsForSave();

	public void RestoreStatusBuildups(IReadOnlyList<(StatusEffectData data, float amount)> entries) =>
		_statusEffects.RestoreBuildups(entries);

	// Fill `dst` with a snapshot of the player's per-effect buildup meters
	// (only entries with amount > 0). Forwards straight through to the
	// controller — Hud uses this each frame to render the buildup bar
	// alongside the active-effect strip without allocating.
	public void FillStatusEffectBuildups(Dictionary<StatusEffectData, float> dst) =>
		_statusEffects.FillBuildupSnapshot(dst);

	public IInteractive HighlightInteractive => _highlightInteractive;
	public IInteractive CurInteractive => _curInteractive;
	public int CurInteractiveActionIndex => _curInteractiveActionIndex;

	// True while the player is riding a vehicle. Suppresses on-foot locomotion
	// and gates the interactive-detect / board path so a mounted player can't
	// board a second vehicle.
	public bool IsMounted => _mount != null;

	// The rider's camera-relative steering intent, exposed so a mounted vehicle
	// can read it from its own _PhysicsProcess (see IRideable / Boat). Same
	// vector the on-foot path uses; the vehicle decides how to apply it.
	public Vector3 MountMoveInput => _inputMove;

	// Board a rideable. Called from the vehicle's IInteractive.Complete (the
	// Board action's OpenInteractive event) — i.e. from inside the runner tick
	// during _PhysicsProcess, so the actual reparent (a physics-tree mutation)
	// is deferred to the idle frame boundary via AttachToMount.
	public void Mount(IRideable vehicle)
	{
		if (vehicle == null || _mount != null)
		{
			return;
		}
		_mount = vehicle;
		_preMountParent = GetParent();
		// Drop transient locomotion so dismount resumes from a clean slate.
		Velocity = Vector3.Zero;
		_dashTimeRemaining = 0f;
		_skating = false;
		_skidding = false;
		_sneaking = false;
		SetCurInteractive(null);
		_highlightInteractive = null;
		onHighlightChanged?.Invoke(null);
		vehicle.OnMounted(this);
		Callable.From(AttachToMount).CallDeferred();
	}

	private void AttachToMount()
	{
		if (_mount?.SeatAnchor == null)
		{
			return;
		}
		// keepGlobalTransform:false leaves the local transform as-is; we then
		// zero it so the rider sits exactly on the seat anchor and faces the
		// vehicle's forward.
		Reparent(_mount.SeatAnchor, keepGlobalTransform: false);
		Position = Vector3.Zero;
		Rotation = Vector3.Zero;
	}

	// Leave the current vehicle and drop onto the nearest shore. Called from
	// ProcessInput (which runs in _Process, not the physics flush), so the
	// reparent is safe to do inline here.
	public void Dismount()
	{
		if (_mount == null)
		{
			return;
		}
		IRideable vehicle = _mount;
		Vector3 dropPos = vehicle.GetDismountPosition();
		_mount = null;
		vehicle.OnDismounted(this);

		Node parent = (_preMountParent != null && IsInstanceValid(_preMountParent))
			? _preMountParent
			: GetParent();
		_preMountParent = null;
		if (parent != null && parent != GetParent())
		{
			Reparent(parent, keepGlobalTransform: false);
		}
		GlobalPosition = dropPos;
		Rotation = Vector3.Zero;
		Velocity = Vector3.Zero;
		_grounded = false;
	}

	// Emergency dismount used when the vehicle itself is being freed (its
	// origin chunk evicted out from under a long voyage) — hand the rider back
	// to the world so freeing the parented vehicle doesn't free the player too.
	// Mirrors Dismount minus the vehicle-side calls (the vehicle is mid-
	// teardown). Pure tree move + state reset — no spawning, safe from the
	// vehicle's _ExitTree.
	public void ForceDismount(Node fallbackParent, Vector3 pos)
	{
		if (_mount == null)
		{
			return;
		}
		_mount = null;
		Node parent = (_preMountParent != null && IsInstanceValid(_preMountParent) && _preMountParent.IsInsideTree())
			? _preMountParent
			: fallbackParent;
		_preMountParent = null;
		if (parent != null && IsInstanceValid(parent) && parent.IsInsideTree())
		{
			Reparent(parent, keepGlobalTransform: false);
			GlobalPosition = pos;
		}
		Rotation = Vector3.Zero;
		Velocity = Vector3.Zero;
		_grounded = false;
	}

	// Minimal per-frame upkeep while mounted: keep status effects ticking and
	// drive the seated animation loop. All locomotion, gravity, water, and
	// collision are owned by the vehicle (the rider rides its transform).
	private void TickMounted(float dt)
	{
		_statusEffects?.Tick(dt);
		UpdateAnimation();
	}

	// Drop any highlighted or current interactive without going through the
	// proximity-detect path. Modal screens (merchant, etc.) that take focus
	// while the player is still standing next to the NPC call this so the
	// interact HUD and highlight overlay don't persist underneath the modal.
	// ProcessInput is gated off by GameClient.InputSuppressed while the modal
	// is open, so UpdateHighlightInteractive doesn't re-detect underneath;
	// the next physics frame after close re-evaluates from scratch.
	public void ClearInteractive()
	{
		if (_curInteractive != null)
		{
			SetCurInteractive(null);
		}
		if (_highlightInteractive != null)
		{
			_highlightInteractive = null;
			onHighlightChanged?.Invoke(null);
		}
	}

	// Hold-to-open-options state. InteractHUD reads InteractHoldProgress to
	// fill its hold bar; it subscribes to onInteractMenuOpenRequested to pop
	// the modal options panel and calls CloseInteractMenu when it dismisses.
	// While InteractMenuOpen, ProcessInput skips the press/release path so
	// the same Interact button can re-confirm a selection without firing a
	// stale tap-start.
	public float InteractHoldProgress { get; private set; }
	public bool InteractMenuOpen { get; private set; }
	public Action onInteractMenuOpenRequested;
	bool _interactPressActive;
	ulong _interactHoldStartMs;
	const ulong InteractHoldDurationMs = 500;

	public void CloseInteractMenu()
	{
		InteractMenuOpen = false;
		InteractHoldProgress = 0f;
		_interactPressActive = false;
	}
	// HUD progress fill while the runner is driving an interactive action.
	// Reads directly off the in-flight PlayerAction so the bar reflects what
	// the runner is actually doing — no separate timer to keep in sync.
	public float ClientInteractProgress
	{
		get
		{
			if (_runner == null || !_runner.IsBusy)
			{
				return 0f;
			}
			ref readonly PlayerAction action = ref _runner.Current;
			if (action.interactiveAction == null || _world == null)
			{
				return 0f;
			}
			ulong total = action.endMs > action.activateMs ? action.endMs - action.activateMs : 0;
			if (total == 0)
			{
				return 0f;
			}
			ulong now = _world.GameTimeMs;
			ulong elapsed = now > action.activateMs ? now - action.activateMs : 0;
			return Mathf.Clamp((float)elapsed / total, 0f, 1f);
		}
	}

	Vector3 _inputMove = Vector3.Zero;
	Vector3 _inputLook = Vector3.Zero;

	void SetCurInteractive(IInteractive value, int actionIndex = 0)
	{
		if (_curInteractive != value || _curInteractiveActionIndex != actionIndex)
		{
			_curInteractive = value;
			_curInteractiveActionIndex = value != null ? actionIndex : 0;
			onInteractChanged?.Invoke(value);
		}
	}


	public override void _Ready()
	{
		CollisionLayer = (uint)ECollisionLayer.Player;
		// Player does NOT physically collide with mobs — running into a mob
		// must never slow or deflect the player. The reaction (mob lurches
		// out of the way) is applied in PushTouchedMobs via an overlap
		// query against MobSpatialHash, not through MoveAndSlide contacts.
		CollisionMask = (uint)ECollisionLayer.Solid;

		// Setting current=true in the .tscn is unreliable when a Camera3D
		// is also in the tree — Godot picks the camera as listener. Force
		// the override explicitly so positional audio is heard from the
		// player's position rather than the (far-away isometric) camera.
		_audioListener?.MakeCurrent();

		interactArea.AreaEntered += OnInteractAreaEntered;
		interactArea.AreaExited += OnInteractAreaExited;

		if (_hurtBox != null)
		{
			_hurtBox.OnHit = OnHurtBoxHit;
			_hurtBox.GetHitType = GetHitType;
		}

		_aimingReticle?.Initialize(this);
	}

	// Resolve the base-model package scene for a gender. Falls back to the
	// Female entry when the gender has no authored package, so the player always
	// has a body. Returns null only if the map is empty.
	private PackedScene ResolveGenderPackage(EGender gender)
	{
		if (_modelPackages == null)
		{
			return null;
		}
		if (_modelPackages.TryGetValue((int)gender, out PackedScene scene) && scene != null)
		{
			return scene;
		}
		_modelPackages.TryGetValue((int)EGender.Female, out PackedScene fallback);
		return fallback;
	}

	// Instance the spawned gender's model package as a child of the player and
	// bind its drivers (animator + held-item socket). Only one rig is ever built.
	private void SpawnModelPackage(EGender gender)
	{
		PackedScene packageScene = ResolveGenderPackage(gender);
		if (packageScene == null)
		{
			return;
		}
		_modelPackageInstance = packageScene.Instantiate<PlayerModelPackage>();
		AddChild(_modelPackageInstance);
		_animator = _modelPackageInstance.animator;
		_heldVisual = _modelPackageInstance.heldVisual;
	}

	// Show the instanced model and wire its drivers. Deferred to Initialize (not
	// _Ready) because the gender that selects the base model only arrives with
	// PlayerSpawnData. GameClient calls Initialize synchronously right after
	// instantiating the scene, before any frame is processed, so there's no
	// window where the player renders unselected.
	private void ActivateVisual()
	{
		if (_animator == null)
		{
			return;
		}
		_animator.SetActive(true);
		_activeVisual = _animator.visual;
		// Footfalls fire from a Call Method Track authored on the model's
		// movement clips (OnFootstep) at the exact foot-contact frame.
		_animator.OnFootstep += EmitFootstep;
		// Validate the base set's clip strings against the live library now
		// that the animator (and its library) exist. Weapon sets validate
		// lazily the first time they're wielded.
		ValidateAnimSet(data?.baseAnims, "base/unarmed");
	}

	// Spawn one footstep + footprint at the current foot position, fired from
	// the model's foot-contact method track. State gates the spawn: skip while
	// ungrounded, swimming, or interacting; route to the shallow-water splash
	// variant while wading.
	private void EmitFootstep()
	{
		if (_world == null)
		{
			return;
		}
		if (!_grounded || _waterState == EWaterState.Swimming || _curInteractive != null
			|| (_runner != null && _runner.LocksMovement) || _birdsEye)
		{
			return;
		}
		Vector3 pos = GlobalPosition;
		EGroundType ground = GroundTypeResolver.Resolve(_world.WorldState, pos);
		if (_waterState == EWaterState.Shallow)
		{
			FootstepEmitter.Emit(_world, pos, _shallowWaterFootstepFx);
		}
		else
		{
			FootstepEmitter.Emit(_world, pos, ground, _footstepEffects);
			float fpAlphaMul = _statusEffects?.FoldStat(EStat.FootprintAlpha, 1f) ?? 1f;
			float fpDurMul = _statusEffects?.FoldStat(EStat.FootprintDuration, 1f) ?? 1f;
			FootprintEmitter.Emit(_world, pos, GlobalRotation.Y, ground, _footprintTexture, _footprintSize, fpAlphaMul, fpDurMul, gated: false);
		}
	}

	// Pure prediction — no state mutation. See Mob.GetHitType for the
	// networked-play motivation.
	private EHitResult GetHitType(HitInfo hit)
	{
		// Receiver-side resistance fold. ApplyResistance scales healthDamage,
		// pierce chance, blunt mult, and knockback magnitude in place using
		// the diverse-site rules so the prediction below matches the actual
		// apply in OnHurtBoxHit.
		ApplyResistance(ref hit);
		if (hit.healthDamage <= 0f)
		{
			return EHitResult.None;
		}
		if (_armor > 0f && !hit.Pierced)
		{
			return EHitResult.Armor;
		}
		if (_health <= 0f)
		{
			return EHitResult.None;
		}
		return hit.healthDamage >= _health ? EHitResult.Lethal : EHitResult.Health;
	}

	// Fold receiver resistances onto the live hit in place. Damage tags
	// (Damage / Fire / Magical / Poison / Electrical / Ranged / Melee) scale
	// healthDamage; Pierce scales the bypass-chance roll; Blunt scales the
	// (1 + blunt) armor-chip multiplier; Knockback scales knockbackDistance
	// and knockbackTime. Each site only applies if the hit carries the
	// corresponding tag — a non-Pierce hit is unaffected by Pierce-resist,
	// etc. Modulating in place means hit.Pierced and the downstream armor /
	// knockback formulas automatically pick up the receiver's resistances
	// without each call site re-asking.
	private void ApplyResistance(ref HitInfo hit)
	{
		if (hit.tags == EStat.None)
		{
			return;
		}
		EStat damageTags = hit.tags & StatModifierUtil.DamageScaleTags;
		if (damageTags != EStat.None)
		{
			hit.healthDamage *= ComposeMaskMul(damageTags);
		}
		if ((hit.tags & EStat.Pierce) != 0)
		{
			hit.pierce *= ComposeMaskMul(EStat.Pierce);
		}
		if ((hit.tags & EStat.Blunt) != 0)
		{
			hit.blunt *= ComposeMaskMul(EStat.Blunt);
		}
		if ((hit.tags & EStat.Knockback) != 0)
		{
			float scale = ComposeMaskMul(EStat.Knockback);
			hit.knockbackDistance *= scale;
			hit.knockbackTime *= scale;
		}
	}

	private void OnHurtBoxHit(HitInfo hit)
	{
		if (CVars.invulnerable.Value)
		{
			return;
		}
		// Fold receiver resistances into the hit (damage / pierce-chance /
		// blunt mult / knockback magnitude) before any side effect fires.
		// A {Damage, 0} modifier on an active dash i-frame status drops
		// healthDamage to 0 here, and the early-return below skips interrupt
		// / sneak side-effects so a dashing player isn't disturbed by a hit
		// that did nothing.
		ApplyResistance(ref hit);
		float incomingDamage = hit.healthDamage;
		if (incomingDamage <= 0f && hit.statusEffects == null && hit.buildups == null)
		{
			return;
		}

		// Capture the charging weapon's guard BEFORE TryInterrupt — a weapon
		// authored to interrupt-on-damage would otherwise leave Charging here
		// and the hit that ended the charge wouldn't be blocked. The guard was
		// up when the hit landed, so it catches this one and then drops.
		WeaponState blockWeapon = GetChargingBlockWeapon();
		// Damage may interrupt an in-flight action (gated by profile +
		// per-tier canInterrupt). External interruption fires BEFORE damage
		// is applied so abortEvents can run on coherent pre-damage state.
		_runner?.TryInterrupt();
		// Discrete hits (anything that isn't a per-frame DoT tick) snap the
		// player out of bird's-eye view. Continuous burn / poison zones keep
		// the overlook intact so a moment of bad air doesn't repeatedly cancel
		// the fly-back-down — UNLESS the player is hidden up a climbable tree,
		// where taking damage from any source (DoT included) is the cue to
		// leave the tree.
		if (_birdsEye && (!hit.dot || _hidden))
		{
			RequestEndBirdsEye();
		}
		_sneaking = false;
		// Armor handling. Bypass-aware split: a portion of `incomingDamage`
		// skips armor entirely (discrete `Pierced` = full bypass; continuous
		// `armorBypassFraction` = partial), the rest is "absorbable" and
		// piles onto the armor chip scaled by `1 + hit.blunt`. Overflow
		// doesn't bleed into health on the absorbed portion — only the
		// pre-resolved bypass lands. Recharge timer resets ONLY when the
		// armor actually took a chip — a pure-pierce hit (continuous burn
		// with pierce=1, etc.) shouldn't extend the depletion window since
		// it never touched the armor.
		float bypassFraction = hit.Pierced ? 1f : hit.armorBypassFraction;
		float bypassed = incomingDamage * bypassFraction;
		float absorbable = incomingDamage - bypassed;
		// Weapon block armor takes the absorbable slice FIRST while the player
		// is charging a guard-bearing weapon — the held charge doubles as a
		// shield. When the guard eats the slice, only the pre-resolved bypass
		// continues past it (matching the central-armor "overflow doesn't
		// bleed" rule below). AbsorbWeaponBlock also re-arms the weapon's
		// recharge delay on any mid-charge hit, even at zero guard.
		float blockAbsorbed = AbsorbWeaponBlock(blockWeapon, ref absorbable, hit.blunt);
		if (blockAbsorbed > 0f)
		{
			incomingDamage = bypassed;
			// Guard reaction one-shot, played over the held charge pose (resolves
			// the wielded weapon's Block override; no-op if it authors none). The
			// blocking weapon is the one being charged, i.e. the wielded weapon.
			PlayOneShot(EAnimation.Block, overridesCharge: true);
		}
		float armorAbsorbed = 0f;
		if (_armor > 0f && absorbable > 0f)
		{
			float armorDamage = absorbable * (1f + hit.blunt);
			float armorBefore = _armor;
			_armor = Mathf.Max(0f, _armor - armorDamage);
			armorAbsorbed = armorBefore - _armor;
			ulong now = _world?.GameTimeMs ?? 0;
			if (_armor <= 0f && armorDamage > 0f)
			{
				_armorDepleted = true;
				_armorRechargeStartMs = now + (ulong)(data.armorRecoverTime * 1000f);
				SpawnWorldEffect(_armorDepletedFx);
			}
			else
			{
				_armorDepleted = false;
				_armorRechargeStartMs = now + (ulong)(data.armorRechargeDelay * 1000f);
			}
			_armorRecharging = false;
			incomingDamage = bypassed;
		}

		bool wasAlive = _health > 0f;
		_health = Mathf.Max(0f, _health - incomingDamage);
		if (_health <= 0f)
		{
			// Death blood + VO are fired on the alive→dead transition only —
			// a follow-up hit on an already-dead body shouldn't re-emit.
			if (wasAlive)
			{
				SpawnWorldEffect(_deathFx);
				SpawnWorldEffect(_deathVoFx);
				HandleDeath();
			}
			PlayOneShot(EAnimation.Die);
		}
		// Per-hit blood / hurt VO. Suppressed for continuous DoT hits (the
		// owning DamageZone pulses these on its own fxIntervalSeconds via
		// OnHurtBoxFxPulse) so a smear-damage zone doesn't spawn a fresh
		// blood spurt every physics frame.
		else if (incomingDamage > 0f && !hit.dot)
		{
			SpawnWorldEffect(_bloodDamageFx);
			SpawnWorldEffect(_hurtVoFx);
			// Slight camera shake on actual health damage. Shares the !hit.dot
			// gate with blood/VO so a continuous burn zone doesn't sustain
			// shake every frame; range=0 since the player IS the camera target.
			GameCamera.Current?.Shake?.AddImpulse(0.12f, 0.15f, GlobalPosition, 0f, GlobalPosition);
		}

		// Floating-number HUD feedback. Armor chip and pierced health damage
		// both show — total = whatever the bar actually moved (capped by what
		// armor / health had to give). DoT hits route into the per-second
		// accumulator so a fast-ticking burn / poison zone emits one rolled-up
		// number per second; single hits fire onDamage immediately.
		float totalShown = blockAbsorbed + armorAbsorbed + Mathf.Max(0f, incomingDamage);
		if (totalShown > 0f)
		{
			if (hit.dot)
			{
				_dotHud.AddDamage(totalShown);
			}
			else
			{
				GameClient client = GameClient.Current;
				client?.onDamage?.Invoke(GlobalPosition, totalShown, EHudTextType.DamageLight);
				client?.FlashDamage(totalShown);
			}
		}

		if (hit.statusEffects != null)
		{
			for (int i = 0; i < hit.statusEffects.Count; i++)
			{
				AddStatusEffect(hit.statusEffects[i]);
			}
		}

		// Buildup contributions — funnel each entry into the receiver's per-
		// effect meter and fold any applyTrigger from a crossed threshold back
		// onto the hit before hitstun/knockback resolution so an OnDizzy
		// modifier can amplify those reads on the same hit that landed dizzy.
		_statusEffects?.ApplyHitBuildups(ref hit);

		// Hitstun + knockback: latch the flinch + knockback windows so
		// per-frame ticks can count them down. Direction comes from the
		// sender via HitInfo.hitDirection; a zero direction drops knockback
		// entirely regardless of distance. Death overrides the hitstun anim
		// because the Die one-shot above latches first.
		if (hit.hitstun > 0f && _health > 0f)
		{
			_hitstunTime = Mathf.Max(_hitstunTime, hit.hitstun);
			PlayOneShot(EAnimation.Hitstun);
		}
		if (hit.knockbackDistance > 0f && hit.knockbackTime > 0f && hit.hitDirection != Vector3.Zero && _health > 0f)
		{
			Vector3 dir = hit.hitDirection;
			dir.Y = 0f;
			if (dir.LengthSquared() > 0.0001f)
			{
				// Constant-velocity knockback: distance/time gives the m/s
				// the body holds during the window so it covers exactly
				// `distance` meters in `time` seconds. _PhysicsProcess
				// forces this onto Velocity.X/Z each tick (overriding the
				// input-driven rebuild) and the trailing edge in TickHitstun
				// snaps horizontal back to zero so the body stops cleanly.
				float speed = hit.knockbackDistance / hit.knockbackTime;
				_knockbackVelocity = dir.Normalized() * speed;
				_knockbackTime = Mathf.Max(_knockbackTime, hit.knockbackTime);
			}
		}
	}

	// One-shot effect parented to World so it stays put as the player
	// continues to move (matching the footstep / ripple convention). Silently
	// no-ops when scene is unset or before Initialize has wired _world.
	private void SpawnWorldEffect(PackedScene scene)
	{
		if (scene == null || _world == null)
		{
			return;
		}
		Fx.Create(scene, _world, GlobalPosition);
	}

	// One-shot effect parented to the player (local origin) so its audio +
	// particles track the body as it keeps moving — used for self-anchored
	// cues like the out-of-breath pant, as opposed to SpawnWorldEffect which
	// leaves the effect behind in world space.
	private void SpawnSelfEffect(PackedScene scene)
	{
		if (scene == null)
		{
			return;
		}
		Fx.Create(scene, this, Vector3.Zero);
	}

	// Drives a loop's lifetime from a "should be active" flag. When `active`
	// flips true and we don't already own an instance, instantiate parented
	// to the player so the loop tracks the body. When it flips false, Stop()
	// the existing instance — it cleans itself up after the trailing audio +
	// particles wind down — and drop our reference so the next activation
	// gets a fresh node.
	private void UpdateLoopEffect(ref Fx instance, PackedScene scene, bool active)
	{
		if (active)
		{
			if (instance == null && scene != null)
			{
				instance = Fx.Create(scene, this, Vector3.Zero);
			}
		}
		else if (instance != null)
		{
			instance.Stop();
			instance = null;
		}
	}

	// Slide-loop driver with per-ground-type scene selection. Resolves the
	// current EGroundType each tick and swaps the active Fx wholesale when
	// the surface type changes mid-slide (e.g. skating from grass onto
	// stone). Missing dictionary entries silently emit nothing for that
	// ground type so a partially-authored player.tscn still works on the
	// surfaces it covers.
	private void UpdateSlideLoop(bool active)
	{
		PackedScene target = null;
		if (active && _world != null && _slideLoopFx != null)
		{
			EGroundType ground = GroundTypeResolver.Resolve(_world.WorldState, GlobalPosition);
			_slideLoopFx.TryGetValue(ground, out target);
		}
		if (target == _slideLoopScene)
		{
			return;
		}
		if (_slideLoop != null)
		{
			_slideLoop.Stop();
			_slideLoop = null;
		}
		if (target != null)
		{
			_slideLoop = Fx.Create(target, this, Vector3.Zero);
		}
		_slideLoopScene = target;
	}

	// One-shots (attack, die, jump) latch the resolved clip and let the animator
	// drive itself to completion — Finished flips because these anims are authored
	// with loop=false. While a one-shot is latched, UpdateAnimation defers; once
	// Finished (or the animator gets reassigned by something else) we clear the
	// latch and resume the state-driven loop pick.
	// `overridesCharge` keeps the one-shot playing over a held charge pose (the
	// block reaction passes true); everything else yields to a charge. Resolves
	// through AnimName, so a wielded weapon's override for the slot wins and an
	// unmapped / missing clip falls back silently.
	public void PlayOneShot(EAnimation anim, bool overridesCharge = false)
	{
		if (_animator == null || data == null)
		{
			return;
		}
		StringName name = AnimName(anim);
		if (name == default || !_animator.HasAnimation(name))
		{
			return;
		}
		_oneShotClip = name;
		_oneShotIsHitstun = anim == EAnimation.Hitstun;
		_oneShotOverridesCharge = overridesCharge;
		_animator.Play(name);
	}

	// Resolve an EAnimation slot to a clip name, preferring the wielded weapon's
	// override and falling back to the unarmed clip. The single chokepoint that
	// makes the whole standard anim set per-weapon overridable with an automatic
	// unarmed fallback — a weapon set leaves a slot blank (or names a clip the
	// active animator lacks) and the unarmed clip is used.
	private StringName AnimName(EAnimation anim)
	{
		WeaponAnimSet set = _wieldedWeapon?.data?.animSet;
		if (set != null)
		{
			StringName ov = set.GetOverride(anim);
			if (ov != default && _animator != null && _animator.HasAnimation(ov))
			{
				return ov;
			}
		}
		return data != null ? data.GetAnimationName(anim) : default;
	}

	// Load-time validation: each WeaponAnimSet's clip strings must exist in the
	// live animation library. Deduped so a set is checked once (the first time
	// it's used / wielded). Missing clips are logged, not fatal.
	private readonly System.Collections.Generic.HashSet<WeaponAnimSet> _validatedAnimSets = new();
	private void ValidateAnimSet(WeaponAnimSet set, string label)
	{
		if (set == null || _animator == null || !_validatedAnimSets.Add(set))
		{
			return;
		}
		set.Validate(_animator.HasAnimation, label);
	}

	// EAnimation charge slot for the in-flight weapon charge, or null when the
	// player isn't charging a weapon. Tier (selectedTierIndex, clamped at 1)
	// picks Charge1 vs Charge2; locomotion picks Idle / Walk / Run on the
	// 75%-of-run-speed split (standing → Idle, moving below → Walk, at/above →
	// Run, so a slowing charge stays in Walk). The slot resolves to a clip
	// through AnimName like every other state — one path.
	const float ChargeRunSpeedFraction = 0.75f;
	private EAnimation? WeaponChargeSlot(float speedSq, bool intentMoving)
	{
		if (_runner == null || !_runner.IsBusy
			|| _runner.Phase != EActionPhase.Charging
			|| _runner.Current.context.primaryItem is not WeaponState)
		{
			return null;
		}
		bool heavy = _runner.Current.selectedTierIndex >= 1;
		bool moving = intentMoving || speedSq > MoveLoopEnterSpeedSq;
		if (!moving)
		{
			return heavy ? EAnimation.Charge2Idle : EAnimation.Charge1Idle;
		}
		float runThreshold = (data?.moveSpeed ?? 0f) * ChargeRunSpeedFraction;
		if (speedSq >= runThreshold * runThreshold)
		{
			return heavy ? EAnimation.Charge2Run : EAnimation.Charge1Run;
		}
		return heavy ? EAnimation.Charge2Walk : EAnimation.Charge1Walk;
	}

	private void UpdateAnimation()
	{
		if (_animator == null || data == null)
		{
			return;
		}
		// Default the animator back to authored speed every tick — the
		// movement-loop branch below re-enables status retiming when (and only
		// when) it picks a speed-scaled loop. One-shots (attack, hitstun, jump,
		// die) take the early return below, so this default sticks for them.
		_animator.effectSpeedMultiplier = 1f;

		// Track airborne dwell time. Cleared the instant we hit ground so the
		// next lift-off starts a fresh grace window. Running up a slope tends
		// to lose floor contact for a frame or two between step-up cycles, and
		// without this the player flickers to "fall" each time.
		if (_grounded)
		{
			_airborneStartMs = 0;
		}
		else if (_airborneStartMs == 0 && _world != null)
		{
			_airborneStartMs = _world.GameTimeMs;
		}

		// Any held charge (consumable or weapon) clears a stale, non-priority
		// one-shot so the charge pose shows immediately; a block reaction
		// (overridesCharge) survives to play over the charge.
		bool chargingNow = _runner != null && _runner.IsBusy
			&& _runner.Phase == EActionPhase.Charging;
		if (chargingNow && !_oneShotOverridesCharge)
		{
			_oneShotClip = default;
		}

		// Movement-locked consumable / scroll held pose (drink / eat / read).
		// chargeEvents PlayAnim fires PlayOneShot on press; a looping clip never
		// reports Finished, so this override clears the stale latch and lets the
		// loop pick swap back to Idle/Run the instant Charging ends. Weapon
		// charges take the Charge* slot branch in the loop pick below instead;
		// weapon ATTACKS fire through their tier's PlayAnim event (animName =
		// Attack / Attack2) like any other timeline one-shot.
		EAnimation? chargeAnimOverride = null;
		if (chargingNow && _runner.LocksMovement
			&& _runner.Current.context.primaryItem is not WeaponState)
		{
			chargeAnimOverride = ResolveChargeAnim(_runner.Current.profile);
		}

		if (_oneShotClip != default)
		{
			// Hitstun is gated solely by _hitstunTime — when the timer hits zero
			// the latch releases regardless of the clip's loop flag or Finished
			// state, so a looping hitstun clip doesn't trap the player past the
			// flinch window. Other one-shots hold while the animator says the
			// clip is still playing.
			if (_oneShotIsHitstun)
			{
				if (_hitstunTime > 0f)
				{
					return;
				}
				_oneShotClip = default;
			}
			else
			{
				if (_animator.CurrentAnimation == _oneShotClip && !_animator.Finished)
				{
					return;
				}
				_oneShotClip = default;
			}
		}

		EAnimation loopAnim;
		// Horizontal speed only — vertical motion belongs to fall/jump/grav,
		// not to the run-vs-idle decision. While stepping up a slope the body
		// briefly leaves the floor and Velocity.Y from gravity dominates the
		// 3D length, which used to flip the pick to "run" for a frame and
		// then back to "idle" once we re-grounded.
		Vector3 horizVel = new(Velocity.X, 0f, Velocity.Z);
		float speedSq = horizVel.LengthSquared();
		// "Wants to move" includes input even when blocked by a wall —
		// otherwise pushing into geometry zeroes Velocity and snaps us back to
		// idle while the player is visibly trying to run.
		bool intentMoving = _inputMove.LengthSquared() > 0.0001f;
		bool fallReady = !_grounded
			&& _airborneStartMs != 0
			&& _world != null
			&& _world.GameTimeMs - _airborneStartMs >= FallGraceMs;
		if (_health <= 0f)
		{
			loopAnim = EAnimation.Dead;
		}
		else if (_mount != null)
		{
			// Seated on a vehicle: paddle-rest vs paddle-stroke per the mount's
			// propulsion state. The vehicle owns the body transform, so locomotion
			// speed / ground state below are irrelevant here.
			loopAnim = _mount.IsPropelling ? _mount.MoveAnim : _mount.IdleAnim;
		}
		else if (chargeAnimOverride.HasValue)
		{
			loopAnim = chargeAnimOverride.Value;
		}
		else if (WeaponChargeSlot(speedSq, intentMoving) is EAnimation chargeSlot)
		{
			// Holding a weapon charge: the Charge* slot (tier x locomotion) takes
			// priority over normal locomotion and resolves to the weapon's clip
			// through AnimName, same as every other slot.
			loopAnim = chargeSlot;
		}
		else if (_curInteractive != null)
		{
			// Interaction holds the player still (movement speed is forced to
			// 0 above) — show the interaction loop regardless of water/ground
			// state until the action completes or is cancelled.
			loopAnim = EAnimation.Interacting;
		}
		else if (_waterState == EWaterState.Swimming)
		{
			// Sprint underwater swaps the moving variant only — the idle pose
			// is the same whether or not Dash is held. (Holding Dash while
			// idle in water is still "sprint intent" per UpdateSprintState,
			// but visually there's nothing to differentiate from a normal
			// tread until the player starts moving.)
			EAnimation swimMove = _sprinting ? EAnimation.SwimSprint : EAnimation.Swim;
			loopAnim = PickMoveLoop(speedSq, intentMoving, swimMove, EAnimation.SwimIdle);
		}
		else if (_dashTimeRemaining > 0f)
		{
			// Still dashing after the Dash one-shot anim has finished. The dash
			// velocity sits far from the input-target velocity, which would put
			// the skid/skate branch below in charge and show Skating -- but a
			// dash should read as a hard run, so fall back to Sprint until the
			// dash ends and normal loop selection resumes. (Water dashes are
			// already handled by the swimming branch above.)
			loopAnim = EAnimation.Sprint;
		}
		else if (_skating || _skidding)
		{
			// Skate anim wins over fall — on a steep slope _grounded is false
			// and the airborne grace would otherwise flip the model to the
			// fall pose every tick the skate ticks past FallGraceMs. Also
			// fires for grounded skids (sharp direction changes), so the
			// player visibly slides their feet during sharp turns at speed.
			loopAnim = EAnimation.Skating;
		}
		else if (fallReady)
		{
			loopAnim = EAnimation.Fall;
		}
		else if (_sneaking)
		{
			loopAnim = PickMoveLoop(speedSq, intentMoving, EAnimation.Sneak, EAnimation.SneakIdle);
		}
		else if (_sprinting)
		{
			// Sprint replaces run as the moving variant; idle stays the same
			// (sprint intent without movement is a transient state that
			// resolves to one or the other within a frame).
			loopAnim = PickMoveLoop(speedSq, intentMoving, EAnimation.Sprint, EAnimation.Idle);
		}
		else
		{
			loopAnim = PickMoveLoop(speedSq, intentMoving, EAnimation.Run, EAnimation.Idle);
		}
		// One path: every slot — locomotion, charge pose, anything — resolves
		// through AnimName, preferring the wielded weapon's override and falling
		// back to the unarmed clip.
		StringName loopName = AnimName(loopAnim);
		if (loopName != default)
		{
			_animator.Play(loopName);
		}

		// Status retiming (Cold etc.) is gated per-anim by AnimationData —
		// only loops authored with affectedBySpeedMultiplier track statusAnimMul
		// (movement anims whose underlying action is also slowed by statusMoveMul).
		// One-shots already returned above, so this branch only runs for loops.
		// Charge slots aren't mapped with that flag, so they're naturally excluded.
		if (data.IsAnimationSpeedAffected(loopAnim))
		{
			_animator.effectSpeedMultiplier = _statusEffects?.FoldStat(EStat.AnimSpeed, 1f) ?? 1f;
		}

		// Drive the anim-audio loop off the same loopAnim. Only idle / run /
		// swim_idle have audio; everything else (fall, dead, interacting,
		// active swim, weapon charge) is silent for the anim-loop layer.
		PackedScene animLoopTarget = null;
		if (_health > 0f)
		{
			if (loopAnim == EAnimation.Idle) animLoopTarget = _idleLoopFx;
			else if (loopAnim == EAnimation.Run) animLoopTarget = _runLoopFx;
			else if (loopAnim == EAnimation.SwimIdle) animLoopTarget = _swimIdleLoopFx;
		}
		UpdateAnimLoop(animLoopTarget);
	}

	// Pulls the held-pose anim out of an ItemActionProfile's chargeEvents.
	// Used by UpdateAnimation to drive movement-locked actions (consumables,
	// scrolls) as a sustained loop. Returns the first PlayAnim event's
	// animName; null when the profile has no charge anim authored.
	private static EAnimation? ResolveChargeAnim(ItemActionProfile profile)
	{
		if (profile?.chargeEvents == null)
		{
			return null;
		}
		for (int i = 0; i < profile.chargeEvents.Count; i++)
		{
			ItemEvent ev = profile.chargeEvents[i];
			if (ev != null && (ev.type & EItemEventType.PlayAnim) != 0)
			{
				return ev.animName;
			}
		}
		return null;
	}

	// Swap the active anim-loop wholesale on state change. No-op when target
	// matches the currently-playing scene, so this is safe to call every frame.
	private void UpdateAnimLoop(PackedScene scene)
	{
		if (scene == _animLoopScene)
		{
			return;
		}
		if (_animLoopFx != null)
		{
			_animLoopFx.Stop();
			_animLoopFx = null;
		}
		if (scene != null)
		{
			_animLoopFx = Fx.Create(scene, this, Vector3.Zero);
		}
		_animLoopScene = scene;
	}

	const ulong FallGraceMs = 400;

	// Inbound vertical speed (m/s, downward positive) at which a land flips
	// from soft to hard. ~10 m/s is the speed a body reaches after falling
	// just over 5 m under 9.8 m/s² — a small ledge hop won't hit it but a
	// roof-height drop will.
	const float LandHardSpeedThreshold = 10f;

	// Inbound vertical speed below which no land sound fires at all. Step-up
	// + step-down + obstacle interactions can cause sub-frame airborne flips
	// even on flat ground; this floor suppresses the resulting phantom lands.
	// Real lands (jumps, ledge drops) easily clear it — a neutral jump arc
	// returns at ~6 m/s.
	const float LandSoftSpeedThreshold = 1.5f;

	// Inbound vertical speed at which entering water flips from a wade-style
	// splash to a full plunge (deeper SFX + bigger spray). Lower than
	// LandHardSpeedThreshold because water entry tends to feel "splashy" at
	// lower speeds than a hard ground impact reads as heavy.
	const float WaterPlungeSpeedThreshold = 6f;

	// Hysteresis on the move-vs-idle pick. Crossing a single threshold every
	// frame produces twitch when the body sits near it (e.g. ground friction
	// just barely > 0.01). Two thresholds with a hold-current band kill that
	// — fully stop below 0.01 m/s, commit to "moving" only above 0.1 m/s,
	// hold whatever's currently playing in between.
	const float MoveLoopEnterSpeedSq = 0.01f;     // 0.1 m/s
	const float MoveLoopExitSpeedSq = 0.0001f;    // 0.01 m/s
	private EAnimation PickMoveLoop(float speedSq, bool intentMoving, EAnimation moveAnim, EAnimation idleAnim)
	{
		// Input intent forces "moving" — keeps the run anim playing while
		// pinned against geometry, where Velocity would otherwise be ~0.
		if (intentMoving || speedSq > MoveLoopEnterSpeedSq)
		{
			return moveAnim;
		}
		if (speedSq < MoveLoopExitSpeedSq)
		{
			return idleAnim;
		}
		// Hold-current band — compare the animator's currently-playing clip
		// against each candidate's authored name to decide which side of the
		// band to stick to. Both lookups are dictionary reads, so this is
		// cheap to run every tick.
		StringName current = _animator.CurrentAnimation;
		if (current == AnimName(moveAnim))
		{
			return moveAnim;
		}
		if (current == AnimName(idleAnim))
		{
			return idleAnim;
		}
		return idleAnim;
	}

	public void Heal(float amount)
	{
		if (amount <= 0f)
		{
			return;
		}
		// Healing climbs all the way to MaxHealth regardless of any
		// outstanding blood drain — a potion brings you to full even
		// while a spell's HP debt is still pending. Any drain the heal
		// climbs into is forgiven (the invariant `Health + DrainedHealth
		// <= MaxHealth` is restored), since the bar's dark region
		// represents debt that would be repaid into bright HP — and
		// you've already paid yourself up to the cap.
		float before = _health;
		_health = Mathf.Min(MaxHealth, _health + amount);
		_drainedHealth = Mathf.Min(_drainedHealth, Mathf.Max(0f, MaxHealth - _health));
		float restored = _health - before;
		if (restored > 0f)
		{
			GameClient.Current?.onHeal?.Invoke(GlobalPosition, restored, EHudTextType.HealLight);
		}
	}

	// IActionActor — press-time blood gate. Non-mutating peek. Costs of 0
	// or less always pass; otherwise refuses when the cost would drop HP
	// to 0, so a drain can never kill the actor directly.
	public bool HasBlood(float amount)
	{
		if (amount <= 0f)
		{
			return true;
		}
		return _health > amount;
	}

	// IActionActor — unconditional spend at EnterActive. Subtracts from
	// current HP, adds to _drainedHealth, and re-arms the single shared
	// regen delay (PlayerData.bloodRegenDelay). Mirrors armor: every
	// drain pushes _bloodRegenStartMs forward so chained spells hold
	// regen back until the player stops drawing.
	public void DrainBlood(float amount)
	{
		if (amount <= 0f || data == null)
		{
			return;
		}
		_health -= amount;
		_drainedHealth += amount;
		ulong now = _world?.GameTimeMs ?? 0;
		_bloodRegenStartMs = now + (ulong)(data.bloodRegenDelay * 1000f);
	}

	// Per-tick refund. No-op while _drainedHealth is empty or before the
	// shared delay elapses; otherwise pays back bloodRegenSpeed * dt to
	// _health and shrinks _drainedHealth by the same amount so the bright
	// and dark HUD zones meet seamlessly.
	private void TickBloodDrain(float dt)
	{
		if (_drainedHealth <= 0f || data == null)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (now < _bloodRegenStartMs)
		{
			return;
		}
		float refund = Mathf.Min(_drainedHealth, data.bloodRegenSpeed * dt);
		_drainedHealth -= refund;
		_health = Mathf.Min(MaxHealth, _health + refund);
	}

	// Hard teleport. Zeros velocity and floods the safe-grounded history so
	// the stuck-recovery can't yank the player back to a pre-teleport position
	// once the buffer rolls. Used by the ruby slippers' return-to-spawn effect.
	public void TeleportTo(Vector3 position)
	{
		GlobalPosition = position;
		Velocity = Vector3.Zero;
		for (int i = 0; i < SafeGroundedHistorySize; i++)
		{
			_safeGroundedHistory[i] = position;
		}
		_safeGroundedHistoryWriteIdx = 0;
		_lastTickPosition = position;
		_stuckCheckDeadlineMs = 0;
	}

	// Append a fresh state for `data`. Multiple instances of the same data
	// are intentional — the HUD shows them as one icon with a count, and each
	// instance ticks independently. Returns the new state so the caller (e.g.
	// the wet-after-swim trigger) can hold a handle and arm the timer later.
	public StatusEffectState AddStatusEffect(StatusEffectData data) => _statusEffects.Add(data);

	public void RemoveStatusEffect(StatusEffectState state) => _statusEffects.Remove(state);

	public void RemoveStatusEffectsByTagMask(EStat mask) => _statusEffects.RemoveByTagMask(mask);

	// WarmthZone (campfires, etc.) calls these on body enter/exit. Counter,
	// not bool, so two campfires whose zones overlap don't release the player
	// from one when they leave the other. Entering accelerates the wetness
	// decay (PlayerData.wetnessWarmthDrySeconds) — a player walking up to
	// a fire dries off in seconds rather than minutes, and the wet status
	// releases naturally once wetness falls below the disarm threshold. The
	// zone's warmingTemperature is summed into _warmthBonus so
	// SampleEnvironmentTemperature can stack heat from multiple overlapping
	// fires.
	public void EnterWarmthZone(WarmthZone zone)
	{
		_warmthZoneCount++;
		if (zone != null)
		{
			_warmthBonus += zone.warmingTemperature;
		}
	}

	public void ExitWarmthZone(WarmthZone zone)
	{
		if (_warmthZoneCount > 0)
		{
			_warmthZoneCount--;
			if (zone != null)
			{
				_warmthBonus -= zone.warmingTemperature;
			}
		}
	}

	// Per-physics-tick wet driver. Routes environmental wetness signals into
	// the player's Wet buildup meter (the controller arms / disarms via
	// armThreshold / disarmThreshold), AND ticks the same signals through
	// each equipped armor's own wetness meter (ArmorState.wetness). Equipped
	// armor wetness cascades back into the player meter each tick at
	// PlayerData.wetnessArmorCascadeRate scaled by the armor's current
	// wetness — so a wet shirt keeps soaking the wearer.
	//
	// Sources, in priority order:
	//   • In water     — every meter (player + each equipped armor) snaps
	//                    to 1 the moment the player enters water.
	//   • In rain (sky-exposed, not in a warmth zone) — every meter
	//                    accumulates at 1 / wetnessRainSoakSeconds scaled
	//                    by RainIntensity.
	//   • Otherwise    — every meter drains at its OWN rate. The player's
	//                    drains at baseDryRate × ComposeStat(WetnessDryRate)
	//                    so equipped wet wool slows it; each armor drains
	//                    at baseDryRate × its own WetnessDryRate modifier.
	//                    Inside a warmth zone (campfires) the warmthRate
	//                    acts as a FLOOR — humid still days can never slow
	//                    drying below it, but a hot wind outdoors still can.
	private void TickWetEffect(float dt)
	{
		if (_wetEffectData == null || data == null)
		{
			return;
		}

		// Source classification — water beats rain beats nothing. Warmth
		// zones suppress rain accumulation entirely so the player dries off
		// at the fire even when it's raining around them; the fast warmth
		// dry rate then takes everyone down regardless of overhead
		// conditions. Water still wins over warmth — step into a stream
		// at a campfire and you're soaked.
		bool inWater = _waterState != EWaterState.None;
		bool inWarmth = _warmthZoneCount > 0;
		// Rain exposure in [0, 1]: 0 when sheltered (solid roof overhead or
		// dense enough canopy), up to 1 in fully open sky. A partial canopy
		// gives partial shelter, so rain soak scales by it.
		float rainExposure = (!inWater && !inWarmth) ? RainExposure01() : 0f;
		bool inRain = rainExposure > 0f;

		float rainAccum = 0f;
		if (inRain && data.wetnessRainSoakSeconds > 0f)
		{
			float rainIntensity = Mathf.Clamp(SkyController.Current?.Palette.RainIntensity ?? 0f, 0f, 1f);
			rainAccum = (dt / data.wetnessRainSoakSeconds) * rainIntensity * rainExposure;
		}

		// Environmental drying scalar — shared across player and armor.
		// Each consumer scales its own WetnessDryRate modifier onto this
		// neutral rate. Skipped during water / rain (you're not drying when
		// being soaked).
		float baseDryRate = 0f;
		float warmthRate = 0f;
		if (!inWater && !inRain)
		{
			GameClient client = GameClient.Current;
			float windSpeed = client != null ? client.SampleWindSpeed(GlobalPosition) : 0f;
			float airTemp = client != null ? client.SampleAirTemperature(GlobalPosition) : data.dryRateReferenceTempF;
			float humidity = SkyController.Current?.Weather?.humidity ?? 0f;
			float windMul = 1f + windSpeed * data.dryRateWindBoostPerMps;
			float humidityMul = Mathf.Clamp(1f - humidity * data.dryRateHumidityDamping, 0f, 1f);
			float tempMul = Mathf.Max(0f, 1f + (airTemp - data.dryRateReferenceTempF) * data.dryRateTempBoostPerF);
			float envFactor = windMul * humidityMul * tempMul;
			baseDryRate = data.wetnessDrySeconds > 0f ? envFactor / data.wetnessDrySeconds : 0f;
			warmthRate = inWarmth && data.wetnessWarmthDrySeconds > 0f ? 1f / data.wetnessWarmthDrySeconds : 0f;
		}

		// Item-side wetness uses the modifier-less wet-clothes status so the
		// per-armor meter never accidentally double-applies cold/heat shifts
		// (those live on _wetEffectData and only fold once, on the player
		// when their own meter arms). Falls back to _wetEffectData if no
		// clothes-side resource is wired.
		StatusEffectData armorEffect = _wetClothesEffectData ?? _wetEffectData;

		// Tick every owned armor's own buildup first — equipped pieces AND
		// anything in the backpack — so wet wool stuffed in the pack still
		// dries on the same clock as worn wool. Done before the player
		// delta so the cascade contribution below reads the freshest
		// post-rain / post-dry armor wetness.
		if (_inventory != null)
		{
			foreach (ArmorState armor in _inventory.EnumerateAllArmor())
			{
				if (armor?.data == null) { continue; }
				float armorDelta;
				if (inWater)
				{
					armorDelta = 1f - armor.statusEffects.GetBuildup(armorEffect);
				}
				else
				{
					float armorDryMul = armor.data.modifiers != null
						? StatModifierUtil.Fold(EStat.WetnessDryRate, armor.data.modifiers, 1f)
						: 1f;
					float armorDryRate = Mathf.Max(baseDryRate * armorDryMul, warmthRate);
					armorDelta = rainAccum - armorDryRate * dt;
				}
				if (armorDelta != 0f)
				{
					armor.statusEffects.AddBuildup(armorEffect, armorDelta);
				}
			}
		}

		// Player meter delta. Cascade from EQUIPPED armor feeds in regardless
		// of in-water (water already pins the player to 1; cascade then is a
		// no-op via clamp) so the path is uniform. Armor in the backpack
		// doesn't cascade — it's not in contact with the wearer's skin.
		float playerDelta;
		if (inWater)
		{
			playerDelta = 1f - _statusEffects.GetBuildup(_wetEffectData);
		}
		else
		{
			float playerDryMul = ComposeStat(EStat.WetnessDryRate);
			float playerDryRate = Mathf.Max(baseDryRate * playerDryMul, warmthRate);
			playerDelta = rainAccum - playerDryRate * dt;
			if (_inventory != null && data.wetnessArmorCascadeRate > 0f)
			{
				foreach (ArmorState armor in _inventory.EnumerateEquippedArmor())
				{
					if (armor == null) { continue; }
					playerDelta += armor.statusEffects.GetBuildup(armorEffect) * data.wetnessArmorCascadeRate * dt;
				}
			}
		}

		if (playerDelta != 0f)
		{
			_statusEffects.AddBuildup(_wetEffectData, playerDelta);
		}
	}

	// Per-physics-tick dirty driver. Mirrors TickWetEffect's per-armor model:
	// each WORN piece of armor slowly accumulates grime (over
	// PlayerData.dirtyDaysToFull game-days of wear), and the player-side Dirty
	// effect — which carries the Scent penalty and the HUD icon — tracks the
	// dirtiest worn piece via its own ContinuousArm meter.
	//
	// Washing: while a piece's wet meter is armed its grime is pinned to zero.
	// Because the wet driver soaks EVERY owned piece (worn or packed), getting
	// the player wet cleans their whole wardrobe — a garment needn't be worn
	// to be washed. There is no passive decay; grime only resets by washing.
	private void TickDirtyEffect(float dt)
	{
		if (_dirtyEffectData == null || _inventory == null || data == null)
		{
			return;
		}

		// Item-side meters key off the modifier-less clothes status (fallback
		// to the player effect if unwired) so an armor piece never arms a
		// Scent-bearing instance of its own — the penalty folds once, on the
		// player, when their meter arms.
		StatusEffectData dirtyClothes = _dirtyClothesEffectData ?? _dirtyEffectData;
		StatusEffectData wetClothes = _wetClothesEffectData ?? _wetEffectData;

		// Grime accrues in GAME-time: dirtyDaysToFull day/night cycles of wear
		// fill the 0→1 meter. One game day is DayLengthSeconds real seconds at
		// time_scale 1, so the per-real-second rate tracks the same clock (and
		// CVar) that advances the sky.
		float dayLength = _world?.WorldState?.SimData?.DayLengthSeconds ?? 600f;
		float daysToFull = Mathf.Max(data.dirtyDaysToFull, 0.0001f);
		float dirtyDelta = dayLength > 0f ? dt * CVars.timeScale.Value / (daysToFull * dayLength) : 0f;

		foreach (ArmorState armor in _inventory.EnumerateAllArmor())
		{
			if (armor?.data == null) { continue; }
			// Wet wins: a soaked piece is being washed, so pin its grime to
			// zero (a fat negative contribution clamps to 0). Applies to packed
			// pieces too, so a swim or a downpour launders everything you own.
			if (wetClothes != null && armor.statusEffects.HasActive(wetClothes))
			{
				if (armor.statusEffects.GetBuildup(dirtyClothes) > 0f)
				{
					armor.statusEffects.AddBuildup(dirtyClothes, -1f);
				}
				continue;
			}
			// Only WORN pieces pick up grime; a packed piece holds its current
			// dirtiness until it's worn again.
			if (dirtyDelta > 0f && _inventory.IsEquipped(armor))
			{
				armor.statusEffects.AddBuildup(dirtyClothes, dirtyDelta);
			}
		}

		// Drive the player-side meter to the dirtiest worn piece. Its
		// ContinuousArm thresholds switch the Scent penalty + HUD icon on once
		// a worn piece is fully grimy and off when it's washed back below the
		// disarm threshold.
		float maxWornDirty = 0f;
		foreach (ArmorState armor in _inventory.EnumerateEquippedArmor())
		{
			if (armor == null) { continue; }
			maxWornDirty = Mathf.Max(maxWornDirty, armor.statusEffects.GetBuildup(dirtyClothes));
		}
		float playerDirtyDelta = maxWornDirty - _statusEffects.GetBuildup(_dirtyEffectData);
		if (playerDirtyDelta != 0f)
		{
			_statusEffects.AddBuildup(_dirtyEffectData, playerDirtyDelta);
		}
	}

	// Surface a continuous 0..1 progress value the HUD's status-effect
	// strip can render as a fill bar, for status effects whose intensity
	// is driven by a continuous player-side state rather than a timer.
	// Returns null for effects that don't have a custom mapping (the HUD
	// falls back to its timer-based progress).
	//
	// Currently returns null for everything — Wet was the only consumer and
	// its meter is now visualized via the controller's buildup bar (same
	// shape every other ContinuousArm / ThresholdCross effect uses). Kept
	// as a hook for future effects that need a custom non-timer mapping
	// distinct from their buildup meter (e.g. a hunger / thirst bar).
	public float? GetStatusEffectProgress(StatusEffectData effectData)
	{
		_ = effectData;
		return null;
	}

	// How exposed to falling rain are we, in [0, 1]? 0 = sheltered (no
	// perceptible rain, a solid roof overhead, or dense enough tree canopy);
	// 1 = fully open sky. The wet-status path scales rain soak by this, so a
	// thin canopy soaks you slowly and a thick one keeps you dry. Water-state
	// is handled separately by the caller (it snaps wetness to 1 directly).
	//
	// Single signal: WorldState.GetSkyExposure01, the non-leaky VERTICAL sky
	// reach baked into the SkyExposure field. It already folds in solid cover
	// (a roof/overhang/cave ceiling extinguishes the column) AND tree canopy
	// density (canopy attenuates the column proportionally), so one value
	// covers both shelter sources. No physics raycast: the field is immune to
	// the horizontal light leak that would otherwise wet a player standing
	// under solid rock at a cave mouth, and it stays correct underground where
	// the ceiling lives in the chunk above.
	//
	// Mapping: fully dry at/below rainShelterSkyThreshold (a mid-range cover
	// level), ramping linearly to full soak at open sky. So a thin canopy
	// (high exposure) soaks you slowly and a moderate canopy (exposure below
	// the threshold) keeps you dry.
	//
	// Gated on a perceptible-rain floor instead of strict `> 0`. The
	// simRain → palette.RainIntensity formula is `pow(simRain, 1.25)`, and
	// simCloud clipping its rain threshold by epsilon produces a simRain
	// of ~1e-5 (displays as 0.000) that maps to a ~1e-7 RainIntensity. A
	// strict positive check keeps the rain branch active at that value,
	// so wetness never drains. RainPerceptibleFloor filters the noise.
	private const float RainPerceptibleFloor = 0.01f;

	// Height above the player's origin to sample sky exposure — chest level, so
	// the probe lands in the air voxel the player occupies rather than the
	// solid ground voxel under their feet (which would always read sheltered).
	private const float SkyExposureProbeHeight = 1.0f;

	private float RainExposure01()
	{
		SkyController sky = SkyController.Current;
		if (sky == null || sky.Palette.RainIntensity < RainPerceptibleFloor)
		{
			return 0f;
		}
		WorldState ws = World.Current?.WorldState;
		if (ws == null)
		{
			return 0f;
		}
		float sky01 = ws.GetSkyExposure01(GlobalPosition + Vector3.Up * SkyExposureProbeHeight);
		float threshold = data.rainShelterSkyThreshold;
		if (threshold >= 1f)
		{
			return sky01 >= 1f ? 1f : 0f;
		}
		return Mathf.Clamp((sky01 - threshold) / (1f - threshold), 0f, 1f);
	}

	// Slides _bodyTemperature toward the sampled environment + warmth bonus,
	// then arms / clears the cold and hot statuses based on the result.
	// Crossing a threshold IN applies the status with the timer paused (the
	// effect persists as long as the body is outside the safe band). Returning
	// to the safe band arms the authored 5s expiry — re-crossing pauses again
	// without re-stacking, mirroring the wet pattern.
	private void TickBodyTemperature(float dt)
	{
		if (data == null)
		{
			return;
		}
		GameClient client = GameClient.Current;
		if (client == null)
		{
			return;
		}

		float envTemp = client.SampleAirTemperature(GlobalPosition) + _warmthBonus;
		float speed = data.temperatureAcclimationSpeed;
		if (speed > 0f)
		{
			float diff = envTemp - _bodyTemperature;
			float step = speed * dt;
			if (Mathf.Abs(diff) <= step)
			{
				_bodyTemperature = envTemp;
			}
			else
			{
				_bodyTemperature += Mathf.Sign(diff) * step;
			}
		}
		else
		{
			_bodyTemperature = envTemp;
		}

		// Resistances from active status effects shift the trigger thresholds.
		// Positive coldResistance lowers the cold threshold (harder to chill);
		// positive heatResistance raises the hot threshold (harder to overheat).
		GetThermalResistances(out float coldResist, out float heatResist);
		// Wind chill. Multiplied by windTemperatureReduction (degrees F per
		// m/s) and shifted onto BOTH thresholds — the comfort band slides
		// upward in actual ambient, so cold triggers earlier and hot needs
		// hotter air to reach. SampleWindSpeed zeroes out under overhead
		// shelter so caves don't pretend to be windy.
		float windEffect = client.SampleWindSpeed(GlobalPosition) * data.windTemperatureReduction;
		float coldThreshold = data.coldTemperature - coldResist + windEffect;
		float hotThreshold = data.hotTemperature + heatResist + windEffect;

		UpdateThermalStatus(ref _coldState, data.coldStatus, _bodyTemperature < coldThreshold);
		UpdateThermalStatus(ref _hotState, data.hotStatus, _bodyTemperature > hotThreshold);
	}

	// Shared apply / pause / arm logic for cold and hot statuses. `triggered`
	// is true while the body is outside the safe band — the status is held
	// with timer paused. Once the body re-enters the safe band, the authored
	// duration is armed and the existing TickStatusEffects pruning loop
	// removes the state when it expires.
	private void UpdateThermalStatus(ref StatusEffectState state, StatusEffectData effectData, bool triggered)
	{
		if (effectData == null)
		{
			return;
		}
		if (state != null && !_statusEffects.Contains(state))
		{
			state = null;
		}
		if (triggered)
		{
			if (state == null)
			{
				state = AddStatusEffect(effectData);
			}
			state?.PauseTimer();
			return;
		}
		if (state != null && !state.IsTimed)
		{
			state.ArmTimer(_world?.GameTimeMs ?? 0);
		}
	}

	// Signed HP delta from a status-effect tick. Positive heals, negative
	// damages. Pierce in [0, 1] controls the armor bypass on the damage
	// branch — 1 (default for status effects) drops everything straight onto
	// health, matching the historical "poison ignores armor" feel; less than
	// 1 routes the absorbable slice through armor and chips the bar. Heals
	// skip armor entirely. Doesn't run the OnHurtBoxHit hit pipeline —
	// status ticks don't interrupt actions or pump per-frame impact fx.
	private void ApplyStatusHealthDelta(float delta, float pierce)
	{
		if (delta == 0f || _health <= 0f)
		{
			return;
		}
		bool wasAlive = _health > 0f;
		float before = _health;
		if (delta > 0f)
		{
			// Heal-over-time effects climb to MaxHealth the same way Heal()
			// does — drain doesn't reduce the effective cap, and any drain
			// the heal climbs into is forgiven to preserve the
			// `Health + DrainedHealth <= MaxHealth` invariant.
			_health = Mathf.Clamp(_health + delta, 0f, MaxHealth);
			_drainedHealth = Mathf.Min(_drainedHealth, Mathf.Max(0f, MaxHealth - _health));
		}
		else
		{
			// Damage branch: split between armor chip (absorbable) and direct
			// HP loss (bypassed) per the effect's pierce. Identical math to
			// the OnHurtBoxHit armor block, scoped down to the fields the
			// status path mutates.
			float damage = -delta;
			float p = Mathf.Clamp(pierce, 0f, 1f);
			float bypassed = damage * p;
			float absorbable = damage - bypassed;
			// Charging guard soaks the absorbable slice before central armor,
			// and any mid-charge DoT tick re-arms its recharge delay. Blunt
			// isn't modeled on status ticks, so the chip is unscaled here.
			// Status ticks don't interrupt actions, so querying the guard at
			// the call site is safe (no TryInterrupt ordering concern).
			AbsorbWeaponBlock(GetChargingBlockWeapon(), ref absorbable, 0f);
			float armorDamage = absorbable;
			if (_armor > 0f && armorDamage > 0f)
			{
				_armor = Mathf.Max(0f, _armor - armorDamage);
				ulong now = _world?.GameTimeMs ?? 0;
				if (_armor <= 0f)
				{
					_armorDepleted = true;
					_armorRechargeStartMs = now + (ulong)(data.armorRecoverTime * 1000f);
					SpawnWorldEffect(_armorDepletedFx);
				}
				else
				{
					_armorDepleted = false;
					_armorRechargeStartMs = now + (ulong)(data.armorRechargeDelay * 1000f);
				}
				_armorRecharging = false;
			}
			_health = Mathf.Max(0f, _health - bypassed);
		}
		// Status-effect ticks already fire at 1Hz from StatusEffectController,
		// so route directly through onDamage / onHeal — no DoT accumulation
		// needed. Use the realized HP change rather than `delta` so a heal
		// that climbed into the MaxHealth cap (or a damage tick that bottomed
		// at 0) only announces what actually moved.
		float change = _health - before;
		GameClient client = GameClient.Current;
		if (client != null)
		{
			if (change > 0f)
			{
				client.onHeal?.Invoke(GlobalPosition, change, EHudTextType.HealLight);
			}
			else if (change < 0f)
			{
				client.onDamage?.Invoke(GlobalPosition, -change, EHudTextType.DamageLight);
			}
		}
		if (_health <= 0f && wasAlive)
		{
			SpawnWorldEffect(_deathFx);
			SpawnWorldEffect(_deathVoFx);
			HandleDeath();
			PlayOneShot(EAnimation.Die);
		}
	}

	// Common bookkeeping on the alive→dead transition. Cancels any in-flight
	// action (weapon charge / consumable / interactive), tears down dash and
	// sprint, drops sneak / aim / hitstun-driven knockback, releases the
	// active interactive, and fires onDied so GameClient can start the
	// death-screen sequence. Position / velocity / animation are left to
	// _PhysicsProcess — the corpse falls under gravity and the Die one-shot
	// (latched by the caller) holds the pose.
	private void HandleDeath()
	{
		_runner?.TryAbort();
		_pendingWeaponPressSlot = null;
		_pendingWeaponPressActionName = null;
		_contextSensitiveAttackSlot = null;
		_dashTimeRemaining = 0f;
		_sprinting = false;
		_sneaking = false;
		_aiming = false;
		_hitstunTime = 0f;
		_knockbackTime = 0f;
		_knockbackVelocity = Vector3.Zero;
		_jumpHeld = false;
		if (_curInteractive != null)
		{
			SetCurInteractive(null);
		}
		if (_highlightInteractive != null)
		{
			_highlightInteractive = null;
			onHighlightChanged?.Invoke(null);
		}
		_inputMove = Vector3.Zero;
		_inputLook = Vector3.Zero;
		onDied?.Invoke(this);
	}

	// Console / scripted death entry point. Drops health to zero on the alive
	// branch only — re-calling on an already-dead body is a silent no-op so a
	// stray `die` press doesn't re-fire the death audio / animation. Runs the
	// same blood + VO + animation latch as a fatal hit so the death sequence
	// reads identically regardless of source.
	public void Kill()
	{
		if (_health <= 0f)
		{
			return;
		}
		_health = 0f;
		SpawnWorldEffect(_deathFx);
		SpawnWorldEffect(_deathVoFx);
		HandleDeath();
		PlayOneShot(EAnimation.Die);
	}

	// Reset for respawn. Keeps inventory / equipped gear / learned languages /
	// armor max — those are run-scope, not life-scope — but restores
	// pools and clears every per-life condition (status effects, wetness,
	// thermal acclimation, hitstun, dash cooldown). Hard-teleports via
	// TeleportTo so the stuck-recovery history can't yank the body back to
	// where it died. Caller (GameClient) is responsible for snapping the
	// camera to the new position.
	public void Respawn(Vector3 position)
	{
		_statusEffects?.Clear();
		_coldState = null;
		_hotState = null;
		_drainedHealth = 0f;
		_bloodRegenStartMs = 0;
		GameClient client = GameClient.Current;
		_bodyTemperature = client != null
			? client.SampleAirTemperature(position)
			: 70f;
		_warmthZoneCount = 0;
		_warmthBonus = 0f;
		_health = MaxHealth;
		_armor = MaxArmor;
		_stamina = MaxStamina;
		_armorRecharging = false;
		_armorDepleted = false;
		_armorRechargeStartMs = 0;
		_staminaRechargeStartMs = 0;
		_dashTimeRemaining = 0f;
		_dashCooldownEndMs = 0;
		_hitstunTime = 0f;
		_knockbackTime = 0f;
		_knockbackVelocity = Vector3.Zero;
		_sneaking = false;
		_sprinting = false;
		_aiming = false;
		_jumpHeld = false;
		_oneShotClip = default;
		_oneShotIsHitstun = false;
		_oneShotOverridesCharge = false;
		_grounded = false;
		_coyoteTimeEndMs = 0;
		TeleportTo(position);
		// Force the animator off the Die clip so the first post-respawn frame
		// shows the idle pose instead of holding the corpse. UpdateAnimation
		// will repick on the next physics tick.
		if (_animator != null && data != null)
		{
			StringName idleName = AnimName(EAnimation.Idle);
			if (idleName != default)
			{
				_animator.Play(idleName);
			}
		}
	}

	// Reconciles the player-carried MovingLight against the current inventory:
	// if any carried consumable has isActive == true and a TorchData backing,
	// a MovingLight from that torch's scene is attached as a child of the
	// player (so it follows the transform); otherwise any existing light is
	// torn down. The torch can live in the active hotbar slot OR any other
	// inventory slot — a lit torch in the backpack still lights the area
	// around the player. Called from the ToggleMovingLight event handler and
	// any time the inventory changes (pickup, drop, slot rearrange).
	public void RefreshCarriedLight()
	{
		PackedScene desiredScene = null;
		if (_inventory != null)
		{
			foreach (ItemState item in _inventory.EnumerateAll())
			{
				if (item is ConsumableState cs && cs.isActive && cs.data is TorchData torchData)
				{
					desiredScene = torchData.movingLightScene;
					break;
				}
			}
		}

		if (desiredScene != null)
		{
			if (_movingLight == null)
			{
				_movingLight = desiredScene.Instantiate<MovingLight>();
				AddChild(_movingLight);
			}
		}
		else if (_movingLight != null)
		{
			_movingLight.Deactivate();
			_movingLight.QueueFree();
			_movingLight = null;
		}
	}

	public void Initialize(World world, PlayerSpawnData spawnData, Vector3 position, Vector3 rotation)
	{
		_world = world;
		GlobalPosition = position;
		Rotation = rotation;
		_grounded = false;
		for (int i = 0; i < SafeGroundedHistorySize; i++)
		{
			_safeGroundedHistory[i] = position;
		}
		_safeGroundedHistoryWriteIdx = 0;
		_lastTickPosition = position;
		_stuckCheckDeadlineMs = 0;
		_inventory = new Inventory(this, data);
		_inventory.onSlotChanged += OnInventorySlotChanged;
		_inventory.onChanged += RefreshCarriedLight;
		_runner = new ActionRunner(this);
		_statusEffects = new StatusEffectController(this, world, ApplyStatusHealthDelta, ComposeMaskMul);
		_scent = new ScentEmitter(this, world, data.scentStrength, data.scentDecayRate,
			data.scentStampInterval, data.scentStampMoveDistance, data.scentMaxCrumbs);
		_health = MaxHealth;

		// Adopt the spawned character's name (blank keeps the default).
		if (!string.IsNullOrEmpty(spawnData?.playerName))
		{
			PlayerName = spawnData.playerName;
		}

		// Instance the base model package for the spawned gender, then activate
		// the live visual. Must run before UpdateArmorVisual below, which drives
		// the active model's mesh set.
		SpawnModelPackage(spawnData?.gender ?? EGender.Female);
		ActivateVisual();
		// Resolve + apply the modular appearance (skin tone, hair color, hair
		// style) before inventory seeding so the styled hair mesh is known the
		// first time the armor compositor runs.
		ApplyAppearance(spawnData);

		if (spawnData != null)
		{
			if (spawnData.equippedInventory != null)
			{
				foreach (ItemCount ic in spawnData.equippedInventory)
				{
					if (ic == null || ic.item == null || ic.count <= 0) { continue; }
					int stackSize = ic.item.maxStack > 0 ? ic.item.maxStack : 1;
					int remaining = ic.count;
					while (remaining > 0)
					{
						int n = System.Math.Min(remaining, stackSize);
						ItemState state = ic.item.CreateState();
						state.stackCount = n;
						_inventory.TryAdd(state);
						TryAutoEquipFromBackpack(state);
						remaining -= n;
					}
				}
			}
			if (spawnData.startingConsumables != null)
			{
				foreach (ConsumableData cd in spawnData.startingConsumables)
				{
					if (cd == null) { continue; }
					ItemState item = cd.CreateState();
					item.stackCount = cd.maxStack;
					_inventory.TryAdd(item);
					_inventory.TryMoveToConsumableSlot(item);
				}
			}
			if (spawnData.startingInventory != null)
			{
				foreach (ItemCount ic in spawnData.startingInventory)
				{
					if (ic == null || ic.item == null || ic.count <= 0) { continue; }
					int stackSize = ic.item.maxStack > 0 ? ic.item.maxStack : 1;
					int remaining = ic.count;
					while (remaining > 0)
					{
						int n = System.Math.Min(remaining, stackSize);
						ItemState state = ic.item.CreateState();
						state.stackCount = n;
						_inventory.TryAdd(state);
						remaining -= n;
					}
				}
			}
			// Apply the spawn-time knowledge pack. Each entry is a
			// TeachableConcept (item identification, recipe, language piece,
			// region reveal, mob bestiary seed) and routes through the same
			// Teach() flow that scrolls / NPC dialogue use. Announcements
			// are gated by GameClient.SuppressAnnouncements (set around
			// this whole Init call) so the player doesn't see a wall of
			// banners on the first frame for things they already know.
			if (spawnData.initialKnowledge != null)
			{
				for (int i = 0; i < spawnData.initialKnowledge.Count; i++)
				{
					spawnData.initialKnowledge[i]?.Teach(this);
				}
			}
		}

		// Start the player at full armor so freshly-spawned armor reads as
		// "ready" rather than charging up through the HUD on first frame.
		RecalculateMaxArmor();
		_armor = MaxArmor;
		_stamina = MaxStamina;

		// Authoritative first pass so the worn-armor meshes match the spawned
		// loadout even when nothing fired a slot-change (e.g. spawning bare).
		UpdateArmorVisual();

		// Seed body temperature to the spawn ambient so the player isn't
		// born already cold / hot just because the default float is 70°F.
		GameClient client = GameClient.Current;
		if (client != null)
		{
			_bodyTemperature = client.SampleAirTemperature(GlobalPosition);
		}
	}

	private void TryAutoEquipFromBackpack(ItemState item)
	{
		if (item?.data == null)
		{
			return;
		}
		switch (item.data)
		{
			case ArmorData armor:
				if (_inventory.GetEquipped(armor.armorSlot) == null)
				{
					_inventory.TryEquip(item, armor.armorSlot);
				}
				break;
			case WeaponData weapon:
				// Handedness is exclusive — melee → WeaponLeft, ranged → WeaponRight.
				// If the canonical slot is occupied, the weapon stays in the backpack.
				EInventorySlot weaponSlot = weapon.CanonicalSlot;
				if (_inventory.GetEquipped(weaponSlot) == null)
				{
					_inventory.TryEquip(item, weaponSlot);
				}
				break;
		}
	}

	private void OnInventorySlotChanged(EInventorySlot slot)
	{
		if (slot == EInventorySlot.ArmorHead
			|| slot == EInventorySlot.ArmorBody)
		{
			RecalculateMaxArmor();
			UpdateArmorVisual();
		}
	}

	// Sums maxArmor across every equipped armor slot. Current armor is capped
	// at the new max — unequipping a piece can only shrink the available pool,
	// it never grants free armor. Increases leave the current value alone so
	// the recharge logic owns the climb back up to the new max.
	private void RecalculateMaxArmor()
	{
		float total = 0f;
		if (_inventory != null)
		{
			AccumulateArmor(EInventorySlot.ArmorHead, ref total);
			AccumulateArmor(EInventorySlot.ArmorBody, ref total);
		}
		_maxArmor = total;
		if (_armor > _maxArmor)
		{
			_armor = _maxArmor;
		}
	}

	private void AccumulateArmor(EInventorySlot slot, ref float total)
	{
		if (_inventory.GetEquipped(slot) is ArmorState armor && armor.data != null)
		{
			total += armor.data.maxArmor;
		}
	}

	// Awards `amount` exp to every equipped weapon and armor piece. Called
	// from Mob.Damage on the lethal hit when the killer is this player; each
	// state walks SimData.ExpPerLevel and promotes level as thresholds are
	// crossed, capped at its own data.maxLevel.
	public void GrantEquippedExperience(int amount)
	{
		if (amount <= 0 || _inventory == null)
		{
			return;
		}
		var thresholds = _world?.SimData?.ExpPerLevel;
		if (thresholds == null)
		{
			return;
		}
		(_inventory.GetEquipped(EInventorySlot.WeaponLeft) as WeaponState)?.AddExp(amount, thresholds);
		(_inventory.GetEquipped(EInventorySlot.WeaponRight) as WeaponState)?.AddExp(amount, thresholds);
		(_inventory.GetEquipped(EInventorySlot.ArmorHead) as ArmorState)?.AddExp(amount, thresholds);
		(_inventory.GetEquipped(EInventorySlot.ArmorBody) as ArmorState)?.AddExp(amount, thresholds);
	}

	// Compose a single stat across inherent PlayerData modifiers, equipped
	// armor modifiers, and active status-effect modifiers. Seeds with the
	// stat's neutral identity (1 for multiplicative, 0 for additive) and
	// folds each source. Multiplicative for most stats, additive for the
	// four additive ones (Camouflage / MaxStamina / ColdResist / HeatResist)
	// per StatModifierUtil.IsAdditive.
	public float ComposeStat(EStat stat)
	{
		float value = StatModifierUtil.NeutralValue(stat);
		if (data?.modifiers != null)
		{
			value = StatModifierUtil.Fold(stat, data.modifiers, value);
		}
		value = AccumulateArmorStat(EInventorySlot.ArmorHead, stat, value);
		value = AccumulateArmorStat(EInventorySlot.ArmorBody, stat, value);
		value = _statusEffects?.FoldStat(stat, value) ?? value;
		return value;
	}

	// Multiplicative compose across all sources for a tag mask — used at
	// hit-application sites (damage / pierce chance / blunt chip / knockback
	// magnitude). Walks every entry whose single-bit stat overlaps the mask
	// and multiplies. The StatusEffectController routes through this
	// callback when scaling buildup contributions and DoT damage ticks.
	public float ComposeMaskMul(EStat mask)
	{
		float product = 1f;
		if (data?.modifiers != null)
		{
			product = StatModifierUtil.FoldMask(mask, data.modifiers, product);
		}
		product = AccumulateArmorMask(EInventorySlot.ArmorHead, mask, product);
		product = AccumulateArmorMask(EInventorySlot.ArmorBody, mask, product);
		product = _statusEffects?.FoldMask(mask, product) ?? product;
		return product;
	}

	private float AccumulateArmorStat(EInventorySlot slot, EStat stat, float value)
	{
		if (_inventory == null) { return value; }
		if (_inventory.GetEquipped(slot) is ArmorState armor && armor.data?.modifiers != null)
		{
			value = StatModifierUtil.Fold(stat, armor.data.modifiers, value);
		}
		return value;
	}

	private float AccumulateArmorMask(EInventorySlot slot, EStat mask, float product)
	{
		if (_inventory == null) { return product; }
		if (_inventory.GetEquipped(slot) is ArmorState armor && armor.data?.modifiers != null)
		{
			product = StatModifierUtil.FoldMask(mask, armor.data.modifiers, product);
		}
		return product;
	}

	// Composite cold / heat resistance from every equipped armor piece plus
	// every active status effect. Used by the temperature path to shift the
	// cold/hot trigger thresholds and by the inventory's player-stats panel
	// to display the resolved total.
	public void GetThermalResistances(out float coldResistance, out float heatResistance)
	{
		coldResistance = ComposeStat(EStat.ColdResist);
		heatResistance = ComposeStat(EStat.HeatResist);
	}

	// Composite sense stats from every equipped armor piece plus every
	// active status effect. Camouflage is an additive sum (0 = neutral);
	// the four sense modifiers are multiplicative products (1.0 = neutral).
	// Callers fold the multipliers into a PlayerData base value when an
	// effective absolute is wanted; the inventory stats panel just renders
	// them as signed deltas off neutral.
	public void GetSenseStats(out float camouflage, out float visionMultiplier, out float hearingMultiplier, out float noiseMultiplier, out float scentMultiplier)
	{
		camouflage = ComposeStat(EStat.Camouflage);
		visionMultiplier = ComposeStat(EStat.Vision);
		hearingMultiplier = ComposeStat(EStat.Hearing);
		noiseMultiplier = ComposeStat(EStat.Noise);
		scentMultiplier = ComposeStat(EStat.Scent);
	}

	// Composite movement multiplier from every active status effect. Doesn't
	// include armor — armor doesn't carry a speed modifier in the current
	// model. Cold and similar effects multiply in here.
	public float SpeedMultiplier => _statusEffects?.FoldStat(EStat.MoveSpeed, 1f) ?? 1f;

	// Counts the per-hit flinch + knockback windows down each physics tick.
	// The hitstun anim is latched as a one-shot in OnHurtBoxHit, so this
	// method only owns the state-clear for that timer — the animator falls
	// out of the anim naturally when it finishes or another one-shot replaces
	// it. Knockback is two-phase: while _knockbackTime > 0 the horizontal
	// velocity is forced to _knockbackVelocity (in the velocity rebuild
	// below); on the trailing edge we snap it back to zero and clear the
	// cached vector so the next frame's input rebuild starts clean.
	private void TickHitstun(float dt)
	{
		if (_hitstunTime > 0f)
		{
			_hitstunTime = Mathf.Max(0f, _hitstunTime - dt);
		}
		if (_knockbackTime > 0f)
		{
			_knockbackTime = Mathf.Max(0f, _knockbackTime - dt);
			if (_knockbackTime <= 0f)
			{
				Velocity = new Vector3(0f, Velocity.Y, 0f);
				_knockbackVelocity = Vector3.Zero;
			}
		}
	}

	private void TickArmor(float dt)
	{
		// MaxArmor (not the raw _maxArmor equipment sum) so a MaxArmor stat
		// modifier actually expands the rechargeable pool, not just the readout.
		float maxArmor = MaxArmor;
		if (maxArmor <= 0f || _armor >= maxArmor)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (now < _armorRechargeStartMs)
		{
			return;
		}
		if (!_armorRecharging)
		{
			_armorRecharging = true;
			SpawnWorldEffect(_armorDepleted ? _armorRecoverStartFx : _armorRechargeStartFx);
		}
		_armor = Mathf.Min(maxArmor, _armor + data.armorRechargeSpeed * dt);
		if (_armor >= maxArmor)
		{
			_armorDepleted = false;
		}
	}

	// The weapon whose block-armor guard is currently live — non-null only
	// while the player is charging a weapon that carries a block pool. The
	// guard is "active" (absorbs damage) only during the Charging phase; it
	// still recharges between charges (TickBlockArmor) so it's topped up for
	// the next one.
	private WeaponState GetChargingBlockWeapon()
	{
		if (_runner == null || !_runner.IsBusy || _runner.Phase != EActionPhase.Charging)
		{
			return null;
		}
		if (_runner.Current.context.primaryItem is WeaponState weapon
			&& weapon.data != null && weapon.data.blockArmor > 0f)
		{
			return weapon;
		}
		return null;
	}

	// Routes the armor-touchable slice of an incoming hit through the charging
	// weapon's guard before the player's central armor. While the guard has
	// any charge it eats the WHOLE absorbable slice (zeroing `absorbable` so
	// the central-armor block downstream sees nothing) and reports how much
	// the pool actually lost for HUD feedback. Re-arms the weapon's recharge
	// delay on every mid-charge hit — even a fully-pierced hit with no
	// absorbable slice, and even when the pool is already empty — so a player
	// taking fire can't regenerate their guard. No-op when not charging a
	// guard-bearing weapon.
	private float AbsorbWeaponBlock(WeaponState weapon, ref float absorbable, float blunt)
	{
		if (weapon == null)
		{
			return 0f;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		weapon.blockArmorRechargeStartMs = now + (ulong)(weapon.data.blockArmorRechargeDelay * 1000f);
		if (weapon.blockArmor <= 0f || absorbable <= 0f)
		{
			return 0f;
		}
		float blockDamage = absorbable * (1f + blunt);
		float before = weapon.blockArmor;
		weapon.blockArmor = Mathf.Max(0f, before - blockDamage);
		absorbable = 0f;
		return before - weapon.blockArmor;
	}

	// Per-tick recharge of every equipped weapon's block-armor guard. Mirrors
	// TickArmor but keyed off the weapon's own (independent) recharge stats and
	// driven for both weapon slots so a guard refills whether or not it's the
	// one being charged. No depletion fx — the HUD bar carries the feedback.
	private void TickBlockArmor(float dt)
	{
		ulong now = _world?.GameTimeMs ?? 0;
		TickWeaponBlockArmor(_inventory?.GetEquipped(EInventorySlot.WeaponLeft) as WeaponState, now, dt);
		TickWeaponBlockArmor(_inventory?.GetEquipped(EInventorySlot.WeaponRight) as WeaponState, now, dt);
	}

	private static void TickWeaponBlockArmor(WeaponState weapon, ulong now, float dt)
	{
		if (weapon?.data == null)
		{
			return;
		}
		float max = weapon.data.blockArmor;
		if (max <= 0f || weapon.blockArmor >= max)
		{
			return;
		}
		if (now < weapon.blockArmorRechargeStartMs)
		{
			return;
		}
		weapon.blockArmor = Mathf.Min(max, weapon.blockArmor + weapon.data.blockArmorRechargeSpeed * dt);
	}

	// IActionActor — press-time stamina gate. Non-mutating peek. Costs of 0
	// or less always pass.
	public bool HasStamina(float amount)
	{
		if (amount <= 0f)
		{
			return true;
		}
		return _stamina >= amount;
	}

	// IActionActor — unconditional spend at EnterActive. Allowed to drive
	// stamina negative; sprint / swim gating already keys off `_stamina <= 0`
	// and the recharge tick re-fills from negative without special handling.
	// Arms the recharge delay so a heavy action doesn't begin refilling
	// immediately after firing.
	public void ConsumeStamina(float amount)
	{
		if (amount <= 0f)
		{
			return;
		}
		_stamina -= amount;
		ulong now = _world?.GameTimeMs ?? 0;
		_staminaRechargeStartMs = now + (ulong)(data.staminaRechargeDelay * 1000f);
	}

	private void TickStamina(float dt)
	{
		float max = MaxStamina;
		// A status effect with maxStaminaBonus can shrink the cap when it
		// expires (e.g. Hydrated wearing off). Clamp before the recharge
		// early-out so a higher-than-cap value comes back down to the new max.
		if (_stamina > max)
		{
			_stamina = max;
		}
		if (max <= 0f || _stamina >= max)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (now < _staminaRechargeStartMs)
		{
			return;
		}
		// staminaRechargeTime is the 0-to-full duration; convert to a flat
		// per-second rate. A partial spend then refills proportionally faster.
		float rechargeTime = data.staminaRechargeTime;
		float rate = rechargeTime > 0f ? max / rechargeTime : max;
		_stamina = Mathf.Min(max, _stamina + rate * dt);
	}

	// Swimming + active move input drains stamina at a flat per-second rate
	// and re-arms the recharge delay each tick (mirrors the dash pattern:
	// spend is unconditional, stamina is allowed to go negative, movement is
	// never gated on it).
	private void TickSwimStamina(float dt)
	{
		if (data == null || _waterState != EWaterState.Swimming)
		{
			return;
		}
		if (_inputMove.LengthSquared() <= 0.0001f)
		{
			return;
		}
		_stamina -= data.swimStaminaDrainPerSecond * dt;
		ulong now = _world?.GameTimeMs ?? 0;
		_staminaRechargeStartMs = now + (ulong)(data.staminaRechargeDelay * 1000f);
	}

	// Gates aiming and look-driven rotation. Returns false during dash and
	// sprint so the player commits to facing movement direction during the
	// burst — both _aiming (which drives the aim reticle, ranged routing,
	// gamepad stick fallback) and the rotation block in _PhysicsProcess
	// consult this single function so they can't drift out of sync.
	private bool CanLook()
	{
		return _dashTimeRemaining <= 0f && !_sprinting;
	}

	// Recompute _sprinting each tick from current state. Sprint engages when
	// Dash is held past the initial dash burst with move input AND the
	// player has stamina to spend; once stamina hits zero, sprint drops
	// entirely (no speed boost, no anim, no continuing drain). After an
	// exhaustion drop the player must RELEASE Dash and press it again to
	// re-engage — holding the button through a stamina refill won't
	// re-enter sprint. The existing staminaRechargeDelay still gates when
	// the bar starts refilling after sprint ends. Disabled while airborne
	// on land (no "air sprint") — swimming keeps its own sprint variant
	// since it's a continuous surface contact, not an arc.
	private void UpdateSprintState()
	{
		bool oldSprinting = _sprinting;
		bool dashHeld = Input.IsActionPressed("Dash");
		if (!dashHeld)
		{
			_sprintLockout = false;
		}
		if (data == null)
		{
			_sprinting = false;
			return;
		}
		bool runnerBlocks = _runner != null
			&& _runner.IsBusy
			&& _runner.Current.profile != data.dashActionProfile;
		bool surfaceAllowsSprint = _grounded || _waterState == EWaterState.Swimming;
		bool wantsSprint = dashHeld
			&& _dashTimeRemaining <= 0f
			&& _inputMove.LengthSquared() > 0.0001f
			&& _curInteractive == null
			&& !runnerBlocks
			&& surfaceAllowsSprint
			&& _stamina > 0f
			&& !_sprintLockout;
		_sprinting = wantsSprint;
		// Latch the exhaustion lockout when sprint ends because stamina
		// hit zero while the button was still held. Won't fire on a
		// voluntary release (dashHeld is false there) or on a context
		// change like an attack (stamina would still be positive).
		if (oldSprinting && !_sprinting && _stamina <= 0f && dashHeld)
		{
			_sprintLockout = true;
		}
	}

	// Mirrors TickSwimStamina. Sprint drains a flat per-second amount and
	// re-arms the recharge delay each tick (stamina is allowed to go
	// negative; movement is never gated on it, but UpdateSprintState ends
	// sprint as soon as stamina hits zero).
	private void TickSprintStamina(float dt)
	{
		if (!_sprinting || data == null)
		{
			return;
		}
		_stamina -= data.sprintStaminaDrainPerSecond * dt;
		ulong now = _world?.GameTimeMs ?? 0;
		_staminaRechargeStartMs = now + (ulong)(data.staminaRechargeDelay * 1000f);
	}

	// Fires the out-of-breath one-shot on the positive→exhausted crossing.
	// Run after the stamina drains/recharge each tick so it reads the settled
	// value. The latch clears only once stamina climbs back above zero, so the
	// gasp plays once per exhaustion instead of every frame the bar is empty.
	private void TickStaminaExhaustion()
	{
		bool exhausted = _stamina <= 0f;
		if (exhausted && !_staminaExhausted)
		{
			SpawnSelfEffect(_outOfBreathFx);
		}
		_staminaExhausted = exhausted;
	}

	// Centralized cancel for dash-and-sprint. Called from attack handlers so
	// committing to a swing always wins over an in-flight movement state.
	// TryAbort on the runner only fires AbortActive when the active tier's
	// canAbort is true (set on the dash tier in dash_action.tres), so this
	// also explicitly zeroes the per-actor dash timers — AbortActive only
	// resets the runner's PlayerAction, not Player's physics state.
	private void CancelDashAndSprint()
	{
		if (_runner != null && _runner.IsBusy && _runner.Current.profile == data?.dashActionProfile)
		{
			_runner.TryAbort();
		}
		_dashTimeRemaining = 0f;
		_sprinting = false;
	}

	public override void _PhysicsProcess(double delta)
	{
		float dt = (float)delta;
		base._PhysicsProcess(delta);

		if (dt <= 0)
		{
			return;
		}

		// Riding a vehicle: the boat / mount owns position, physics, and water
		// handling; the rider just keeps its status effects and seated anim
		// ticking. Began-mounted frames take this gate; the frame mounting
		// completes is handled by the post-runner bail below.
		if (_mount != null)
		{
			TickMounted(dt);
			return;
		}

		UpdateTerrainSpeed();
		UpdateWaterState();
		UpdateSprintState();
		TickArmor(dt);
		TickBlockArmor(dt);
		TickStamina(dt);
		TickSwimStamina(dt);
		TickSprintStamina(dt);
		TickStaminaExhaustion();
		TickBloodDrain(dt);
		TickHitstun(dt);
		_statusEffects.Tick(dt);
		TickWetEffect(dt);
		TickDirtyEffect(dt);
		TickBodyTemperature(dt);
		DotHudFlush dotFlush = _dotHud.Tick(_world?.GameTimeMs ?? 0, GlobalPosition);
		if (dotFlush.damage)
		{
			// Continuous damage authors no per-frame fx; its "ouch" rides on
			// the once-per-second HUD rollup instead. Same pacing for the
			// red damage-flash so a slow burn doesn't desaturate the screen
			// permanently — one pulse per HUD-rolled second.
			SpawnWorldEffect(_hurtVoFx);
			GameClient.Current?.FlashDamage(dotFlush.damageAmount);
		}
		// Recompose emitted scent strength each tick so equipment / status
		// modifiers actually change the trail mobs read — a Dirty garment
		// cranks it up (EStat.Scent > 1), a future scent-masking cloak would
		// cut it. Strength feeds both per-crumb potency and crumb lifetime
		// (lifetime = strength / decayRate), so dirtier reads farther and
		// lingers longer. Without this the Scent stat only fed the stats panel.
		if (_scent != null)
		{
			_scent.Strength = data.scentStrength * ComposeStat(EStat.Scent);
		}
		_scent?.Tick(dt);

		// Footstep / wake ripples on the water surface. Stride is longer
		// while wading (discrete step impacts) than while swimming
		// (continuous wake). Strength is kept low — the radial wave packet
		// in voxel_water.gdshader is already amplified by water_ripple_tilt,
		// so per-emit strength only needs to mark "this is a footstep,
		// not a boulder splash".
		bool inWater = _waterState != EWaterState.None;
		float rippleStride = _waterState == EWaterState.Swimming ? 1.5f : 2.0f;
		float rippleStrength = _waterState == EWaterState.Swimming ? 0.15f : 0.25f;
		Vector3 ripplePos = new(GlobalPosition.X, _waterSurfaceY, GlobalPosition.Z);
		_rippleEmitter.Update(ripplePos, inWater, rippleStrength, rippleStride);

		// Footsteps and footprints are driven by the model's foot-contact
		// method track (see EmitFootstep), not anything in this method.
		// Movement-gated continuous loops below still key off horizontal speed.
		Vector2 horizVel = new(Velocity.X, Velocity.Z);
		float horizSpeedSq = horizVel.LengthSquared();

		// Movement-gated continuous loops. The water swim loop only plays
		// while actually swimming — shallow wading is covered by the
		// shallow-water footstep FX, so playing the swim loop there too
		// would double-up the audio.
		bool intentMoving = _inputMove.LengthSquared() > 0.0001f;
		bool moving = intentMoving || horizSpeedSq > _movingMinSpeedSq;
		bool waterLoopActive = moving && _waterState == EWaterState.Swimming;
		// Tall-grass and water are mutually exclusive — when wading, the
		// shallow footsteps win so we don't double up on rustle + slosh.
		bool foliageLoopActive = moving && _foliageCollisions.Count > 0 && _waterState == EWaterState.None;
		UpdateLoopEffect(ref _waterMovementLoop, _waterMovementLoopFx, waterLoopActive);
		UpdateLoopEffect(ref _foliageMovementLoop, _foliageMovementLoopFx, foliageLoopActive);

		// Dash and sprint suppress aim — the player commits to the movement
		// burst, so look-rotation and the gamepad-stick aim fallback both
		// yield to movement direction. Single gate for both _aiming below and
		// the rotation block further down.
		//
		// Charging a right-hand weapon also forces aim on: the cursor needs
		// to keep updating (Positional aim wants the player to rest the stick
		// without dropping out of aim), and the reticle should stay visible
		// for the full hold. The gate still requires `canLook`, so a dash
		// out of a charge still suppresses aim (charging cancels via the
		// existing path).
		bool canLook = CanLook();
		bool chargingRightWeapon = _runner != null
			&& _runner.IsBusy
			&& _runner.Phase == EActionPhase.Charging
			&& _runner.Current.context.sourceSlot == EInventorySlot.WeaponRight;
		_aiming = canLook
			&& (Input.IsActionPressed("Aim")
				|| (_inputLook != Vector3.Zero && InputDevice.Current == InputDevice.EDevice.Gamepad)
				|| chargingRightWeapon);

		// Stamina-gated speed table:
		//   sneaking                         → sneakSpeed
		//   sprinting                        → sprintSpeed  (gated on stamina>0 in UpdateSprintState)
		//   not sprinting + stamina ≤ 0      → tiredRunSpeed
		//   else                             → moveSpeed
		bool exhausted = _stamina <= 0f;
		float speed = data.moveSpeed;
		if (_sneaking)
		{
			speed = data.sneakSpeed;
		}
		else if (_sprinting)
		{
			speed = data.sprintSpeed;
		}
		else if (exhausted)
		{
			speed = data.tiredRunSpeed;
		}
		speed *= _terrainSpeed;

		if (_waterState == EWaterState.Shallow)
		{
			speed *= data.shallowWaterSpeed;
		}
		else if (_waterState == EWaterState.Swimming)
		{
			// Same stamina gating as run: depleted swimmer drops to
			// tiredSwimSpeed. Swim drain runs whenever swimming + moving
			// (see TickSwimStamina) so an exhausted swimmer can't refill
			// until they stop trying to move.
			speed = exhausted ? data.tiredSwimSpeed : data.swimSpeed;
		}
		float statusMoveMul = _statusEffects?.FoldStat(EStat.MoveSpeed, 1f) ?? 1f;
		speed *= statusMoveMul;
		// Anim retiming is gated to movement-loop anims only — see
		// UpdateAnimation, which writes effectSpeedMultiplier per-frame based
		// on the currently-picked loopAnim. Attack / hitstun / death anims
		// play at authored speed regardless of status.
		if (_curInteractive != null || (_runner != null && _runner.LocksMovement) || _birdsEye)
		{
			speed = 0;
		}

		// Horizontal velocity: knockback wins over everything (dash, glide,
		// input) — _knockbackVelocity is the constant m/s the hit author
		// wants the body to travel at for the duration of _knockbackTime, and
		// only when the timer expires does control return to the rest of the
		// table. Otherwise dash and its glide window override the input-driven
		// rebuild. All paths still honor _terrainSpeed (tall grass slows) and
		// status moveMul (Cold etc.) so dashing through a thicket isn't the
		// same as dashing across open ground — except knockback, which is a
		// fixed-distance shove and ignores those.
		bool prevSkidding = _skidding;
		_skidding = false;
		// Wind drift target, sampled once per tick and reused by both the
		// airborne input target (additive, so input intent stacks on wind
		// rather than fights it) and the airborne drag block below (the
		// drag is computed in the wind-relative frame so a hanging player
		// in strong wind drifts toward the wind's drift target rather than
		// zero). Mirrors the water-current pattern used by the swimming
		// branch. Zero while grounded (feet planted) or under overhead
		// cover (SampleWindSpeed raycasts up and returns 0 under a roof).
		Vector3 windDrift = Vector3.Zero;
		if (!_grounded)
		{
			Vector3 windDir = SkyController.Current?.ZoneState.WindDirection ?? Vector3.Zero;
			if (windDir.LengthSquared() > 0.0001f)
			{
				GameClient client = GameClient.Current;
				float windSpeed = client != null ? client.SampleWindSpeed(GlobalPosition) : 0f;
				if (windSpeed > 0f)
				{
					windDir.Y = 0f;
					windDrift = windDir.Normalized() * (windSpeed * data.windDragXZ);
				}
			}
		}
		if (_knockbackTime > 0f)
		{
			Velocity = new Vector3(_knockbackVelocity.X, Velocity.Y, _knockbackVelocity.Z);
		}
		else if (_dashTimeRemaining > 0f)
		{
			float dashSpeedActual = _dashSpeed * _terrainSpeed * statusMoveMul;
			if (_waterState == EWaterState.Swimming)
			{
				dashSpeedActual *= data.dashSwimSpeedScale;
			}
			Velocity = new Vector3(_dashDir.X * dashSpeedActual, Velocity.Y, _dashDir.Z * dashSpeedActual);
			_dashTimeRemaining -= dt;
			if (_dashTimeRemaining <= 0f)
			{
				// Dash ended this tick — arm the glide so the player doesn't
				// snap from dash speed to input speed.
				EndDash();
			}
		}
		else if (_skating)
		{
			ApplySkatingMotion(dt);
		}
		else
		{
			Vector3 inputVel = _inputMove * speed;
			// Wall-jump arc preservation. While the air-control timer is alive
			// and we're airborne, lerp from the current XZ velocity (the kick
			// the wall jump just applied) toward the input-driven target so
			// input authority fades in over wallJumpAirControlTime rather than
			// snapping every tick. The timer ticks down regardless of which
			// velocity branch is active (see below), so a knockback / dash
			// landing mid-window doesn't extend the blend past its arc.
			if (_wallJumpAirControlTimer > 0f && !_grounded && data.wallJumpAirControlTime > 0f)
			{
				float t = 1f - (_wallJumpAirControlTimer / data.wallJumpAirControlTime);
				Vector3 currentXZ = new(Velocity.X, 0f, Velocity.Z);
				Vector3 blended = currentXZ.Lerp(inputVel, t);
				Velocity = new Vector3(blended.X, Velocity.Y, blended.Z);
			}
			else if (_waterState == EWaterState.Swimming)
			{
				// Linear acceleration toward (input + current × drag). The
				// target folds in the local water current so steady-state
				// matches the previous additive model — player at rest still
				// drifts at current × drag, with input stacked on top — but
				// the transient takes ramp-in time so swimming reads as
				// weighted instead of snapping. ApplyWaterPhysics no longer
				// re-adds current; with acceleration in place the per-tick
				// add would compound across ticks.
				Vector3 waterCurrent = _world.WorldState.SampleWaterCurrent(GlobalPosition);
				Vector3 target = inputVel + new Vector3(waterCurrent.X, 0f, waterCurrent.Z) * data.waterCurrentDrag;
				Vector3 currentXZ = new(Velocity.X, 0f, Velocity.Z);
				Vector3 nextXZ = ApproachXZ(currentXZ, target, data.waterAcceleration * dt);
				Velocity = new Vector3(nextXZ.X, Velocity.Y, nextXZ.Z);
			}
			else
			{
				// Ground / air: linear approach toward input target. Same math
				// as the water branch above minus the current term. Ground uses
				// groundAcceleration (sharp), air uses airAcceleration (drifty
				// so jumps preserve momentum); releasing input decelerates at
				// the same rate, so the ground branch replaces the old instant
				// snap-to-stop. Airborne stacks windDrift onto the input target
				// so wind nudges a hanging-in-air player without fighting their
				// move intent — input + drift, water-current style.
				// Grip drops while skidding — uses the skid-specific acceleration
				// instead of groundAcceleration so a sharp direction change
				// commits to the existing velocity vector for a beat rather than
				// snapping. `prevSkidding` is the latch from the previous tick;
				// using it here means the same tick that detects skid-entry
				// already runs with the reduced accel.
				float accel;
				if (_grounded)
				{
					accel = prevSkidding ? data.skidGroundAcceleration : data.groundAcceleration;
				}
				else
				{
					accel = data.airAcceleration;
				}
				Vector3 currentXZ = new(Velocity.X, 0f, Velocity.Z);
				Vector3 target = inputVel + windDrift;
				Vector3 nextXZ = ApproachXZ(currentXZ, target, accel * dt);
				Velocity = new Vector3(nextXZ.X, Velocity.Y, nextXZ.Z);
				// Skid detection: gap between desired and actual horizontal
				// velocity. Only meaningful when grounded (airborne intent /
				// actual disagreements aren't a skid — feet aren't touching
				// anything). Hysteresis prevents puff flicker near threshold.
				if (_grounded)
				{
					float gapSq = (inputVel - currentXZ).LengthSquared();
					float enter = data.skidEnterSpeed;
					float exit = data.skidExitSpeed;
					_skidding = prevSkidding
						? gapSq >= exit * exit
						: gapSq > enter * enter;
					if (CVars.debugSlopes.Value && prevSkidding != _skidding)
					{
						string ts = System.DateTime.Now.ToString("HH:mm:ss.fff");
						float gap = Mathf.Sqrt(gapSq);
						GD.Print(_skidding
							? $"[skid] ENTER {ts} gap={gap:F2}m/s input={inputVel.Length():F2} actual={currentXZ.Length():F2} (thr={enter:F2})"
							: $"[skid] EXIT  {ts} gap={gap:F2}m/s (thr={exit:F2})");
					}
				}
			}
		}
		if (_wallJumpAirControlTimer > 0f)
		{
			_wallJumpAirControlTimer = Mathf.Max(0f, _wallJumpAirControlTimer - dt);
		}

		// Speed-line streaks during the dash. Driven outside the velocity
		// if/else chain above so toggling the loop can't skip the glide and
		// input-rebuild branches.
		UpdateLoopEffect(ref _dashLoop, _dashSpeedLinesFx, _dashTimeRemaining > 0f);
		// The streaks are emitted forward (+Z) at the dash speed and damped so
		// they fall behind — GPUParticles3D inherit_velocity is broken in Godot
		// 4.x, so we drive the heading ourselves. The dash can travel a
		// different way than the player faces (look-locked backstep while
		// aiming), so orient the emitter to the real _dashDir rather than facing.
		if (_dashLoop != null)
		{
			_dashLoop.GlobalRotation = new Vector3(0f, Mathf.Atan2(_dashDir.X, _dashDir.Z), 0f);
		}

		// Vertical: airborne dry-land dash with freezeGravity zeros Y and
		// suppresses gravity for the dash hang. Grounded dash falls through to
		// the grounded branch's -1 downward so step-down still hugs slopes
		// (and walks off cliffs by clearing _grounded when no floor is found).
		// Swim dash keeps normal water physics so buoyancy and drag still apply.
		bool dashFreezeY = _dashTimeRemaining > 0f && _dashFreezeGravity
			&& !_grounded && _waterState != EWaterState.Swimming;
		if (dashFreezeY)
		{
			Velocity = new Vector3(Velocity.X, 0f, Velocity.Z);
		}
		else if (_waterState == EWaterState.Swimming)
		{
			ApplyWaterPhysics(dt);
		}
		else if (!_grounded)
		{
			float gravity = (_jumpHeld && Velocity.Y > 0) ? _world.SimData.Gravity * data.jumpHoldGravityScale : _world.SimData.Gravity;
			Velocity += Vector3.Down * gravity * dt;
			// Two-axis air drag. Skipped while dashing — the dash action
			// authors its own forced velocity and drag would fight it.
			//   airDragDown: linear, opposes falls only. Velocity.Y > 0
			//   (upward) is left alone so jumps and skate-launches keep
			//   their full arc.
			//   airDragXZ: QUADRATIC in the wind-relative frame. Per-tick
			//   deceleration scales as airDragXZ * |v_rel|² along v_rel,
			//   where v_rel = velocityXZ − windDrift. The quadratic profile
			//   keeps drag near-imperceptible at sprint speeds and very
			//   strong past them, so skate / dash-launch excess bleeds off
			//   hard without fighting normal flight speed. The wind-frame
			//   computation means the asymptote is windDrift, not zero —
			//   a hanging player parks at the wind's drift target rather
			//   than being yanked back to a standstill.
			if (_dashTimeRemaining <= 0f)
			{
				if (Velocity.Y < 0f)
				{
					float dragY = 1f - data.airDragDown * dt;
					if (dragY < 0f)
					{
						dragY = 0f;
					}
					Velocity = new Vector3(Velocity.X, Velocity.Y * dragY, Velocity.Z);
				}
				Vector3 vXZ = new(Velocity.X, 0f, Velocity.Z);
				Vector3 vRel = vXZ - windDrift;
				float speedRel = vRel.Length();
				if (speedRel > 0.001f)
				{
					float factor = data.airDragXZ * speedRel * dt;
					if (factor > 1f)
					{
						factor = 1f;
					}
					vXZ -= vRel * factor;
					Velocity = new Vector3(vXZ.X, Velocity.Y, vXZ.Z);
				}
			}
		}
		else
		{
			Velocity = new Vector3(Velocity.X, -1f, Velocity.Z); // Small downward force to keep grounded
		}

		// Same CanLook gate as the _aiming suppression above — during dash or
		// sprint, rotation falls through to move direction. While skating
		// or skidding, yaw locks to the velocity heading rather than the
		// input direction so the model reads as committed to its existing
		// trajectory — the feet are visibly sliding because the body
		// hasn't caught up to the input yet.
		if (_skating || _skidding)
		{
			Vector3 lockHoriz = new(Velocity.X, 0f, Velocity.Z);
			if (lockHoriz.LengthSquared() > 0.001f)
			{
				Rotation = new Vector3(0, Mathf.Atan2(lockHoriz.X, lockHoriz.Z), 0);
			}
		}
		else if (CanLook() && _inputLook != Vector3.Zero)
		{
			Rotation = new Vector3(0, Mathf.Atan2(_inputLook.X, _inputLook.Z), 0);
		}
		else if (_inputMove != Vector3.Zero)
		{
			Rotation = new Vector3(0, Mathf.Atan2(_inputMove.X, _inputMove.Z), 0);
		}

		// Ranged aim assist runs after the stick-driven rotation so the yaw
		// cone is evaluated against the just-applied yaw, and so the gentle
		// yaw pull lands before _runner.Tick reads ActorForward to fire.
		UpdateAimAssist();

		_runner?.Tick();

		// Runner finished the interactive action this tick — clear the
		// player's "engaged with X" state so movement unlocks next frame and
		// the Interacting anim resumes. Also drop the highlight so the
		// player has to walk back into range to re-engage.
		if (_curInteractive != null && _runner != null && !_runner.IsBusy)
		{
			SetCurInteractive(null);
			_highlightInteractive = null;
			onHighlightChanged?.Invoke(null);
		}

		// Mounting completed this tick — the Board interactive's OpenInteractive
		// fired Mount() from inside _runner.Tick above. Bail before MoveAndSlide;
		// the deferred AttachToMount snaps the rider onto the seat and the
		// top-of-frame gate drives every subsequent frame.
		if (_mount != null)
		{
			return;
		}

		// Step up: lift the player before moving so they can clear small obstacles.
		// Disabled while swimming — the player is floating, not walking. Uses
		// MoveAndCollide so the lift stops at contact; raw teleport would clip
		// the head through low ceilings (e.g. cave interiors) and block
		// horizontal motion because MoveAndSlide then pushes back down.
		Vector3 posBeforeStep = GlobalPosition;
		bool useStepUp = _grounded && _waterState != EWaterState.Swimming;
		if (useStepUp)
		{
			using var stepUpResult = MoveAndCollide(Vector3.Up * data.stepHeight);
		}

		bool wasOnFloor = _grounded;
		// Captured before MoveAndSlide because the slide will zero Y on contact
		// and the grounding block below replaces Y outright with 0. This is
		// the speed we approached the ground at — drives the hard-vs-soft land
		// pick after the grounding logic resolves.
		float inboundFallSpeed = -Velocity.Y;
		MoveAndSlide();

		if (CVars.debugSlopes.Value)
		{
			UpdateSlopeDebug();
		}

		PushTouchedMobs();
		if (_dashTimeRemaining > 0f)
		{
			HandleDashWallCollisions();
		}

		// Step down: snap back to the ground after moving. The vertical sweep
		// distance has to cover stepHeight (matching the step-up at the start
		// of the tick) PLUS the slope-induced floor drop across this tick's
		// horizontal motion — on a downhill walk the floor at the new XZ has
		// moved DOWN by horizDist * tan(slope), and a pure stepHeight sweep
		// misses it, leaving the player floating one tick. tan(FloorMaxAngle)
		// gives the worst-case walkable drop, so any walkable slope is caught.
		// Was the cause of phantom landing sounds and false skate entries on
		// 45°+ hills.
		if (wasOnFloor && _waterState != EWaterState.Swimming)
		{
			Vector3 horizDelta = GlobalPosition - posBeforeStep;
			horizDelta.Y = 0f;
			float maxSlopeDrop = horizDelta.Length() * Mathf.Tan(FloorMaxAngle);
			using KinematicCollision3D stepDownResult = MoveAndCollide(Vector3.Down * (data.stepHeight + maxSlopeDrop));
			// Match the body's own floor classifier — same threshold MoveAndSlide
			// and IsOnFloor use, editor-tunable via FloorMaxAngle on the node.
			float floorDotMin = Mathf.Cos(FloorMaxAngle);
			bool foundFloor = stepDownResult != null && stepDownResult.GetNormal().Dot(Vector3.Up) >= floorDotMin;
			if (foundFloor)
			{
				_grounded = true;
			}
			else if (stepDownResult != null)
			{
				// Hit a non-floor surface during step-down (mob capsule
				// flank, steep slope). The lift+slide bumped us into the
				// obstacle; revert Y to the pre-step floor and stay
				// grounded. Going airborne here was the bug behind the
				// land sound spamming every other tick when running into
				// a mob — wasOnFloor=true → step-up lifts → MoveAndSlide
				// hits the mob → step-down hits the mob's side → we used
				// to set _grounded=false, then next tick IsOnFloor() came
				// back true and counted as a fresh land.
				GlobalPosition = new Vector3(
					GlobalPosition.X,
					posBeforeStep.Y,
					GlobalPosition.Z
				);
				_grounded = true;
			}
			else
			{
				// No collision at all — we walked off a ledge. The
				// step-down moved us the full stepHeight before stopping,
				// which is fine; gravity will continue the fall next tick.
				GlobalPosition = new Vector3(
					GlobalPosition.X,
					posBeforeStep.Y,
					GlobalPosition.Z
				);
				_grounded = false;
			}
		}
		else
		{
			_grounded = IsOnFloor();
		}

		if (_grounded)
		{
			_jumpHeld = false;
			_coyoteTimeEndMs = 0;
			_wallJumpAirControlTimer = 0f;
			Velocity = new Vector3(Velocity.X, 0, Velocity.Z);
		}

		// Swimming overrides grounding — player is floating
		if (_waterState == EWaterState.Swimming)
		{
			_grounded = false;
		}
		if (wasOnFloor && !_grounded)
		{
			_coyoteTimeEndMs = _world.GameTimeMs + (ulong)(data.coyoteTime * 1000);
		}

		// Steep-slope sliding & skating. Order matters: detect slide contact
		// from the just-completed MoveAndSlide, then evaluate skating start /
		// exit based on (sliding, _grounded, velocity) so the Fx wiring below
		// sees the resolved state for this tick.
		UpdateSlideState();
		UpdateSkating(wasOnFloor, inboundFallSpeed);
		UpdateSlideLoop((_sliding || _skating || _skidding) && _waterState == EWaterState.None);
		// Airborne → grounded transition. Speed-gate a hard-land variant so
		// stepping off small ledges plays the soft sound; only meaningful
		// drops produce the dust-and-thud landHard. The bottom threshold
		// suppresses spurious lands from sub-frame physics jitter (e.g.
		// stepping over rough geometry); only audible drops fire either
		// variant.
		if (!wasOnFloor && _grounded && _waterState == EWaterState.None && inboundFallSpeed >= LandSoftSpeedThreshold)
		{
			bool hardLand = inboundFallSpeed >= LandHardSpeedThreshold;
			PackedScene landScene = hardLand ? _landHardFx : _landFx;
			SpawnWorldEffect(landScene);
			if (hardLand)
			{
				_sneaking = false;
			}
		}

		// Stuck-in-crevice recovery. The voxel mesher can produce geometry
		// (e.g. a 1-voxel-wide vertical crevice) where the capsule pinches
		// against wall normals at the bottom and IsOnFloor never trips —
		// gravity keeps pulling into the same wedged contact and the player
		// is frozen airborne. The "still moving" test uses actual per-tick
		// displacement, not Velocity: MoveAndSlide's slide projection only
		// cancels velocity along the contact normal, and near-vertical wall
		// normals barely touch the Y axis, so Velocity.Y runs to a huge
		// terminal value even when the body isn't moving at all. Position
		// delta is the ground truth. Swimming has its own physics and
		// forces _grounded=false so it's excluded from the check.
		if (_waterState != EWaterState.Swimming)
		{
			if (_grounded)
			{
				_safeGroundedHistory[_safeGroundedHistoryWriteIdx] = GlobalPosition;
				_safeGroundedHistoryWriteIdx = (_safeGroundedHistoryWriteIdx + 1) % SafeGroundedHistorySize;
				_stuckCheckDeadlineMs = 0;
			}
			else
			{
				float vt = _world.SimData.PlayerStuckVelocityThreshold;
				float tickThreshold = vt * dt;
				float displacementSq = (GlobalPosition - _lastTickPosition).LengthSquared();
				if (displacementSq > tickThreshold * tickThreshold || _stuckCheckDeadlineMs == 0)
				{
					_stuckCheckDeadlineMs = _world.GameTimeMs
						+ (ulong)(_world.SimData.PlayerStuckTimeoutSeconds * 1000);
				}
				else if (_world.GameTimeMs >= _stuckCheckDeadlineMs)
				{
					// Oldest entry — the slot that's about to be overwritten next
					// is the one furthest back in time. Recovers to a position
					// ~SafeGroundedHistorySize ticks ago, well clear of the
					// edge tile that launched the player into the crevice.
					Vector3 safePos = _safeGroundedHistory[_safeGroundedHistoryWriteIdx];
					GlobalPosition = safePos;
					Velocity = Vector3.Zero;
					_grounded = true;
					_stuckCheckDeadlineMs = 0;
					// Flush the history to the recovery point — otherwise the
					// next stuck event could pull the player back to the same
					// pre-recovery edge tile that's still buffered.
					for (int i = 0; i < SafeGroundedHistorySize; i++)
					{
						_safeGroundedHistory[i] = safePos;
					}
					_safeGroundedHistoryWriteIdx = 0;
				}
			}
		}
		_lastTickPosition = GlobalPosition;

		UpdateVisibility();

		// Update highlight interactive
		UpdateHighlightInteractive();

		UpdateAnimation();

		UpdateHeldItemVisual();
	}

	// Drives the in-hand model each tick off authoritative runner / animator
	// state. The weapon channel is set event-side (TryStartWeaponAction) and
	// only needs conceal toggling here; the consumable channel mirrors the live
	// Use action. A consumable in hand conceals the weapon (the potion replaces
	// the sword); so does any clip authored with AnimationData.hidesHeldItem.
	private void UpdateHeldItemVisual()
	{
		if (_heldVisual == null)
		{
			return;
		}

		PackedScene itemModel = null;
		if (_runner != null && _runner.IsBusy
			&& _runner.Current.context.sourceSlot == EInventorySlot.Consumable)
		{
			itemModel = _runner.Current.context.primaryItem?.data?.heldModel;
		}
		_heldVisual.SetActiveItem(itemModel);

		// While aiming, draw the equipped ranged weapon so the bow is in hand
		// for the full aim/draw — not just once an attack fires (the event-side
		// SetWeapon in TryStartWeaponAction). The weapon channel is persistent,
		// so the bow stays in hand after aim ends; a later melee swing swaps it
		// back. Only ranged weapons are forced here — aiming with nothing but a
		// melee weapon equipped leaves the existing held model untouched.
		if (_aiming)
		{
			WeaponState ranged = _inventory?.GetWeapon(EInventorySlot.WeaponRight);
			PackedScene rangedModel = ranged?.data?.heldModel;
			if (rangedModel != null)
			{
				_heldVisual.SetWeapon(rangedModel, ranged.data.wieldHand);
				// The drawn bow becomes the wielded weapon, so its anim set drives
				// the stance / charge poses while aiming and after aim ends.
				_wieldedWeapon = ranged;
				ValidateAnimSet(ranged.data.animSet, ranged.data.displayName);
			}
		}

		bool animHides = data != null && _animator != null
			&& data.AnimationHidesHeldItem(_animator.CurrentAnimation);
		_heldVisual.SetWeaponConcealed(itemModel != null || animHides);
	}

	// Aim deflection from the mouse path. `deflection01` is the virtual aim
	// cursor's offset from center divided by the disk radius — already in
	// [0, 1] magnitude so it matches the gamepad right-stick convention and
	// Positional aim sees a consistent rate input regardless of device.
	// Directional aim only reads the direction (atan2) so the magnitude
	// change is invisible there.
	public void ProcessMouseMotion(Vector2 deflection01, float cameraYaw)
	{
		_inputLook = new Vector3(deflection01.X, 0, deflection01.Y).Rotated(Vector3.Up, cameraYaw);
	}

	void HandleInteractInput()
	{
		if (InteractMenuOpen)
		{
			return;
		}
		ulong now = _world?.GameTimeMs ?? 0;
		if (Input.IsActionJustPressed("Interact"))
		{
			if (_curInteractive != null)
			{
				CancelInteract();
				return;
			}
			if (_highlightInteractive != null && _highlightInteractive.CanActorInteract(this))
			{
				Godot.Collections.Array<InteractiveAction> actions = _highlightInteractive.GetActions(this);
				if (actions != null && actions.Count > 1)
				{
					_interactPressActive = true;
					_interactHoldStartMs = now;
					InteractHoldProgress = 0f;
					return;
				}
				if (actions != null && actions.Count == 1)
				{
					if (TryStartInteractiveAction(_highlightInteractive))
					{
						_highlightInteractive = null;
						onHighlightChanged?.Invoke(null);
					}
				}
			}
		}
		if (_interactPressActive)
		{
			ulong elapsed = now > _interactHoldStartMs ? now - _interactHoldStartMs : 0;
			InteractHoldProgress = Mathf.Clamp((float)elapsed / InteractHoldDurationMs, 0f, 1f);
			bool stillHeld = Input.IsActionPressed("Interact");
			if (!stillHeld)
			{
				_interactPressActive = false;
				InteractHoldProgress = 0f;
				// Tap (released before threshold): start the default action.
				if (_highlightInteractive != null && _highlightInteractive.CanActorInteract(this))
				{
					if (TryStartInteractiveAction(_highlightInteractive))
					{
						_highlightInteractive = null;
						onHighlightChanged?.Invoke(null);
					}
				}
			}
			else if (elapsed >= InteractHoldDurationMs)
			{
				_interactPressActive = false;
				InteractMenuOpen = true;
				onInteractMenuOpenRequested?.Invoke();
			}
		}
	}

	void CancelInteract()
	{
		// If the runner is mid-interactive, abort it so completionEvents
		// don't fire. Weapon actions are gated by their own canAbort flag
		// inside TryAbort, which interactive actions skip — they always
		// abort cleanly.
		if (_runner != null && _runner.IsBusy && _runner.Current.interactiveAction != null)
		{
			_runner.TryAbort();
		}
		SetCurInteractive(null);
		_highlightInteractive = null;
		onHighlightChanged?.Invoke(null);
	}

	static readonly Dictionary<EInventorySlot, string> _weaponActions = new()
	{
		{ EInventorySlot.WeaponLeft, "AttackMelee" },
		{ EInventorySlot.WeaponRight, "AttackRanged" }
	};
	// Zero the cached input vectors so _PhysicsProcess stops applying the
	// last-known stick deflection while gameplay input is suppressed (e.g.
	// inventory open). Without this, opening a modal mid-movement leaves the
	// player coasting in the held direction since ProcessInput is the only
	// thing that refreshes these.
	public void ClearInput()
	{
		_inputMove = Vector3.Zero;
		_inputLook = Vector3.Zero;
	}

	public void ProcessInput(float cameraYaw)
	{
		Vector2 move = Vector2.Zero;
		move.X -= Input.GetActionStrength("MoveLeft");
		move.X += Input.GetActionStrength("MoveRight");
		move.Y -= Input.GetActionStrength("MoveUp");
		move.Y += Input.GetActionStrength("MoveDown");
		move = move.LengthSquared() > 1 ? move.Normalized() : move;
		_inputMove = new Vector3(move.X, 0, move.Y).Rotated(Vector3.Up, cameraYaw);

		// Look input. Gamepad: every frame from the right-stick axes (stick
		// centered → _inputLook = Zero, so the rotation block falls back to
		// move direction). KBM: ProcessMouseMotion only writes _inputLook
		// while Aim is held (GameClient gates the motion event), but a stale
		// _inputLook can survive an Aim release until the next mouse event,
		// so explicitly zero it on KBM frames without Aim to guarantee the
		// rotation block sees a clean state.
		if (InputDevice.Current == InputDevice.EDevice.Gamepad)
		{
			Vector2 look = Vector2.Zero;
			look.X -= Input.GetActionStrength("LookLeft");
			look.X += Input.GetActionStrength("LookRight");
			look.Y -= Input.GetActionStrength("LookUp");
			look.Y += Input.GetActionStrength("LookDown");
			look = look.LengthSquared() > 1 ? look.Normalized() : look;
			_inputLook = new Vector3(look.X, 0, look.Y).Rotated(Vector3.Up, cameraYaw);
		}
		else if (!Input.IsActionPressed("Aim"))
		{
			_inputLook = Vector3.Zero;
		}

		// Riding a vehicle: keep the steering vectors computed above (the boat
		// reads MountMoveInput) but drop every other action press — the only
		// control while mounted is Interact to dismount. Dismount reparents the
		// rider out of the vehicle; safe here because ProcessInput runs from
		// _Process, not the physics flush.
		if (_mount != null)
		{
			if (Input.IsActionJustPressed("Interact"))
			{
				Dismount();
			}
			return;
		}

		// Bird's-eye lock drops every action press for the duration of the
		// overview shot. Movement velocity is already gated by the
		// _runner.LocksMovement check farther down, but we still need to drop
		// jump / dash / weapon presses so a held button while the camera is
		// up can't punch through the lock. ui_cancel is handled by GameClient
		// since it shares ESC with TogglePause and needs to consume the input
		// before TogglePause sees it.
		if (_birdsEye)
		{
			_inputMove = Vector3.Zero;
			_inputLook = Vector3.Zero;
			return;
		}

		// Hitstun rejects every action press for the duration of the flinch.
		// Movement / look input has already been latched above so the body
		// keeps coasting in the held direction (subject to knockback velocity);
		// what we drop is interact, jump, dash, weapon attacks, consumables,
		// and the sneak toggle. The runner is still allowed to tick down its
		// in-flight action on its own so wind-downs complete naturally.
		if (_hitstunTime > 0f)
		{
			return;
		}

		// InteractCancel shares its gamepad binding with Interact, so only
		// consume the frame when there is actually something for it to abort:
		// a runner-driven interactive, or a weapon mid-charge. Otherwise fall
		// through and let Interact (and the other action presses) fire on the
		// same input event.
		if (Input.IsActionJustPressed("InteractCancel") && _runner != null && _runner.IsBusy)
		{
			if (_runner.Current.interactiveAction != null)
			{
				CancelInteract();
				return;
			}
			// Charging always aborts via TryAbort — bail out of a charged
			// weapon without releasing it into a swing/shot.
			if (_runner.Phase == EActionPhase.Charging)
			{
				_runner.TryAbort();
				return;
			}
		}

		// Handle interact input. Multi-action interactives split tap vs hold:
		// a tap (release before InteractHoldDurationMs) runs the default
		// action; a hold past the threshold raises the options modal via
		// onInteractMenuOpenRequested. Single-action interactives still run
		// on JustPressed so the snappy feel is preserved.
		HandleInteractInput();

		if (Input.IsActionJustPressed("Jump") || Input.IsActionJustPressed("UseItem") || Input.IsActionJustPressed("AttackMelee") || Input.IsActionJustPressed("AttackContextSensitive") || Input.IsActionJustPressed("Dash"))
		{
			CancelInteract();
		}

		// Sneak is broken by overt actions: jumping, swinging, firing, using
		// a consumable. Gated on input intent rather than action success so a
		// pressed-but-blocked attack (no ammo, runner busy) still ends sneak —
		// the player is plainly not trying to stay quiet.
		if (Input.IsActionJustPressed("Jump")
			|| Input.IsActionJustPressed("AttackMelee")
			|| Input.IsActionJustPressed("AttackRanged")
			|| Input.IsActionJustPressed("AttackContextSensitive")
			|| Input.IsActionJustPressed("UseItem")
			|| Input.IsActionJustPressed("Dash"))
		{
			_sneaking = false;
		}

		if (Input.IsActionJustPressed("ConsumableCycleLeft"))
		{
			_inventory?.CycleConsumable(-1);
		}
		if (Input.IsActionJustPressed("ConsumableCycleRight"))
		{
			_inventory?.CycleConsumable(+1);
		}
		if (Input.IsActionJustPressed("ConsumableSelect1"))
		{
			_inventory?.SelectConsumable(0);
		}
		if (Input.IsActionJustPressed("ConsumableSelect2"))
		{
			_inventory?.SelectConsumable(1);
		}
		if (Input.IsActionJustPressed("ConsumableSelect3"))
		{
			_inventory?.SelectConsumable(2);
		}

		// Sneak is a toggle. Pressing also doubles as the player-initiated
		// abort key while a runner action is in flight (charging always
		// cancels; Active cancels only if the selected tier opts in via
		// canAbort). A successful abort consumes the press — the player
		// wanted to bail out of the attack, not also flip into sneak.
		if (Input.IsActionJustPressed("Sneak"))
		{
			_sneaking = !_sneaking;
		}

		if (Input.IsActionJustPressed("UseItem"))
		{
			TryUseActiveConsumable();
		}
		if (Input.IsActionJustReleased("UseItem"))
		{
			ReleaseUseConsumable();
		}

		if (Input.IsActionJustPressed("Jump"))
		{
			bool swimSurfaceJump = _waterState == EWaterState.Swimming && GlobalPosition.Y >= _waterSurfaceY - data.waterJumpOffset;
			// Skating routes to the ground-jump branch (preserves XZ momentum)
			// and exits skate mode. The intent is a "skate jump" that lets the
			// player chain ramps or launch off the bottom of a slope with their
			// accumulated speed intact — not a wall-jump kick away from the
			// slope normal.
			if (_grounded || _world.GameTimeMs < _coyoteTimeEndMs || swimSurfaceJump || _skating)
			{
				float jumpSpeed = swimSurfaceJump ? data.swimJumpSpeed : data.jumpSpeed;
				Velocity = new Vector3(Velocity.X, jumpSpeed, Velocity.Z);
				_grounded = false;
				_coyoteTimeEndMs = 0;
				_jumpHeld = true;
				if (_skating && CVars.debugSlopes.Value)
				{
					string ts = System.DateTime.Now.ToString("HH:mm:ss.fff");
					Vector3 horizVel = new(Velocity.X, 0f, Velocity.Z);
					GD.Print($"[skate] EXIT  {ts} speed={horizVel.Length():F1}m/s (jump)");
				}
				_skating = false;
				_skateContactLostMs = 0;
				PlayOneShot(EAnimation.Jump);
				SpawnWorldEffect(_jumpFx);
			}
			else if (_waterState == EWaterState.Swimming)
			{
				Velocity = new Vector3(Velocity.X, data.swimVerticalSpeed, Velocity.Z);
			}
			else
			{
				TryWallJump();
			}
		}
		else if (!Input.IsActionPressed("Jump"))
		{
			_jumpHeld = false;
		}

		if (Input.IsActionJustPressed("Dash"))
		{
			TryStartDash();
		}

		// Convert a pending pre-cooldown press if the player is still holding
		// the button and the cooldown has now elapsed. Runs before this
		// frame's JustPressed handling so a press that lands on the exact
		// frame the cooldown expires still goes through TryStartWeaponAction
		// normally — the pending field is only set when cooldown is in flight.
		if (_pendingWeaponPressSlot is EInventorySlot pendingSlot)
		{
			if (!Input.IsActionPressed(_pendingWeaponPressActionName))
			{
				_pendingWeaponPressSlot = null;
				_pendingWeaponPressActionName = null;
			}
			else if (_runner != null && !_runner.IsBusy)
			{
				WeaponState pendingWeapon = GetMeleeWeaponOrUnarmed(pendingSlot);
				ulong nowMs = _world?.GameTimeMs ?? 0;
				if (pendingWeapon != null && pendingWeapon.cooldownExpireMs <= nowMs)
				{
					TryStartWeaponAction(pendingSlot, _pendingWeaponPressActionName);
				}
			}
		}

		foreach (var (slot, actionName) in _weaponActions)
		{
			if (Input.IsActionJustPressed(actionName))
			{
				TryStartWeaponAction(slot, actionName);
			}
			if (Input.IsActionJustReleased(actionName))
			{
				ReleaseWeaponAction(slot);
			}
		}

		// AttackContextSensitive routes to ranged when Aim is held at press
		// time, melee otherwise. Slot is latched until release so a mid-press
		// Aim toggle doesn't switch which weapon's release fires.
		if (Input.IsActionJustPressed("AttackContextSensitive"))
		{
			EInventorySlot slot = Input.IsActionPressed("Aim")
				? EInventorySlot.WeaponRight
				: EInventorySlot.WeaponLeft;
			_contextSensitiveAttackSlot = slot;
			TryStartWeaponAction(slot, "AttackContextSensitive");
		}
		if (Input.IsActionJustReleased("AttackContextSensitive") && _contextSensitiveAttackSlot is EInventorySlot latchedSlot)
		{
			ReleaseWeaponAction(latchedSlot);
			_contextSensitiveAttackSlot = null;
		}
	}

	bool TryGetWeaponState(EInventorySlot slot, out WeaponState weapon)
	{
		weapon = _inventory?.GetWeapon(slot);
		return weapon != null;
	}

	private void UpdateHighlightInteractive()
	{
		if (_curInteractive != null)
		{
			return;
		}

		IInteractive prevHighlight = _highlightInteractive;

		if (_interactiveCollisions.Count == 0)
		{
			_highlightInteractive = null;
		}
		else
		{
			IInteractive closest = null;
			float closestDist = float.MaxValue;
			foreach (IInteractive interactive in _interactiveCollisions)
			{
				if (interactive is Node3D node && interactive.CanActorInteract(this))
				{
					float dist = GlobalPosition.DistanceSquaredTo(node.GlobalPosition);
					if (dist < closestDist)
					{
						closestDist = dist;
						closest = interactive;
					}
				}
			}
			_highlightInteractive = closest;
		}

		if (_highlightInteractive != prevHighlight)
		{
			onHighlightChanged?.Invoke(_highlightInteractive as Node3D);
		}
	}

	private void OnInteractAreaEntered(Area3D area)
	{
		if (area is InteractiveBox box && box.Interactive != null)
		{
			_interactiveCollisions.Add(box.Interactive);
		}
	}

	private void OnInteractAreaExited(Area3D area)
	{
		if (area is InteractiveBox box && box.Interactive != null)
		{
			_interactiveCollisions.Remove(box.Interactive);
		}
	}

	private void UpdateTerrainSpeed()
	{
		_terrainSpeed = 1f;
		foreach (Foliage foliage in _foliageCollisions)
		{
			_terrainSpeed = Mathf.Min(_terrainSpeed, foliage.speed);
		}
	}

	// React to walls hit by an in-flight dash. Head-on contact (dash direction
	// within data.dashWallHeadOnAngle of the wall normal) short-circuits the
	// dash, dropping into the glide window. Glancing contact reprojects the
	// dash direction onto the wall plane so the next tick continues at full
	// dash speed along the tangent — MoveAndSlide has already removed the
	// into-wall component from Velocity, but without reprojecting _dashDir the
	// next tick would push back into the wall again. Skips floors and ceilings:
	// step-up / step-down handles ground transitions, and a head-bonk on a
	// ceiling shouldn't kill horizontal momentum.
	// Tear down dash physics at the end of the dash phase: zero the timer
	// and arm the glide window so velocity tapers instead of snapping.
	// Called from natural timeout and the head-on wall short-circuit. The
	// i-frame status effect is runner-managed (applied at t=0 via an
	// ApplyStatusEffect event, auto-expires on its own duration timer), so
	// a wall short-circuit at t<duration leaves a small invuln tail — fine.
	// Airborne wall-jump probe. Sweeps the player's collider
	// wallJumpCheckDistance forward in the movement/yaw direction; on a hit
	// whose normal is steeper than the walkable floor cutoff (cos FloorMaxAngle)
	// and not an overhang (n.Y >= 0), the player's velocity is replaced with
	// the wall-jump kick: vertical = wallJumpSpeedY, horizontal = (wall normal
	// × wallJumpSpeedXZ) + the tangent component of incoming velocity. The
	// normal-aligned kick gives a predictable peel-off independent of approach
	// angle; preserving the full tangent keeps along-wall momentum (Mirror's
	// Edge / Titanfall style) so wall-running into a wall jump reads as
	// continuous rather than rebounding. Gated on Velocity.Y >
	// -wallJumpMaxFallingSpeed so a long fall can't be saved by kicking off a
	// passing wall. Cancels any in-flight dash so the dash velocity override
	// doesn't clobber the kick.
	// Linear approach of horizontal velocity toward a target at a fixed rate
	// (m/s² × dt = max step per call). Used by the water / ground / air input
	// branches to ramp velocity instead of snapping. Caller passes the XZ
	// vectors with Y already zeroed; result still has Y=0 and is recomposed
	// with the existing Velocity.Y by the caller.
	private static Vector3 ApproachXZ(Vector3 currentXZ, Vector3 target, float step)
	{
		Vector3 toTarget = target - currentXZ;
		float toTargetLen = toTarget.Length();
		if (toTargetLen <= step)
		{
			return target;
		}
		return currentXZ + toTarget * (step / toTargetLen);
	}

	private bool TryWallJump()
	{
		if (data == null || _world == null || _waterState != EWaterState.None)
		{
			return false;
		}
		if (Velocity.Y <= -data.wallJumpMaxFallingSpeed)
		{
			return false;
		}
		if (_wallJumpAirControlTimer > 0f)
		{
			return false;
		}
		if (_stamina <= 0f)
		{
			return false;
		}

		Vector3 forward;
		if (_inputMove.LengthSquared() > 0.0001f)
		{
			forward = new Vector3(_inputMove.X, 0f, _inputMove.Z).Normalized();
		}
		else
		{
			float yaw = Rotation.Y;
			forward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
		}

		using KinematicCollision3D hit = MoveAndCollide(forward * data.wallJumpCheckDistance, testOnly: true);
		if (hit == null)
		{
			return false;
		}

		Vector3 n = hit.GetNormal();
		float floorDotMin = Mathf.Cos(FloorMaxAngle);
		if (n.Y >= floorDotMin || n.Y < 0f)
		{
			return false;
		}

		// Decompose incoming horizontal velocity around the wall normal. The
		// into-wall component (Velocity · nHoriz, negative when moving into the
		// wall) is discarded; the tangent (along-wall) component is preserved
		// verbatim and added to a fixed normal-aligned kick. n.Y is in
		// [0, floorDotMin) by the gates above, so nHoriz is guaranteed non-zero.
		Vector3 nHoriz = new Vector3(n.X, 0f, n.Z).Normalized();
		Vector3 incomingXZ = new(Velocity.X, 0f, Velocity.Z);
		Vector3 tangent = incomingXZ - incomingXZ.Dot(nHoriz) * nHoriz;
		Vector3 horiz = nHoriz * data.wallJumpSpeedXZ + tangent;

		_dashTimeRemaining = 0f;
		_dashFreezeGravity = false;

		Velocity = new Vector3(horiz.X, data.wallJumpSpeedY, horiz.Z);
		_grounded = false;
		_coyoteTimeEndMs = 0;
		_jumpHeld = true;
		_wallJumpAirControlTimer = data.wallJumpAirControlTime;
		ConsumeStamina(data.wallJumpStaminaCost);

		PlayOneShot(EAnimation.Jump);
		SpawnWorldEffect(_wallJumpFootFx);
		SpawnWorldEffect(_wallJumpEffortFx);
		return true;
	}

	// Hand off from dash into normal motion. Velocity direction is preserved
	// — the magnitude is clamped horizontally to the ambient sprint speed
	// (sprintSpeed on land, swimSprintSpeed in water). Airborne dashes
	// return uncapped: air drag + gravity carry the player out of the dash
	// over the next several ticks without an explicit glide window.
	private void EndDash()
	{
		_dashTimeRemaining = 0f;
		if (data == null)
		{
			return;
		}
		float cap;
		if (_grounded)
		{
			cap = data.sprintSpeed;
		}
		else if (_waterState == EWaterState.Swimming)
		{
			cap = data.swimSprintSpeed;
		}
		else
		{
			return;
		}
		Vector3 horiz = new(Velocity.X, 0f, Velocity.Z);
		float horizSpeed = horiz.Length();
		if (horizSpeed > cap && horizSpeed > 0.001f)
		{
			Vector3 capped = horiz * (cap / horizSpeed);
			Velocity = new Vector3(capped.X, Velocity.Y, capped.Z);
		}
	}

	// Resolves the player's current slope contact in two bands:
	//   _sliding         — strict steep band [slideSurfaceMinNormalY, cos(FloorMaxAngle))
	//   _onSkateSurface  — extended skate band [slideSurfaceMinNormalY, skateContinueMaxNormalY)
	// _sliding drives the puff FX and skate initiation; _onSkateSurface is the
	// superset that keeps skate momentum alive through walkable ramp runouts.
	// _slideNormal tracks the most-upright surface in the extended band so
	// ApplySkatingMotion can project gravity onto it regardless of which
	// sub-band the player is currently riding.
	private void UpdateSlideState()
	{
		if (data == null || _waterState == EWaterState.Swimming)
		{
			_sliding = false;
			_onSkateSurface = false;
			return;
		}

		float floorDotMin = Mathf.Cos(FloorMaxAngle);
		float slideMin = data.slideSurfaceMinNormalY;
		float skateMax = data.skateContinueMaxNormalY;
		bool foundSlide = false;
		bool foundSkateSurf = false;
		Vector3 bestNormal = Vector3.Up;
		float bestY = -1f;

		int count = GetSlideCollisionCount();
		for (int i = 0; i < count; i++)
		{
			KinematicCollision3D col = GetSlideCollision(i);
			Vector3 n = col.GetNormal();
			if (n.Y < slideMin || n.Y >= skateMax)
			{
				continue;
			}
			if (n.Y > bestY)
			{
				bestY = n.Y;
				bestNormal = n;
			}
			foundSkateSurf = true;
			if (n.Y < floorDotMin)
			{
				foundSlide = true;
			}
		}

		// Airborne with no extended-band hit this tick — probe directly below
		// to catch surfaces we're about to land on (and to hold contact across
		// the brief gaps between voxel-face transitions on a discontinuous
		// slope). The probe accepts the full extended band; _sliding flips
		// only if the probe lands inside the strict steep band.
		if (!foundSkateSurf && !_grounded)
		{
			using KinematicCollision3D probe = MoveAndCollide(
				Vector3.Down * data.stepHeight, testOnly: true);
			if (probe != null)
			{
				Vector3 n = probe.GetNormal();
				if (n.Y >= slideMin && n.Y < skateMax)
				{
					bestNormal = n;
					foundSkateSurf = true;
					if (n.Y < floorDotMin)
					{
						foundSlide = true;
					}
				}
			}
		}

		_sliding = foundSlide;
		_onSkateSurface = foundSkateSurf;
		if (foundSkateSurf)
		{
			_slideNormal = bestNormal;
		}
	}

	// Skating state machine. Initiates skating when the player lands on a
	// slide surface aligned with the slope's downhill direction with enough
	// inbound momentum; exits when the slope flattens to walkable, contact
	// is lost beyond the grace window, or speed drops below the floor.
	// Jump-driven exit is handled inline in the Jump input handler.
	private void UpdateSkating(bool wasOnFloor, float inboundFallSpeed)
	{
		if (data == null || _world == null)
		{
			return;
		}

		bool wasSkating = _skating;
		UpdateSkatingInner(wasOnFloor, inboundFallSpeed);
		if (wasSkating != _skating && CVars.debugSlopes.Value)
		{
			string ts = System.DateTime.Now.ToString("HH:mm:ss.fff");
			float angle = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(_slideNormal.Y, -1f, 1f)));
			Vector3 horizVel = new(Velocity.X, 0f, Velocity.Z);
			GD.Print(_skating
				? $"[skate] ENTER {ts} slope={angle:F1}° speed={horizVel.Length():F1}m/s fall={inboundFallSpeed:F1}m/s"
				: $"[skate] EXIT  {ts} speed={horizVel.Length():F1}m/s");
		}
	}

	private void UpdateSkatingInner(bool wasOnFloor, float inboundFallSpeed)
	{
		ulong now = _world.GameTimeMs;
		if (!_skating)
		{
			// Initiation requires: just landed (not walked-into a slope),
			// current contact is in the extended skate band (anything
			// steeper than skateContinueMaxNormalY — includes walkable
			// ramps so a moderate slope can still launch a skate when
			// alignment is sharp), inbound horizontal velocity meets the
			// speed floor, the landing was an actual fall (not stepping
			// down a small ledge), and direction aligns with the slope's
			// projected-downhill direction.
			if (_onSkateSurface && !wasOnFloor && inboundFallSpeed >= data.skateInitiationMinFallSpeed)
			{
				Vector3 downhill = Vector3.Down.Slide(_slideNormal);
				Vector3 downhillHoriz = new(downhill.X, 0f, downhill.Z);
				Vector3 horizVel = new(Velocity.X, 0f, Velocity.Z);
				float dhLen = downhillHoriz.Length();
				float velLen = horizVel.Length();
				if (dhLen > 0.01f && velLen >= data.skateInitiationMinSpeed)
				{
					float align = horizVel.Dot(downhillHoriz) / (velLen * dhLen);
					if (align >= data.skateInitiationAlignDot)
					{
						_skating = true;
						_skateContactLostMs = 0;
					}
				}
			}
			return;
		}

		// Exit table.
		//  - Airborne with no slope below: run out the grace window so brief
		//    voxel-face-transition gaps don't drop the state.
		//  - On a steep (unwalkable) slope: never exit on speed — gravity
		//    will rebuild momentum even if the player has stalled or reversed
		//    direction up the slope.
		//  - On a walkable surface (skate-band ramp OR flat ground past the
		//    band): exit only when horizontal speed has decayed below moveSpeed.
		//    Lets the player carry skate momentum across ramp runouts onto
		//    flat ground until friction drains it back into normal-control
		//    territory.
		if (!_grounded && !_onSkateSurface)
		{
			if (_skateContactLostMs == 0)
			{
				_skateContactLostMs = now;
			}
			else if (now - _skateContactLostMs > SkateContactGraceMs)
			{
				_skating = false;
				_skateContactLostMs = 0;
			}
			return;
		}
		_skateContactLostMs = 0;
		if (_sliding)
		{
			return;
		}
		Vector3 horizCheck = new(Velocity.X, 0f, Velocity.Z);
		if (horizCheck.LengthSquared() < data.moveSpeed * data.moveSpeed)
		{
			_skating = false;
		}
	}

	// Skating velocity build. Steers the current XZ heading toward input
	// (yaw-rate limited), applies brake when input opposes heading,
	// integrates slope-tangent gravity into the horizontal component, drains
	// friction, and caps to skateMaxSpeed. Y is left alone here — the
	// airborne gravity branch below adds full gravity, and MoveAndSlide
	// projects out the into-slope component each tick.
	private void ApplySkatingMotion(float dt)
	{
		Vector3 horiz = new(Velocity.X, 0f, Velocity.Z);
		float horizSpeed = horiz.Length();
		Vector3 heading = horizSpeed > 0.001f
			? horiz / horizSpeed
			: new Vector3(Mathf.Sin(Rotation.Y), 0f, Mathf.Cos(Rotation.Y));

		Vector3 inputXZ = new(_inputMove.X, 0f, _inputMove.Z);
		float inputMag = inputXZ.Length();
		if (inputMag > 0.001f && horizSpeed > 0.01f)
		{
			Vector3 inputDir = inputXZ / inputMag;
			float currentYaw = Mathf.Atan2(heading.X, heading.Z);
			float targetYaw = Mathf.Atan2(inputDir.X, inputDir.Z);
			float yawDelta = Mathf.Wrap(targetYaw - currentYaw, -Mathf.Pi, Mathf.Pi);
			float maxStep = data.skateSteerYawRate * inputMag * dt;
			float yawStep = Mathf.Clamp(yawDelta, -maxStep, maxStep);
			float newYaw = currentYaw + yawStep;
			heading = new Vector3(Mathf.Sin(newYaw), 0f, Mathf.Cos(newYaw));

			// Brake when input meaningfully opposes the (newly-steered) heading.
			float align = inputDir.Dot(heading);
			if (align < -data.skateBrakeDotThreshold)
			{
				horizSpeed = Mathf.Max(0f, horizSpeed - data.skateBrakeDecel * -align * dt);
			}
		}

		// Slope-tangent gravity adds momentum along the slope's downhill
		// projection. Adds the XZ part to the heading-scaled velocity; the
		// perpendicular-to-heading component naturally curves the trajectory
		// toward downhill when input is held off-axis. When the player has
		// glided past the skate band onto effectively flat ground we use Up
		// as the normal — the projection collapses to zero tangent gravity,
		// so only friction drains the carried momentum.
		Vector3 surfaceNormal = _onSkateSurface ? _slideNormal : Vector3.Up;
		Vector3 gravityVec = Vector3.Down * _world.SimData.Gravity;
		Vector3 gravityAlongSlope = gravityVec - gravityVec.Dot(surfaceNormal) * surfaceNormal;
		Vector3 newHoriz = heading * horizSpeed
			+ new Vector3(gravityAlongSlope.X, 0f, gravityAlongSlope.Z) * dt;

		// Friction is applied to the magnitude after gravity injection so a
		// shallow slope reaches a finite terminal speed.
		float newSpeed = newHoriz.Length();
		if (newSpeed > 0.001f)
		{
			float drop = Mathf.Min(newSpeed, data.skateFriction * dt);
			newSpeed -= drop;
			newHoriz = newHoriz.Normalized() * newSpeed;
		}

		if (newSpeed > data.skateMaxSpeed)
		{
			newHoriz = newHoriz * (data.skateMaxSpeed / newSpeed);
		}

		Velocity = new Vector3(newHoriz.X, Velocity.Y, newHoriz.Z);
	}

	// Publishes floor angle + unwalkable-wall hits to the static Debug* fields
	// so DiagnosticsOverlay can render them, and prints a per-hit log line
	// throttled to changes ≥2° or stale ≥500ms. "Unwalkable" here means an
	// upward-facing surface (n.Y > 0) whose normal is below cos(FloorMaxAngle)
	// — i.e. a slope the body classifies as wall, not floor. Vertical walls
	// (n.Y ≈ 0) and overhangs (n.Y < 0) are skipped: the question is "what
	// ramp face just stopped the climb", not "did we run into a cliff".
	private void UpdateSlopeDebug()
	{
		float floorDotMin = Mathf.Cos(FloorMaxAngle);

		if (IsOnFloor())
		{
			Vector3 fn = GetFloorNormal();
			DebugFloorAngleDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(fn.Y, -1f, 1f)));
		}
		else
		{
			DebugFloorAngleDeg = float.NaN;
		}

		bool moving = _inputMove.LengthSquared() > 0.0001f;
		int count = GetSlideCollisionCount();
		for (int i = 0; i < count; i++)
		{
			using KinematicCollision3D c = GetSlideCollision(i);
			Vector3 n = c.GetNormal();
			if (n.Y <= 0f || n.Y >= floorDotMin)
			{
				continue;
			}
			float angleDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(n.Y, -1f, 1f)));
			Vector3 pos = c.GetPosition();

			DebugLastWallAngleDeg = angleDeg;
			DebugLastWallNormal = n;
			DebugLastWallPosition = pos;
			DebugLastWallHitMs = _world?.GameTimeMs ?? 0;
			DebugHasWallHit = true;

			if (!moving)
			{
				continue;
			}
			bool angleChanged = float.IsNaN(_debugLastLoggedWallAngle)
				|| Mathf.Abs(angleDeg - _debugLastLoggedWallAngle) > 2f;
			ulong nowMs = _world?.GameTimeMs ?? 0;
			bool stale = nowMs == 0 || (nowMs - _debugLastWallLogMs) > 500;
			if (!angleChanged && !stale)
			{
				continue;
			}
			_debugLastLoggedWallAngle = angleDeg;
			_debugLastWallLogMs = nowMs;
			GD.Print($"[slope] wall hit angle={angleDeg:F1}° normal=({n.X:F2},{n.Y:F2},{n.Z:F2}) at ({pos.X:F2},{pos.Y:F2},{pos.Z:F2})");
		}
	}

	private void HandleDashWallCollisions()
	{
		float floorDotMin = Mathf.Cos(FloorMaxAngle);
		float headOnDot = Mathf.Cos(data.dashWallHeadOnAngle);
		int count = GetSlideCollisionCount();
		for (int i = 0; i < count; i++)
		{
			using KinematicCollision3D c = GetSlideCollision(i);
			Vector3 n = c.GetNormal();
			if (Mathf.Abs(n.Y) >= floorDotMin)
			{
				continue;
			}
			float hitDot = -_dashDir.Dot(n);
			if (hitDot >= headOnDot)
			{
				EndDash();
				return;
			}
			Vector3 tangent = _dashDir - _dashDir.Dot(n) * n;
			if (tangent.LengthSquared() > 1e-6f)
			{
				_dashDir = tangent.Normalized();
			}
		}
	}

	// Mobs the player is currently overlapping get nudged toward a target
	// horizontal velocity along the player's direction of travel. The push
	// is a velocity TARGET, not a per-frame impulse — running into a mob
	// for many ticks doesn't compound, so corpses / merchants no longer
	// fly across the map. Skips dead and Freeze-pinned mobs entirely so
	// settled corpses and idle-locked NPCs stay where they are. Player no
	// longer carries the Mob bit in its CollisionMask, so MoveAndSlide
	// can't surface mob contacts; the overlap query against MobSpatialHash
	// is now the only path that turns "player touches mob" into a reaction.
	private static readonly List<Mob> _pushTouchedScratch = [];
	private void PushTouchedMobs()
	{
		if (data == null || data.mobPushStrength <= 0f)
		{
			return;
		}
		MobSpatialHash hash = _world?.MobSpatialHash;
		if (hash == null)
		{
			return;
		}
		Vector3 vel = Velocity;
		vel.Y = 0f;
		float speed = vel.Length();
		if (speed < 0.01f)
		{
			return;
		}
		Vector3 dir = vel / speed;
		// Tangent is dir rotated 90° clockwise in XZ (right-hand). Used to
		// score how off-center the player hit the mob and to apply the
		// "slip" impulse that scoots the mob out of the player's path.
		Vector3 tangent = new Vector3(-dir.Z, 0f, dir.X);
		// Capping the mob's resulting horizontal speed (along the push
		// direction) at speed * mobPushStrength is the fix for the
		// "merchant flies off the map" bug — without it, every physics
		// tick of contact added another mass²-amplified impulse.
		float maxPushSpeed = speed * data.mobPushStrength;
		float maxSlipSpeed = speed * data.mobPushSlip;
		// 1m covers the widest player+mob capsule overlap (player 0.25 +
		// goblin/villager 0.35 = 0.6, padded so a single fast tick doesn't
		// step past the contact band before this runs).
		const float QueryRadius = 1f;
		const float ContactRadius = 0.6f;
		const float ContactRadiusSq = ContactRadius * ContactRadius;
		const float MaxVerticalSeparation = 1.5f;

		_pushTouchedScratch.Clear();
		hash.QueryRadius(GlobalPosition, QueryRadius, _pushTouchedScratch);

		Vector3 playerPos = GlobalPosition;
		for (int i = 0; i < _pushTouchedScratch.Count; i++)
		{
			Mob mob = _pushTouchedScratch[i];
			if (mob == null || !mob.alive || mob.Freeze)
			{
				continue;
			}
			Vector3 toMob = mob.GlobalPosition - playerPos;
			if (Mathf.Abs(toMob.Y) > MaxVerticalSeparation)
			{
				continue;
			}
			float distSq = toMob.X * toMob.X + toMob.Z * toMob.Z;
			if (distSq > ContactRadiusSq)
			{
				continue;
			}
			Vector3 mobVel = mob.LinearVelocity;

			// Forward push: top up the mob's velocity along player heading
			// to maxPushSpeed. No-op if the mob is already going faster.
			float currentAlong = mobVel.X * dir.X + mobVel.Z * dir.Z;
			float deltaAlong = currentAlong < maxPushSpeed ? maxPushSpeed - currentAlong : 0f;

			// Slip: nudge the mob sideways AWAY from the player's path,
			// proportional to how off-center the contact was. A dead-center
			// hit gives no slip; a graze on the edge gives full slip. Sign
			// of lateralOffset picks left vs. right; only push further away
			// (never pull the mob across the player's path).
			float lateralOffset = toMob.X * tangent.X + toMob.Z * tangent.Z;
			float slipScale = Mathf.Clamp(lateralOffset / ContactRadius, -1f, 1f);
			float currentLateral = mobVel.X * tangent.X + mobVel.Z * tangent.Z;
			float targetLateral = maxSlipSpeed * slipScale;
			float deltaLateral = 0f;
			if (slipScale > 0f && currentLateral < targetLateral)
			{
				deltaLateral = targetLateral - currentLateral;
			}
			else if (slipScale < 0f && currentLateral > targetLateral)
			{
				deltaLateral = targetLateral - currentLateral;
			}

			if (deltaAlong == 0f && deltaLateral == 0f)
			{
				continue;
			}
			// ApplyImpulse divides by mass internally, so multiply by mass
			// here to make the resulting velocity change exactly the
			// (deltaAlong, deltaLateral) pair regardless of mob mass.
			Vector3 impulse = (dir * deltaAlong + tangent * deltaLateral) * mob.Mass;
			mob.ApplyImpulse(new Vector3(impulse.X, 0f, impulse.Z));
		}
	}

	private void UpdateVisibility()
	{
		float targetLightMax = _world.SimData?.TargetLightMax ?? 0.75f;
		float lightFactor = targetLightMax > 0f ? Mathf.Clamp(_world.GetPerceivedLight(GlobalPosition) / targetLightMax, 0, 1) : 0f;

		float speedFactor = data.moveSpeed > 0f ? Mathf.Clamp(Mathf.Pow(Velocity.Length() / data.moveSpeed, data.visibilityMovementPower), data.visibilityMovementMin, 1f) : 1f;

		float camouflage = 0f;
		foreach (Foliage foliage in _foliageCollisions)
		{
			camouflage = Mathf.Max(camouflage, foliage.camouflage);
		}

		visibility = Mathf.Clamp(lightFactor * speedFactor * (1.0f - camouflage), 0f, 1f);
		visibilityLight = lightFactor;
		visibilitySpeed = speedFactor;
		visibilityCamouflage = Mathf.Max(0f, 1f - camouflage);

		Vector3 horizVel = Velocity;
		horizVel.Y = 0f;
		CurrentDecibels = PlayerPerception.ComputeMovementDecibels(horizVel.Length(), data.sneakSpeed, data.moveSpeed, data.sneakDecibels, data.runDecibels);
	}

	public void AddTerrainModifier(Foliage foliage)
	{
		_foliageCollisions.Add(foliage);
	}

	public void RemoveTerrainModifier(Foliage foliage)
	{
		_foliageCollisions.Remove(foliage);
	}

	public void WaterAreaEntered()
	{
		_waterOverlapCount++;
		if (_waterOverlapCount == 1)
		{
			// Pick plunge over splash when the player drops in fast. Velocity.Y
			// at this signal still reflects inbound fall speed — water is an
			// Area3D, not a colliding body, so MoveAndSlide hasn't zeroed Y.
			float fallSpeed = -Velocity.Y;
			PackedScene scene = (fallSpeed >= WaterPlungeSpeedThreshold && _waterPlungeFx != null)
				? _waterPlungeFx
				: _waterEnterSplashFx;
			SpawnWorldEffect(scene);
			OnWaterEnter?.Invoke(this);
		}
	}

	public void WaterAreaExited()
	{
		_waterOverlapCount--;
		if (_waterOverlapCount == 0)
		{
			OnWaterExit?.Invoke(this);
		}
	}

	private void UpdateWaterState()
	{
		EWaterState prev = _waterState;
		int fx = Mathf.FloorToInt(GlobalPosition.X);
		int fy = Mathf.FloorToInt(GlobalPosition.Y);
		int fz = Mathf.FloorToInt(GlobalPosition.Z);

		VoxelType voxelAtFeet = _world.WorldState.GetVoxelWorld(fx, fy, fz);
		if (voxelAtFeet != VoxelType.Water)
		{
			_waterState = EWaterState.None;
			return;
		}

		VoxelType voxelAtBody = _world.WorldState.GetVoxelWorld(fx, fy + 1, fz);
		VoxelType voxelBelow = _world.WorldState.GetVoxelWorld(fx, fy - 1, fz);

		if (voxelAtBody == VoxelType.Water)
		{
			_waterState = EWaterState.Swimming;
		}
		else if (VoxelTypeInfo.IsSolid(voxelBelow))
		{
			_waterState = EWaterState.Shallow;
		}
		else
		{
			_waterState = EWaterState.Swimming;
		}

		// Compute water surface Y by scanning upward
		int scanY = fy;
		while (_world.WorldState.GetVoxelWorld(fx, scanY, fz) == VoxelType.Water)
		{
			scanY++;
		}
		_waterSurfaceY = scanY;

		// Going over your head breaks sneak — splashing in is plainly audible.
		// Only the swim-edge counts; wading through shallows is fine.
		if (prev != EWaterState.Swimming && _waterState == EWaterState.Swimming)
		{
			_sneaking = false;
		}
	}

	private void ApplyWaterPhysics(float dt)
	{
		float targetY = _waterSurfaceY - data.waterSurfaceOffset;
		float depthBelowSurface = targetY - GlobalPosition.Y;

		if (depthBelowSurface > 0f)
		{
			Velocity += Vector3.Up * Mathf.Min(depthBelowSurface, 1f) * data.buoyancyAcceleration * dt;
		}
		else
		{
			Velocity += Vector3.Down * data.buoyancyAcceleration * 0.5f * dt;
		}

		// Drag to damp vertical oscillation and bleed off horizontal entry
		// momentum. Y uses waterDrag (sized to kill the buoyancy bounce); XZ
		// uses waterHorizontalDrag, applied to the velocity RELATIVE to the
		// local water current so a stationary swimmer in a current drifts
		// downstream rather than being dragged toward zero. The current ×
		// waterCurrentDrag drift target matches the one the swim approach
		// uses (see the EWaterState.Swimming branch in _PhysicsProcess), so
		// at steady-state with no input the player parks at exactly the
		// drift target.
		float horizDecay = 1f - data.waterHorizontalDrag * dt;
		if (horizDecay < 0f)
		{
			horizDecay = 0f;
		}
		Vector3 waterCurrent = _world.WorldState.SampleWaterCurrent(GlobalPosition);
		Vector3 driftTarget = new Vector3(waterCurrent.X, 0f, waterCurrent.Z) * data.waterCurrentDrag;
		float newX = driftTarget.X + (Velocity.X - driftTarget.X) * horizDecay;
		float newZ = driftTarget.Z + (Velocity.Z - driftTarget.Z) * horizDecay;
		Velocity = new Vector3(
			newX,
			Velocity.Y - Velocity.Y * data.waterDrag * dt,
			newZ);

		// Water current is folded into the swim-acceleration target above
		// (see the EWaterState.Swimming branch in _PhysicsProcess) rather
		// than re-added per tick, so input-driven inertia and current drift
		// can't compound across frames.

		// Clamp sinking speed
		if (Velocity.Y < -data.waterSinkSpeed)
		{
			Velocity = new Vector3(Velocity.X, -data.waterSinkSpeed, Velocity.Z);
		}
	}

}
