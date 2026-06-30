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
	// CharacterCreationState.playerName at Initialize. Defaults to a placeholder
	// so the UI always has something to show even before / without spawn data.
	public string PlayerName { get; private set; } = "Wyatt Anderson";
	[Export] public Area3D interactArea;
	// World-space anchor (head height) used to project a screen-space point
	// above the player for HUD elements that float over the character — e.g.
	// the transient status-effect notification. Mirrors Mob.HudAnchor.
	[Export] public Node3D hudAnchor;
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
	// Authored head-height local pose of _audioListener (player.tscn). Restored
	// when the bird's-eye world-position override is cleared.
	private static readonly Vector3 AUDIO_LISTENER_REST_POS = new Vector3(0f, 1f, 0f);
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

	// Status effect armed while the player is caked in mud. Carries the
	// MoveSpeed penalty (you slog) and the Scent modifier < 1 (the mud masks
	// your smell), plus the HUD icon. TickMuddyEffect drives its ContinuousArm
	// meter up while the player stands on EGroundType.Mud terrain and snaps it
	// to zero the moment they enter water — mud rinses off when you get wet.
	[Export] private StatusEffectData _muddyEffectData;

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
	// Per-gender player voice-over banks, keyed by (int)EGender. Each value is a
	// PlayerVoiceData carrying the hurt cry / death cry / out-of-breath gasp
	// scenes — the voice clips ride on top of the shared, gender-agnostic
	// impact / death-splat / breath-puff effects so only the vocal layer varies
	// by body type. Initialize resolves the spawned gender's bank into _voice
	// (Female fallback), mirroring the model-package map. A gender with no entry
	// falls back to Female; a null slot inside a bank just stays silent.
	[Export] private Godot.Collections.Dictionary<int, VoiceData> _voices = new();
	// The live voice bank for the spawned gender, resolved in Initialize.
	private VoiceData _voice;
	// Armor lifecycle one-shots. Depleted plays the moment armor hits zero
	// from damage; rechargeStart plays when the post-hit recharge delay
	// elapses and the bar starts climbing again; recoverStart replaces it
	// when the recharge follows a full depletion (longer recover delay).
	[Export] private PackedScene _armorDepletedFx;
	[Export] private PackedScene _armorRechargeStartFx;
	[Export] private PackedScene _armorRecoverStartFx;
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
	// Dirt puff spawned at the player's feet on each shovel scoop, fired from a
	// Call Method Track on the dig clip (ModelAnimator.OnDigDirt). Authored in
	// the player .tscn; null = no synced puff.
	[Export] private PackedScene _digDirtFx;
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

	// True while the camp screen (opened at a lit campfire) is up. Drives the
	// SitIdle pose in UpdateAnimation; EnterCamp also conceals the player from
	// mobs so a wandering threat can't attack through the modal. The model stays
	// visible (the player sits by the fire) — distinct from the tree-climb
	// conceal, which hides the model entirely.
	bool _camping;
	public bool IsCamping => _camping;

	// Latches true once the player has actively entered a fight this combat:
	// taken damage from a mob, dealt weapon damage to a mob (any weapon, melee /
	// hitscan / projectile — all route through Mob.Damage), or swung a weapon
	// while a triggered hostile was within _combatEngageRange (TryEngageCombatFromWeaponUse,
	// so the latch flips on a committed miss too). A guard companion reads this
	// (via ThreatPerceivedCondition.requirePlayerCombat) to hold at a wary growl
	// until the player chooses to fight rather than picking fights on the player's
	// behalf. Reset on combat end (GameClient subscribes to CombatTracker.onCombatEnd).
	public bool CombatEngaged { get; private set; }
	public void NotifyCombatEngaged() { CombatEngaged = true; }
	public void ResetCombatEngaged() { CombatEngaged = false; }

	// Radius around the player within which a swung weapon counts as committing to
	// a fight with any triggered (combat-alert) hostile — flips CombatEngaged even
	// on a miss. The player's "I'm clearly fighting this" bubble; companion-agnostic.
	[Export] private float _combatEngageRange = 12f;
	private readonly List<Mob> _combatEngageScratch = new();



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
	// Dark-adaptation ("night eyes") state in [0,1]: 0 = light-adapted (bright),
	// 1 = fully dilated (deep dark). Sim-owned — updated each physics tick from
	// the perceived light where the player stands (UpdateEyeDilation), smoothed
	// with asymmetric time constants. GameClient reads this to drive the
	// eye_adaptation render global; PlayerPerception reads it to partially relieve
	// the darkness penalty when noticing things in the gloom.
	public float EyeDilation { get; private set; }
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
	// True whenever the player is in water at all (wading in the shallows or
	// swimming) — distinct from IsSwimming, which is deep water only. Read by
	// aquatic mobs whose hearing toward the player sharpens once prey shares
	// their water.
	public bool IsInWater => _waterState != EWaterState.None;
	public bool HasDamagingStatusEffect => _statusEffects?.HasDamagingEffect ?? false;
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

	// Catch up status effects by `dt` seconds in one call. Used by the sleep
	// time-skip (World.AdvanceTime) to integrate DoT, expire timed/time-of-day
	// effects, and drain buildup meters over a skipped span. Identical to the
	// per-frame path so there's no separate catch-up logic to drift.
	public void TickStatusEffects(float dt) => _statusEffects?.Tick(dt);

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
	// Shared hold threshold (milliseconds) for the context-button hold gestures
	// — interact-options and the consumable wheel — sourced from the
	// context_button_hold_time cvar (authored in seconds).
	float ContextButtonHoldMs => CVars.contextButtonHoldTime.Value * 1000f;

	// Consumable quick-select wheel. Holding ConsumableCycleRight past
	// ContextButtonHoldMs opens the HUD item wheel; the right stick then
	// highlights a belt slot and release selects it. A tap (release before the
	// threshold) falls back to cycling to the next consumable. Times on the sim
	// clock to match the InteractHold hold-to-open gesture above.
	bool _consumableWheelPressActive;
	ulong _consumableWheelPressStartMs;
	bool _consumableWheelOpen;


	Vector3 _inputMove = Vector3.Zero;
	Vector3 _inputLook = Vector3.Zero;
	// Mouse positional-aim cursor delta (world XZ, meters), accumulated across
	// motion events and cleared each time the reticle consumes it. Distinct from
	// _inputLook (facing): the mouse drives the ground cursor by displacement, not
	// by an absolute disk position. Gamepad leaves this zero (it's a rate device).
	Vector3 _mouseAimWorldDelta = Vector3.Zero;



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
			// Hit filter: the player is on the Player side, so allied hits
			// (a tamed companion, a friendly NPC) can't land unless friendly-fire.
			_hurtBox.CanHit = (hit) =>
				hit.friendlyFire || !Teams.AreAllied(hit.attackerTeam, ETeam.Player);
			// Cache the hurtbox shape so AimCenter (IAimTarget) reports the
			// chest-height body center, not the feet — mirrors Mob's lookup.
			foreach (Node child in _hurtBox.GetChildren())
			{
				if (child is CollisionShape3D shape)
				{
					_hurtBoxShape = shape;
					break;
				}
			}
		}

		_aimingReticle?.Initialize(this);
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
	//
	// First runs any apply-time payloads (instant heal, removesOnApply cleanse).
	// An `instantaneous` effect (the Restore blessing) is a one-shot — its
	// payloads fire and no lingering state is kept.
	public StatusEffectState AddStatusEffect(StatusEffectData data)
	{
		if (data == null)
		{
			return null;
		}
		ApplyInstantPayloads(data);
		if (data.instantaneous)
		{
			// One-shot blessing: no lingering state, but still honor its
			// removesOnApply cleanse (Add, which normally runs it, is skipped).
			_statusEffects.ApplyRemovesOnApply(data);
			return null;
		}
		return _statusEffects.Add(data);
	}

	// Fire the one-shot apply-time payloads on `data`. Cleanse before heal so a
	// poison tick can't shave the freshly-restored HP.
	void ApplyInstantPayloads(StatusEffectData data)
	{
		if (data.instantHealPercent > 0f)
		{
			Heal(MaxHealth * data.instantHealPercent);
		}
	}

	// Drop a single unit of `item` into the backpack, merging into an existing
	// stack first (Inventory.TryAdd). A unit that doesn't fit is dropped silently
	// — boons don't overflow into the world. Used by the Gold boon's item grant.
	public void GrantItem(ItemData item)
	{
		if (_inventory == null || item == null)
		{
			return;
		}
		_inventory.TryAdd(item.CreateState());
	}

	// True when the player has lost any health (current below max, or has an
	// outstanding blood-drain debt). Read by the fairy upgrade screen to gate
	// the restorative boon — there's no point offering a heal at full health.
	public bool IsInjured => _health < MaxHealth || _drainedHealth > 0f;

	// True when an instance of `data` is currently active. Read by the upgrade
	// screen so a lasting buff the player already carries isn't offered again.
	public bool HasStatusEffect(StatusEffectData data) => _statusEffects.HasActive(data);

	public void RemoveStatusEffect(StatusEffectState state) => _statusEffects.Remove(state);

	public void RemoveStatusEffectsByTagMask(EStat mask) => _statusEffects.RemoveByTagMask(mask);


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




	// Reconciles the player's carried torch against the current inventory. The
	// selected consumable, if a torch, is shown in-hand; otherwise any lit torch
	// in another slot is shown stowed on the back so it keeps lighting (the torch
	// can live in the active hotbar slot OR any other slot — a lit torch in the
	// backpack still lights the area). The HeldTorch prop carries its own world
	// light, so setting the prop + lit state is all the player does; placement
	// (hand vs back vs hidden) is HeldItemVisual's job. Called from the
	// ToggleMovingLight handler and any time the inventory changes.
	public void RefreshCarriedLight()
	{
		RefreshCarriedTorchVisual();
	}

	private void OnActiveConsumableChanged(int _)
	{
		RefreshCarriedTorchVisual();
	}

	// See RefreshCarriedLight. The light rides the held torch (HeldTorch.movingLightScene),
	// parented to the player root via the lightParent passed here.
	private void RefreshCarriedTorchVisual()
	{
		if (_heldVisual == null)
		{
			return;
		}
		ItemState active = _inventory?.GetActiveConsumable();
		if (active?.data is TorchData activeTorch)
		{
			// Selected torch: held in hand (HeldItemVisual stows it on the back on
			// its own if a weapon / item is also drawn), lit per its active state.
			bool lit = active is ConsumableState cs && cs.isActive;
			_heldVisual.SetTorch(activeTorch.heldTorchScene, inHand: true);
			_heldVisual.SetTorchLit(lit, this);
			return;
		}
		// No torch selected — a lit torch in any other slot stays visible (stowed)
		// and lighting.
		TorchData litTorch = FindLitTorch();
		_heldVisual.SetTorch(litTorch?.heldTorchScene, inHand: false);
		_heldVisual.SetTorchLit(litTorch != null, this);
	}

	// First lit (isActive) torch consumable anywhere in inventory, or null.
	private TorchData FindLitTorch()
	{
		if (_inventory == null)
		{
			return null;
		}
		foreach (ItemState item in _inventory.EnumerateAll())
		{
			if (item is ConsumableState cs && cs.isActive && cs.data is TorchData td)
			{
				return td;
			}
		}
		return null;
	}

	// Environment-driven counterpart to the manual ToggleMovingLight douse:
	// a carried torch can't survive being plunged underwater or stand up to
	// heavy rain, so swimming (over the head) or rain past the douse threshold
	// clears every active carried torch and reconciles the MovingLight (which
	// fires its authored off-cue). Mirrors DoToggleMovingLight's douse half but
	// is one-way — isActive is only ever set false here, so the flame never
	// auto-relights when conditions ease; the player relights manually. Wading
	// in shallows keeps a torch lit (only EWaterState.Swimming counts).
	// Idempotent: once cleared, the next tick finds nothing active, so running
	// every physics frame is a no-op.
	private void DouseCarriedTorches()
	{
		if (_inventory == null || data == null) { return; }

		bool douse = IsSwimming;
		if (!douse)
		{
			// RainExposure01 already folds in the perceptible-rain floor and the
			// overhead-shelter ramp, so > 0 means the player is genuinely being
			// rained on; only then does raw intensity decide "heavy enough".
			float rainIntensity = SkyController.Current?.Palette.RainIntensity ?? 0f;
			douse = RainExposure01() > 0f && rainIntensity >= data.torchDouseRainThreshold;
		}
		if (!douse) { return; }

		bool dousedAny = false;
		PackedScene douseFx = null;
		foreach (ItemState item in _inventory.EnumerateAll())
		{
			if (item is ConsumableState cs && cs.isActive && cs.data is TorchData torch)
			{
				cs.isActive = false;
				dousedAny = true;
				// Fire one wet-douse cue per event (the player only carries one
				// visible light), from the first doused torch that authors one.
				douseFx ??= torch.douseEffectScene;
			}
		}
		if (dousedAny)
		{
			RefreshCarriedLight();
			if (douseFx != null)
			{
				Fx.Create(douseFx, this, Vector3.Up * SkyExposureProbeHeight);
			}
		}
	}

	public void Initialize(World world, WorldGenData worldGenData, CharacterCreationState characterCreation, Vector3 position, Vector3 rotation)
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
		// Selecting a different consumable swaps what's shown in hand, so the
		// held-torch prop has to refresh even when the inventory contents don't.
		_inventory.onActiveConsumableChanged += OnActiveConsumableChanged;
		_runner = new ActionRunner(this);
		_statusEffects = new StatusEffectController(this, world, ApplyStatusHealthDelta, ComposeMaskMul);
		_scent = new ScentEmitter(this, world, data.scentStrength, data.scentDecayRate,
			data.scentStampInterval, data.scentStampMoveDistance, data.scentMaxCrumbs);
		_health = MaxHealth;

		// Adopt the spawned character's name (blank keeps the default).
		if (!string.IsNullOrEmpty(characterCreation?.playerName))
		{
			PlayerName = characterCreation.playerName;
		}

		// Instance the base model package for the spawned gender, then activate
		// the live visual. Must run before UpdateArmorVisual below, which drives
		// the active model's mesh set.
		EGender spawnGender = characterCreation?.gender ?? EGender.Female;
		_voice = ResolveGenderVoice(spawnGender);
		SpawnModelPackage(spawnGender);
		ActivateVisual();
		// Resolve + apply the modular appearance (skin tone, hair color, hair
		// style) before inventory seeding so the styled hair mesh is known the
		// first time the armor compositor runs.
		ApplyAppearance(characterCreation);

		// Starting loadout / knowledge is a property of the world scenario, not
		// the character, so it rides on WorldGenData.
		if (worldGenData != null)
		{
			if (worldGenData.equippedInventory != null)
			{
				foreach (ItemCount ic in worldGenData.equippedInventory)
				{
					if (ic?.descriptor?.item == null || ic.count <= 0) { continue; }
					int stackSize = ic.descriptor.item.maxStack > 0 ? ic.descriptor.item.maxStack : 1;
					int remaining = ic.count;
					while (remaining > 0)
					{
						int n = System.Math.Min(remaining, stackSize);
						ItemState state = ic.descriptor.CreateState();
						state.stackCount = n;
						_inventory.TryAdd(state);
						TryAutoEquipFromBackpack(state);
						remaining -= n;
					}
				}
			}
			if (worldGenData.startingConsumables != null)
			{
				foreach (ConsumableData cd in worldGenData.startingConsumables)
				{
					if (cd == null) { continue; }
					ItemState item = cd.CreateState();
					item.stackCount = cd.maxStack;
					_inventory.TryAdd(item);
					_inventory.TryMoveToConsumableSlot(item);
				}
			}
			if (worldGenData.startingInventory != null)
			{
				foreach (ItemCount ic in worldGenData.startingInventory)
				{
					if (ic?.descriptor?.item == null || ic.count <= 0) { continue; }
					int stackSize = ic.descriptor.item.maxStack > 0 ? ic.descriptor.item.maxStack : 1;
					int remaining = ic.count;
					while (remaining > 0)
					{
						int n = System.Math.Min(remaining, stackSize);
						ItemState state = ic.descriptor.CreateState();
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
			if (worldGenData.initialKnowledge != null)
			{
				for (int i = 0; i < worldGenData.initialKnowledge.Count; i++)
				{
					worldGenData.initialKnowledge[i]?.Teach(this);
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
		if (_world != null)
		{
			_bodyTemperature = _world.SampleAirTemperature(GlobalPosition);
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

		UpdateTerrainSpeed(dt);
		UpdateWaterState();
		UpdateSprintState();
		TickArmor(dt);
		TickBlockArmor(dt);
		TickAmmoRecharge(_world?.GameTimeMs ?? 0);
		TickStamina(dt);
		TickSwimStamina(dt);
		TickSprintStamina(dt);
		TickStaminaExhaustion();
		TickBloodDrain(dt);
		TickHitstun(dt);
		_statusEffects.Tick(dt);
		// Drop movement-trail hazards (e.g. the fairy-corpse buff's burning
		// fairy-fire) while dashing or sprinting. No-op unless an active effect
		// authors a trailZoneScene.
		_statusEffects.TickMovementTrail(this, IsDashing || IsSprinting, GlobalPosition, dt);
		UpdateNightVisionShaderGlobal();
		TickWetEffect(dt);
		DouseCarriedTorches();
		TickDirtyEffect(dt);
		TickMuddyEffect(dt);
		TickBodyTemperature(dt);
		DotHudFlush dotFlush = _dotHud.Tick(_world?.GameTimeMs ?? 0, GlobalPosition);
		if (dotFlush.damage)
		{
			// Continuous damage authors no per-frame fx; its "ouch" rides on
			// the once-per-second HUD rollup instead. Same pacing for the
			// red damage-flash so a slow burn doesn't desaturate the screen
			// permanently — one pulse per HUD-rolled second.
			SpawnVoice(_voice?.hurt);
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
		// An in-flight action scales movement by its current phase's multiplier:
		// the held tier's charge slowdown (heavy club / ranged draw) while
		// Charging, its active-phase root (drinking, dash, mob strike) while
		// Active. 1 when idle, so this is a no-op outside an action.
		if (_runner != null)
		{
			speed *= _runner.MovementSpeedMultiplier;
		}
		// Anim retiming is gated to movement-loop anims only — see
		// UpdateAnimation, which writes effectSpeedMultiplier per-frame based
		// on the currently-picked loopAnim. Attack / hitstun / death anims
		// play at authored speed regardless of status.
		if (_curInteractive != null || _birdsEye)
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
				float windSpeed = _world?.SampleWindSpeed(GlobalPosition) ?? 0f;
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
				// the same rate rather than snapping to a stop. Airborne stacks
				// windDrift onto the input target so wind nudges a hanging-in-air
				// player without fighting their move intent — input + drift,
				// water-current style.
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
			float gravity = (_jumpHeld && Velocity.Y > 0) ? _world.SimData.gravity * data.jumpHoldGravityScale : _world.SimData.gravity;
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
		// A committed action (a weapon tier with turnSpeedMultiplierActive = 0)
		// owns the player's facing for its duration — skip stick/look-driven
		// rotation so the swing commits to its starting heading. Default 1 on
		// every tier leaves this false, so normal weapons turn freely.
		bool facingLocked = _runner != null && _runner.LocksFacing;
		if (facingLocked)
		{
			// Facing held by the action — no rotation this tick.
		}
		else if (_skating || _skidding)
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
				float vt = _world.SimData.playerStuckVelocityThreshold;
				float tickThreshold = vt * dt;
				float displacementSq = (GlobalPosition - _lastTickPosition).LengthSquared();
				if (displacementSq > tickThreshold * tickThreshold || _stuckCheckDeadlineMs == 0)
				{
					_stuckCheckDeadlineMs = _world.GameTimeMs
						+ (ulong)(_world.SimData.playerStuckTimeoutSeconds * 1000);
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
		UpdateEyeDilation(dt);

		// Update highlight interactive
		UpdateHighlightInteractive();

		UpdateAnimation();

		UpdateHeldItemVisual();
	}



	bool TryGetWeaponState(EInventorySlot slot, out WeaponState weapon)
	{
		weapon = _inventory?.GetWeapon(slot);
		return weapon != null;
	}





}
