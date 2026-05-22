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
	[Export] public Area3D interactArea;
	[Export] private HurtBox _hurtBox;
	[Export] private LitSpriteAnimator _animator;
	[Export] private DashGhostTrail _dashGhostTrail;
	[Export] private AudioListener3D _audioListener;
	[Export] private AimingReticle _aimingReticle;
	// Status effect applied while the player is in water or in unsheltered
	// rain. Authored data lives on the resource (duration, displayName, icon);
	// TickWetEffect arms / pauses the timer so the 30s dry-out only counts
	// while the player is actually drying.
	[Export] private StatusEffectData _wetEffectData;

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
	[Export] private PackedScene _tallGrassMovementLoopFx;
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
	// Per-animation footfall frame indices. One entry per animation that
	// should emit footsteps (run, sprint, sneak, …); each entry names the
	// animation and lists the frame numbers within it where the foot
	// strikes the ground. The animator fires OnFrameAdvanced as the sprite
	// cycles; a matching (anim, frame) pair triggers a footstep + footprint.
	// Anims absent from this list (idle, jump, fall, attack, …) never emit.
	[Export] private Godot.Collections.Array<FootstepFrameSet> _footstepFrames = new();
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

	World _world;
	IInteractive _curInteractive;
	// Companion to _curInteractive — names which entry in the interactive's
	// GetActions() list the player has committed to. Future radial-menu UI
	// will overwrite this between highlight and commit so the player can
	// pick Lockpick/Break/Open on a chest.
	int _curInteractiveActionIndex;
	IInteractive _highlightInteractive;
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
	readonly List<TallGrass> _tallGrassCollisions = new();
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
	Fx _tallGrassMovementLoop;
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
	// mirrors it for the knockback lockout window. Independent of any stun
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
	// hits zero, _dashGlideRemaining counts down a tapered carry-over so
	// the player doesn't snap from dash speed to input speed in one tick.
	// _dashCooldownEndMs gates re-activation.
	Vector3 _dashDir;
	float _dashSpeed;
	float _dashTimeRemaining;
	float _dashGlideRemaining;
	bool _dashFreezeGravity;
	ulong _dashCooldownEndMs;
	// Status effects (poison, heal-over-time, hot, wet, ...). Multiple
	// instances of the same StatusEffectData stack — each AddStatusEffect
	// appends a fresh state and ticks independently. The HUD groups by data
	// when rendering. Wired in Initialize once `_world` is known.
	StatusEffectController _statusEffects;
	// Live handle to the player's wet effect (null when dry). Reused across
	// re-wettings so the HUD shows a single Wet stack rather than rolling a
	// fresh icon every time the player enters/leaves rain.
	StatusEffectState _wetState;
	// Player's accumulated wetness in [0, 1]. Integrates the rain-exposure
	// signal each tick (gated by sky exposure) and decays back to 0 in dry
	// conditions. The wet status arms only when wetness crosses the arm
	// threshold and disarms when it falls below the disarm threshold,
	// giving rain a build-up window before the gameplay effect fires and
	// a drying period after rain stops. Standing in water snaps to 1
	// immediately.
	float _wetness;
	public float Wetness => _wetness;
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
	// Same pattern as _wetState — we keep the reference so the safe-band
	// timer arms / pauses on the EXISTING state instead of stacking icons.
	StatusEffectState _coldState;
	StatusEffectState _hotState;
	// Rolls up HitInfo.dot per-frame damage / heal into one onDamage /
	// onHeal invocation per second so a burn or poison zone emits a single
	// floating HUD number per second instead of one per physics frame.
	// Non-DoT hits bypass this and fire onDamage / onHeal immediately.
	readonly DotHudAccumulator _dotHud = new();
	MovingLight _movingLight;
	EAnimation? _oneShotAnim;
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
	public bool IsSprinting => _sprinting;
	public bool IsDead => _health <= 0f;
	public EWaterState WaterState => _waterState;
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
	public float MaxHealth => data?.maxHealth ?? 100f;
	public float Armor => _armor;
	public float MaxArmor => _maxArmor;
	public float Stamina => _stamina;
	public float MaxStamina => (data?.maxStamina ?? 0f) + (_statusEffects?.MaxStaminaBonus ?? 0f);
	public IReadOnlyList<StatusEffectState> StatusEffects => _statusEffects.StatusEffects;

	public IInteractive HighlightInteractive => _highlightInteractive;
	public IInteractive CurInteractive => _curInteractive;
	public int CurInteractiveActionIndex => _curInteractiveActionIndex;

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
		CollisionMask = (uint)ECollisionLayer.Environment;

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

		if (_animator != null)
		{
			_animator.OnFrameAdvanced += OnAnimFrameAdvanced;
		}

		_aimingReticle?.Initialize(this);
	}

	// Footstep / footprint emission is driven by the sprite animator instead
	// of distance travelled. The animator fires this whenever its frame
	// changes; we look up the current animation in _footstepFrames and emit
	// when the frame index matches an authored footfall. State gates the
	// spawn: skip while ungrounded, swimming, or interacting; route to the
	// shallow-water splash variant while wading.
	private void OnAnimFrameAdvanced(StringName anim, int frame)
	{
		if (_world == null || _footstepFrames == null)
		{
			return;
		}
		if (!FootstepFrameSet.Matches(_footstepFrames, anim, frame))
		{
			return;
		}
		if (!_grounded || _waterState == EWaterState.Swimming || _curInteractive != null)
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
			_statusEffects.GetFootprintMultipliers(out float fpAlphaMul, out float fpDurMul);
			FootprintEmitter.Emit(_world, pos, GlobalRotation.Y, ground, _footprintTexture, _footprintSize, fpAlphaMul, fpDurMul, gated: false);
		}
	}

	// Pure prediction — no state mutation. See Mob.GetHitType for the
	// networked-play motivation.
	private EHitResult GetHitType(HitInfo hit)
	{
		// Status-driven i-frames: any active effect with damageMultiplier=0
		// reduces the product to 0, signaling "no hit landed." Dash i-frames
		// are authored as an ApplyStatusEffect event on the dash profile, so
		// the i-frame window is data-tunable independent of the dash's
		// physical duration.
		float damageMultiplier = _statusEffects?.DamageMultiplier ?? 1f;
		if (damageMultiplier <= 0f)
		{
			return EHitResult.None;
		}
		float incoming = hit.healthDamage * damageMultiplier;
		if (incoming <= 0f)
		{
			return EHitResult.None;
		}
		// A pierced hit skips armor entirely and lands on health. Otherwise
		// armor (when present) absorbs the whole hit, matching the legacy
		// fully-absorbed semantics.
		if (_armor > 0f && !hit.Pierced)
		{
			return EHitResult.Armor;
		}
		if (_health <= 0f)
		{
			return EHitResult.None;
		}
		return incoming >= _health ? EHitResult.Lethal : EHitResult.Health;
	}

	private void OnHurtBoxHit(HitInfo hit)
	{
		if (CVars.invulnerable.Value)
		{
			return;
		}
		// Scale by status-driven damage multipliers. A 0.0 product (dash
		// i-frames, etc.) drops the hit before interrupt/sneak side-effects
		// fire — a dashing player should not have their dash interrupted
		// nor lose sneak from a hit that did nothing.
		float damageMultiplier = _statusEffects?.DamageMultiplier ?? 1f;
		float incomingDamage = hit.healthDamage * damageMultiplier;
		if (incomingDamage <= 0f && hit.statusEffects == null && hit.stun <= 0f)
		{
			return;
		}

		// Damage may interrupt an in-flight action (gated by profile +
		// per-tier canInterrupt). External interruption fires BEFORE damage
		// is applied so abortEvents can run on coherent pre-damage state.
		_runner?.TryInterrupt();
		_sneaking = false;
		// Armor handling. Two-part chip: hit.stun always chips armor (when
		// any is present), and the healthDamage portion piles on top unless
		// the hit pierced — pierce skips the healthDamage chip but still
		// counts as "the hit registered," so we reset the recharge timer
		// regardless. Overflow doesn't bleed into health on the absorbed
		// path. A hit that takes armor to zero arms the longer recover
		// window via _armorDepleted; everything else uses the regular
		// recharge delay. The player has no stun meter today, so hit.stun
		// is consumed entirely by this armor chip.
		float armorAbsorbed = 0f;
		if (_armor > 0f && (incomingDamage > 0f || hit.stun > 0f))
		{
			float armorDamage = hit.stun + (hit.Pierced ? 0f : incomingDamage);
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
			if (!hit.Pierced)
			{
				incomingDamage = 0f;
			}
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
		else if (incomingDamage > 0f)
		{
			SpawnWorldEffect(_bloodDamageFx);
			SpawnWorldEffect(_hurtVoFx);
		}

		// Floating-number HUD feedback. Armor chip and pierced health damage
		// both show — total = whatever the bar actually moved (capped by what
		// armor / health had to give). DoT hits route into the per-second
		// accumulator so a fast-ticking burn / poison zone emits one rolled-up
		// number per second; single hits fire onDamage immediately.
		float totalShown = armorAbsorbed + Mathf.Max(0f, incomingDamage);
		if (totalShown > 0f)
		{
			if (hit.dot)
			{
				_dotHud.AddDamage(totalShown);
			}
			else
			{
				GameClient.Current?.onDamage?.Invoke(GlobalPosition, totalShown, EHudTextType.DamageLight);
			}
		}

		if (hit.statusEffects != null)
		{
			for (int i = 0; i < hit.statusEffects.Count; i++)
			{
				AddStatusEffect(hit.statusEffects[i]);
			}
		}

		// Hitstun + knockback: latch the flinch + knockback windows so
		// per-frame ticks can count them down. Player has no stun state today,
		// so no OnStun modifier folds in here — the path is still wired so
		// future player-stun work picks it up for free. Direction comes from
		// the sender via HitInfo.hitDirection; a zero direction drops
		// knockback entirely regardless of distance. Death overrides the
		// hitstun anim because the Die one-shot above latches first.
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

	// One-shots (attack, die, jump) latch _oneShotAnim and let the
	// LitSpriteAnimator drive itself to completion — Finished flips because
	// these anims are authored with loop=false in player.tscn. While a one-
	// shot is latched, UpdateAnimation defers; once Finished (or the animator
	// gets reassigned by something else) we clear the latch and resume the
	// state-driven loop pick.
	public void PlayOneShot(EAnimation anim)
	{
		if (_animator == null || data == null)
		{
			return;
		}
		StringName name = data.GetAnimationName(anim);
		if (name == default || !_animator.HasAnimation(name))
		{
			return;
		}
		_oneShotAnim = anim;
		_animator.Play(name);
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

		if (_oneShotAnim.HasValue)
		{
			EAnimation oneShot = _oneShotAnim.Value;
			// Hitstun is gated solely by _hitstunTime — when the timer hits
			// zero the latch releases regardless of the clip's loop flag or
			// Finished state, so a looping hitstun clip doesn't trap the
			// player in the anim past the flinch window. Other one-shots
			// hold while the animator says the clip is still playing.
			if (oneShot == EAnimation.Hitstun)
			{
				if (_hitstunTime > 0f)
				{
					return;
				}
				_oneShotAnim = null;
			}
			else
			{
				StringName oneShotName = data.GetAnimationName(oneShot);
				if (_animator.CurrentAnimation == oneShotName && !_animator.Finished)
				{
					return;
				}
				_oneShotAnim = null;
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
		StringName loopName = data.GetAnimationName(loopAnim);
		if (loopName != default)
		{
			_animator.Play(loopName);
		}

		// Status retiming (Cold etc.) is gated per-anim by AnimationData —
		// only loops authored with affectedBySpeedMultiplier track statusAnimMul
		// (movement anims whose underlying action is also slowed by statusMoveMul).
		// One-shots already returned above, so this branch only runs for loops.
		if (data.IsAnimationSpeedAffected(loopAnim))
		{
			_statusEffects.GetMovementMultipliers(out _, out float animSpeedMul);
			_animator.effectSpeedMultiplier = animSpeedMul;
		}

		// Drive the anim-audio loop off the same loopAnim. Only idle / run /
		// swim_idle have audio; everything else (fall, dead, interacting,
		// active swim) is silent for the anim-loop layer.
		PackedScene animLoopTarget = null;
		if (_health > 0f)
		{
			if (loopAnim == EAnimation.Idle) animLoopTarget = _idleLoopFx;
			else if (loopAnim == EAnimation.Run) animLoopTarget = _runLoopFx;
			else if (loopAnim == EAnimation.SwimIdle) animLoopTarget = _swimIdleLoopFx;
		}
		UpdateAnimLoop(animLoopTarget);
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
		if (current == data.GetAnimationName(moveAnim))
		{
			return moveAnim;
		}
		if (current == data.GetAnimationName(idleAnim))
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

	// WarmthZone (campfires, etc.) calls these on body enter/exit. Counter,
	// not bool, so two campfires whose zones overlap don't release the player
	// from one when they leave the other. Entering accelerates the wetness
	// decay rate (PlayerData.wetnessWarmthDryRate) — a player walking up to
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

	// Per-physics-tick wet state machine driven by an accumulating
	// wetness float on the player (0 = bone dry, 1 = soaked). Sources:
	// standing in water snaps wetness to 1 immediately; standing in rain
	// while sky-exposed builds wetness at wetnessRainRate × RainIntensity
	// per second. Dry conditions decay wetness back toward 0 at
	// wetnessDryRate (or wetnessWarmthDryRate if the player is inside a
	// warmth zone). The wet status only arms when wetness crosses
	// wetnessArmThreshold and only releases when it falls below
	// wetnessDisarmThreshold — hysteresis prevents the status flapping
	// while wetness hovers near either boundary.
	private void TickWetEffect(float dt)
	{
		if (_wetEffectData == null || data == null)
		{
			return;
		}

		// Drop our handle if TickStatusEffects already pruned the expired effect.
		if (_wetState != null && !_statusEffects.Contains(_wetState))
		{
			_wetState = null;
		}

		// Source classification — water beats rain beats nothing. Warmth
		// zones (campfires) suppress rain accumulation entirely so the
		// player dries off at the fire even when it's raining around
		// them; the fast warmthDryRate then takes them down regardless
		// of overhead conditions. Water still wins over warmth — if you
		// step into a stream at a campfire, you're soaked.
		bool inWater = _waterState != EWaterState.None;
		bool inWarmth = _warmthZoneCount > 0;
		bool inRain = !inWater && !inWarmth && IsInRain();

		if (inWater)
		{
			_wetness = 1f;
		}
		else if (inRain)
		{
			float rainIntensity = Mathf.Clamp(SkyController.Current?.Palette.RainIntensity ?? 0f, 0f, 1f);
			_wetness = Mathf.Clamp(_wetness + data.wetnessRainRate * rainIntensity * dt, 0f, 1f);
		}
		else
		{
			float dryRate = inWarmth ? data.wetnessWarmthDryRate : data.wetnessDryRate;
			_wetness = Mathf.Clamp(_wetness - dryRate * dt, 0f, 1f);
		}

		// Hysteresis: arm above wetnessArmThreshold, release below
		// wetnessDisarmThreshold. Between the two, the status holds its
		// current state — prevents single-frame flapping when wetness
		// brushes the arm boundary on a low-intensity drizzle.
		if (_wetState == null)
		{
			if (_wetness >= data.wetnessArmThreshold)
			{
				_wetState = AddStatusEffect(_wetEffectData);
				_wetState?.PauseTimer();
			}
		}
		else
		{
			if (_wetness <= data.wetnessDisarmThreshold)
			{
				RemoveStatusEffect(_wetState);
				_wetState = null;
			}
			else
			{
				// Status persists; timer stays paused so it doesn't auto-
				// expire on us — wetness decay is what releases the status.
				_wetState.PauseTimer();
			}
		}
	}

	// Surface a continuous 0..1 progress value the HUD's status-effect
	// strip can render as a fill bar, for status effects whose intensity
	// is driven by a continuous player-side state rather than a timer.
	// Returns null for effects that don't have a custom mapping (the HUD
	// falls back to its timer-based progress).
	//
	// Mapping for wet: the bar's floor (0) is wetnessDisarmThreshold —
	// the wetness at which the status auto-clears — so the bar empties
	// as the player approaches drying off. The ceiling (1) is full
	// saturation. Rain-armed status enters the bar partway up
	// ((armThreshold - disarm) / (1 - disarm) ≈ 44% at defaults), swim-
	// armed status enters at full. Future thirst / hunger / cold / hot
	// status effects can hook into the same method.
	public float? GetStatusEffectProgress(StatusEffectData effectData)
	{
		if (effectData == null || data == null) { return null; }
		if (effectData == _wetEffectData)
		{
			float disarm = data.wetnessDisarmThreshold;
			float denom = Mathf.Max(1f - disarm, 1e-4f);
			return Mathf.Clamp((_wetness - disarm) / denom, 0f, 1f);
		}
		return null;
	}

	// Are we outdoors with rain falling? Replaces the old IsInWetConditions
	// for the wet-status path — water-state is handled separately so the
	// caller can snap wetness to 1 directly.
	//
	// Gated on a perceptible-rain floor instead of strict `> 0`. The
	// simRain → palette.RainIntensity formula is `pow(simRain, 1.25)`, and
	// simCloud clipping its rain threshold by epsilon produces a simRain
	// of ~1e-5 (displays as 0.000) that maps to a ~1e-7 RainIntensity. A
	// strict positive check keeps the rain branch active at that value,
	// so wetness never drains. RainPerceptibleFloor filters the noise.
	private const float RainPerceptibleFloor = 0.01f;

	private bool IsInRain()
	{
		SkyController sky = SkyController.Current;
		if (sky == null || sky.Palette.RainIntensity < RainPerceptibleFloor)
		{
			return false;
		}
		return IsSkyExposed();
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

	// Single upward raycast against environment voxels. A clear shot to the
	// arbitrary high cap means the player has open sky overhead — anything in
	// the way (cave roof, balcony, tree canopy that registers as collidable)
	// counts as shelter. Cheap enough to run every physics tick (one ray);
	// the per-tick gating in IsInRain skips it whenever it's not raining or
	// the player is already in water.
	private bool IsSkyExposed()
	{
		World3D world3D = GetWorld3D();
		if (world3D == null)
		{
			return false;
		}
		Vector3 from = GlobalPosition + Vector3.Up * 1.5f;
		Vector3 to = from + Vector3.Up * 200f;
		using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Environment);
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;
		query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
		var result = world3D.DirectSpaceState.IntersectRay(query);
		return result.Count == 0;
	}

	// Signed HP delta from a status-effect tick. Positive heals, negative
	// damages. Bypasses armor — poison-style ticks are designed to chip
	// regardless of armor in most games, and routing through OnHurtBoxHit
	// would also fire the runner-interrupt + DamageData path which doesn't
	// fit a per-second tick.
	private void ApplyStatusHealthDelta(float delta)
	{
		if (delta == 0f || _health <= 0f)
		{
			return;
		}
		bool wasAlive = _health > 0f;
		float before = _health;
		// Heal-over-time effects climb to MaxHealth the same way Heal()
		// does — drain doesn't reduce the effective cap, and any drain
		// the heal climbs into is forgiven to preserve the
		// `Health + DrainedHealth <= MaxHealth` invariant.
		_health = Mathf.Clamp(_health + delta, 0f, MaxHealth);
		if (delta > 0f)
		{
			_drainedHealth = Mathf.Min(_drainedHealth, Mathf.Max(0f, MaxHealth - _health));
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
		_dashGlideRemaining = 0f;
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
		_wetState = null;
		_coldState = null;
		_hotState = null;
		_wetness = 0f;
		_drainedHealth = 0f;
		_bloodRegenStartMs = 0;
		GameClient client = GameClient.Current;
		_bodyTemperature = client != null
			? client.SampleAirTemperature(position)
			: 70f;
		_warmthZoneCount = 0;
		_warmthBonus = 0f;
		_health = MaxHealth;
		_armor = _maxArmor;
		_stamina = MaxStamina;
		_armorRecharging = false;
		_armorDepleted = false;
		_armorRechargeStartMs = 0;
		_staminaRechargeStartMs = 0;
		_dashTimeRemaining = 0f;
		_dashGlideRemaining = 0f;
		_dashCooldownEndMs = 0;
		_hitstunTime = 0f;
		_knockbackTime = 0f;
		_knockbackVelocity = Vector3.Zero;
		_sneaking = false;
		_sprinting = false;
		_aiming = false;
		_jumpHeld = false;
		_oneShotAnim = null;
		_grounded = false;
		_coyoteTimeEndMs = 0;
		TeleportTo(position);
		// Force the animator off the Die clip so the first post-respawn frame
		// shows the idle pose instead of holding the corpse. UpdateAnimation
		// will repick on the next physics tick.
		if (_animator != null && data != null)
		{
			StringName idleName = data.GetAnimationName(EAnimation.Idle);
			if (idleName != default)
			{
				_animator.Play(idleName);
			}
		}
	}

	// Phase 4 ToggleMovingLight handler hook. Spawns/despawns a MovingLight
	// attached to the player. The player must be inside the scene tree by
	// this point (Initialize has run); attach the light as a child so it
	// follows the player's transform. The scene comes from the activating
	// torch's TorchData — different torches can carry different lights.
	public void SetMovingLightActive(bool active, PackedScene scene = null)
	{
		if (active)
		{
			if (_movingLight != null)
			{
				return;
			}
			if (scene == null)
			{
				return;
			}
			_movingLight = scene.Instantiate<MovingLight>();
			AddChild(_movingLight);
		}
		else
		{
			if (_movingLight == null)
			{
				return;
			}
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
		_runner = new ActionRunner(this);
		_statusEffects = new StatusEffectController(this, world, ApplyStatusHealthDelta);
		_scent = new ScentEmitter(this, world, data.scentStrength, data.scentDecayRate,
			data.scentStampInterval, data.scentStampMoveDistance, data.scentMaxCrumbs);
		_health = MaxHealth;

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
		_armor = _maxArmor;
		_stamina = MaxStamina;

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

	private void AccumulateArmorResistance(EInventorySlot slot, ref float coldResist, ref float heatResist)
	{
		if (_inventory == null) { return; }
		if (_inventory.GetEquipped(slot) is ArmorState armor && armor.data != null)
		{
			coldResist += armor.data.coldResistance;
			heatResist += armor.data.heatResistance;
		}
	}

	// Composite cold / heat resistance from every equipped armor piece plus
	// every active status effect. Used by the temperature path to shift the
	// cold/hot trigger thresholds and by the inventory's player-stats panel
	// to display the resolved total.
	public void GetThermalResistances(out float coldResistance, out float heatResistance)
	{
		coldResistance = 0f;
		heatResistance = 0f;
		_statusEffects?.GetThermalResistances(out coldResistance, out heatResistance);
		AccumulateArmorResistance(EInventorySlot.ArmorHead, ref coldResistance, ref heatResistance);
		AccumulateArmorResistance(EInventorySlot.ArmorBody, ref coldResistance, ref heatResistance);
	}

	// Composite sense stats from every equipped armor piece plus every
	// active status effect. Camouflage is an additive sum (0 = neutral);
	// the four sense modifiers are multiplicative products (1.0 = neutral).
	// Callers fold the multipliers into a PlayerData base value when an
	// effective absolute is wanted; the inventory stats panel just renders
	// them as signed deltas off neutral.
	public void GetSenseStats(out float camouflage, out float visionMultiplier, out float hearingMultiplier, out float noiseMultiplier, out float scentMultiplier)
	{
		camouflage = 0f;
		visionMultiplier = 1f;
		hearingMultiplier = 1f;
		noiseMultiplier = 1f;
		scentMultiplier = 1f;
		AccumulateArmorSenses(EInventorySlot.ArmorHead, ref camouflage, ref visionMultiplier, ref hearingMultiplier, ref noiseMultiplier, ref scentMultiplier);
		AccumulateArmorSenses(EInventorySlot.ArmorBody, ref camouflage, ref visionMultiplier, ref hearingMultiplier, ref noiseMultiplier, ref scentMultiplier);
		_statusEffects?.AccumulateSenseModifiers(ref camouflage, ref visionMultiplier, ref hearingMultiplier, ref noiseMultiplier, ref scentMultiplier);
	}

	private void AccumulateArmorSenses(EInventorySlot slot, ref float camouflage, ref float vision, ref float hearing, ref float noise, ref float scent)
	{
		if (_inventory == null) { return; }
		if (_inventory.GetEquipped(slot) is ArmorState armor && armor.data != null)
		{
			camouflage += armor.data.camouflage;
			vision *= armor.data.visionMultiplier;
			hearing *= armor.data.hearingMultiplier;
			noise *= armor.data.noiseMultiplier;
			scent *= armor.data.scentMultiplier;
		}
	}

	// Composite movement multiplier from every active status effect. Doesn't
	// include armor — armor doesn't carry a speed modifier in the current
	// model. Cold and similar effects multiply in here.
	public float SpeedMultiplier
	{
		get
		{
			if (_statusEffects == null) { return 1f; }
			_statusEffects.GetMovementMultipliers(out float movement, out _);
			return movement;
		}
	}

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
		if (_maxArmor <= 0f || _armor >= _maxArmor)
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
		_armor = Mathf.Min(_maxArmor, _armor + data.armorRechargeSpeed * dt);
		if (_armor >= _maxArmor)
		{
			_armorDepleted = false;
		}
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

	// Recompute _sprinting each tick from current state. Sprint is
	// intent-based: it engages when Dash is held past the initial dash burst
	// with move input, regardless of stamina. Stamina gates the *speed boost*
	// in the speed calc (sprintSpeed → moveSpeed when depleted) but the
	// intent still arms the recharge delay via TickSprintStamina, so holding
	// sprint while exhausted prevents refill. Clears the post-dash glide on
	// the transition into sprint so the dash-to-sprint hand-off skips the
	// tapered carry.
	private void UpdateSprintState()
	{
		if (data == null)
		{
			_sprinting = false;
			return;
		}
		bool runnerBlocks = _runner != null
			&& _runner.IsBusy
			&& _runner.Current.profile != data.dashActionProfile;
		bool wantsSprint = Input.IsActionPressed("Dash")
			&& _dashTimeRemaining <= 0f
			&& _inputMove.LengthSquared() > 0.0001f
			&& _curInteractive == null
			&& !runnerBlocks;
		if (wantsSprint && !_sprinting)
		{
			_dashGlideRemaining = 0f;
		}
		_sprinting = wantsSprint;
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
		_dashGlideRemaining = 0f;
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

		UpdateTerrainSpeed();
		UpdateWaterState();
		UpdateSprintState();
		TickArmor(dt);
		TickStamina(dt);
		TickSwimStamina(dt);
		TickSprintStamina(dt);
		TickBloodDrain(dt);
		TickHitstun(dt);
		_statusEffects.Tick(dt);
		TickWetEffect(dt);
		TickBodyTemperature(dt);
		_dotHud.Tick(_world?.GameTimeMs ?? 0, GlobalPosition);
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

		// Footsteps and footprints are driven by sprite animation events
		// (see OnAnimFrameAdvanced), not anything in this method. Movement-
		// gated continuous loops below still key off horizontal speed.
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
		bool tallGrassLoopActive = moving && _tallGrassCollisions.Count > 0 && _waterState == EWaterState.None;
		UpdateLoopEffect(ref _waterMovementLoop, _waterMovementLoopFx, waterLoopActive);
		UpdateLoopEffect(ref _tallGrassMovementLoop, _tallGrassMovementLoopFx, tallGrassLoopActive);

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
		//   sprinting + stamina > 0          → sprintSpeed
		//   sprinting + stamina ≤ 0          → moveSpeed   (effort with no gas left)
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
			speed = exhausted ? data.moveSpeed : data.sprintSpeed;
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
		_statusEffects.GetMovementMultipliers(out float statusMoveMul, out float _);
		speed *= statusMoveMul;
		// Sprite anim retiming is gated to movement-loop anims only — see
		// UpdateAnimation, which writes effectSpeedMultiplier per-frame based
		// on the currently-picked loopAnim. Attack / hitstun / death anims
		// play at authored speed regardless of status.
		if (_curInteractive != null)
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
		else if (_dashGlideRemaining > 0f)
		{
			// Post-dash carry: hold dashEndSpeedCap in the dash direction,
			// tapered linearly to 0 over dashGlideTime. Input still steers
			// rotation; the regular input-driven rebuild resumes once glide
			// expires.
			float t = _dashGlideRemaining / data.dashGlideTime;
			float glideSpeed = data.dashEndSpeedCap * _terrainSpeed * statusMoveMul * t;
			Velocity = new Vector3(_dashDir.X * glideSpeed, Velocity.Y, _dashDir.Z * glideSpeed);
			_dashGlideRemaining -= dt;
			if (_dashGlideRemaining < 0f)
			{
				_dashGlideRemaining = 0f;
			}
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
			else
			{
				Velocity = new Vector3(0, Velocity.Y, 0) + inputVel;
			}
		}
		if (_wallJumpAirControlTimer > 0f)
		{
			_wallJumpAirControlTimer = Mathf.Max(0f, _wallJumpAirControlTimer - dt);
		}

		// Ghost-trail emit state is a side-effect of the dash phase, not part
		// of the velocity chain — it must NOT live inside the if/else if/else
		// above or the glide and input-rebuild branches get skipped whenever
		// the trail is wired, leaving Velocity locked at the last dash value
		// for the rest of the run.
		if (_dashGhostTrail != null)
		{
			_dashGhostTrail.EmitEnabled = _dashTimeRemaining > 0f;
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
		}
		else
		{
			Velocity = new Vector3(Velocity.X, -1f, Velocity.Z); // Small downward force to keep grounded
		}

		// Same CanLook gate as the _aiming suppression above — during dash or
		// sprint, rotation falls through to move direction.
		if (CanLook() && _inputLook != Vector3.Zero)
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

		// Step down: snap back to the ground after moving
		if (wasOnFloor && _waterState != EWaterState.Swimming)
		{
			using KinematicCollision3D stepDownResult = MoveAndCollide(Vector3.Down * data.stepHeight);
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
			if (_runner != null && _runner.IsBusy && _runner.TryAbort())
			{
				// abort consumed the press
			}
			else
			{
				_sneaking = !_sneaking;
			}
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
			if (_grounded || _world.GameTimeMs < _coyoteTimeEndMs || swimSurfaceJump)
			{
				float jumpSpeed = swimSurfaceJump ? data.swimJumpSpeed : data.jumpSpeed;
				Velocity = new Vector3(Velocity.X, jumpSpeed, Velocity.Z);
				_grounded = false;
				_coyoteTimeEndMs = 0;
				_jumpHeld = true;
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
				WeaponState pendingWeapon = _inventory?.GetWeapon(pendingSlot);
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
		foreach (TallGrass grass in _tallGrassCollisions)
		{
			_terrainSpeed = Mathf.Min(_terrainSpeed, grass.speed);
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
		_dashGlideRemaining = 0f;
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

	private void EndDash()
	{
		_dashTimeRemaining = 0f;
		_dashGlideRemaining = data.dashGlideTime;
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
		foreach (TallGrass grass in _tallGrassCollisions)
		{
			camouflage = Mathf.Max(camouflage, grass.camouflage);
		}

		visibility = Mathf.Clamp(lightFactor * speedFactor * (1.0f - camouflage), 0f, 1f);
		visibilityLight = lightFactor;
		visibilitySpeed = speedFactor;
		visibilityCamouflage = Mathf.Max(0f, 1f - camouflage);

		Vector3 horizVel = Velocity;
		horizVel.Y = 0f;
		CurrentDecibels = PlayerPerception.ComputeMovementDecibels(horizVel.Length(), data.sneakSpeed, data.moveSpeed, data.sneakDecibels, data.runDecibels);
	}

	public void AddTerrainModifier(TallGrass tallGrass)
	{
		_tallGrassCollisions.Add(tallGrass);
	}

	public void RemoveTerrainModifier(TallGrass tallGrass)
	{
		_tallGrassCollisions.Remove(tallGrass);
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

		// Drag to damp vertical oscillation
		Velocity = new Vector3(Velocity.X, Velocity.Y - Velocity.Y * data.waterDrag * dt, Velocity.Z);

		// Carried by the water current — add the current's velocity directly,
		// scaled by waterCurrentDrag. The XZ component of Velocity was just
		// overwritten by input above, so a per-second drag rate would never
		// integrate; treat this as the steady-state push instead. drag=1
		// means the player drifts at exactly the current's m/s when standing
		// still, with input simply layered on top.
		Vector3 current = _world.WorldState.SampleWaterCurrent(GlobalPosition);
		Velocity = new Vector3(
			Velocity.X + current.X * data.waterCurrentDrag,
			Velocity.Y,
			Velocity.Z + current.Z * data.waterCurrentDrag
		);

		// Clamp sinking speed
		if (Velocity.Y < -data.waterSinkSpeed)
		{
			Velocity = new Vector3(Velocity.X, -data.waterSinkSpeed, Velocity.Z);
		}
	}

}
