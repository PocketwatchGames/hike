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
	[Export] private AudioListener3D _audioListener;
	[Export] private AimingReticle _aimingReticle;
	// Per-ground-type one-shot effect played at the player's feet while
	// walking/running on solid ground. Authored in the player .tscn; missing
	// keys silently emit nothing.
	[Export] private Godot.Collections.Dictionary<EGroundType, PackedScene> _footstepEffects;
	// Per-character footprint texture projected onto the ground at footstep
	// cadence. The shared player vs mob footprint scenes (and per-ground
	// tints) live on SimData; this is the only print authoring that varies
	// per character.
	[Export] private Texture2D _footprintTexture;
	// One-shot blood splatter spawned at the player's position on a non-lethal
	// damage hit. Spawned in world space so the puff stays put as the player
	// runs through it, matching the footstep effect convention.
	[Export] private PackedScene _bloodDamageEffect;
	// One-shot death blood spawned the moment _health crosses to zero.
	[Export] private PackedScene _deathEffect;
	// One-shot splash spawned when the player first enters a water trigger
	// (overlap count goes 0 → 1 in WaterAreaEntered).
	[Export] private PackedScene _waterEnterSplashEffect;
	// Continuous loop scenes (see Fx._loop). Parented to the player
	// so they follow the body; held alive while in the matching state and
	// stopped when leaving so the trailing audio + particles wind down cleanly.
	[Export] private PackedScene _waterMovementLoopEffect;
	[Export] private PackedScene _tallGrassMovementLoopEffect;
	// One-shots for vertical motion. Jump fires the moment input takes the
	// player off the floor. Land fires on every floor reacquisition unless
	// the inbound vertical speed exceeded LandHardSpeedThreshold, in which
	// case landHard takes its place — a heavier impact deserves dust + a
	// harder hit.
	[Export] private PackedScene _jumpEffect;
	[Export] private PackedScene _landEffect;
	[Export] private PackedScene _landHardEffect;
	// High-speed water entry. Picked over the standard splash when inbound
	// vertical speed at WaterAreaEntered exceeds WaterPlungeSpeedThreshold.
	[Export] private PackedScene _waterPlungeEffect;
	// Per-stride splash effect emitted while running through Shallow water.
	// Drives a separate FootstepEmitter from the ground-material dict because
	// shallow-water detection lives in _waterState (an Area-trigger flag),
	// not the EGroundType resolver — running across a thin film of water
	// over grass should still trigger water audio rather than grass audio.
	[Export] private PackedScene _shallowWaterFootstepEffect;
	// VO that plays in tandem with _bloodDamageEffect / _deathEffect on
	// the same hit. Separate scenes so the per-actor voice clips can ride on
	// top of the shared impact / death-splat audio without authoring per-
	// actor blood scenes.
	[Export] private PackedScene _hurtVoEffect;
	[Export] private PackedScene _deathVoEffect;
	// Armor lifecycle one-shots. Depleted plays the moment armor hits zero
	// from damage; rechargeStart plays when the post-hit recharge delay
	// elapses and the bar starts climbing again; recoverStart replaces it
	// when the recharge follows a full depletion (longer recover delay).
	[Export] private PackedScene _armorDepletedEffect;
	[Export] private PackedScene _armorRechargeStartEffect;
	[Export] private PackedScene _armorRecoverStartEffect;
	// Per-anim-state loops. UpdateAnimation maps the picked loopAnim down to
	// one of these scenes; only one (or none) is active at a time. Slots can
	// be left null in the .tscn — the actor falls silent for that state,
	// which is the current player default until per-character idle / run /
	// swim_idle audio is authored.
	[Export] private PackedScene _idleLoopEffect;
	[Export] private PackedScene _runLoopEffect;
	[Export] private PackedScene _swimIdleLoopEffect;
	// Distance the player must travel in XZ between footstep effect emits.
	// Larger = slower step cadence.
	[Export] private float _footstepStride = 1.2f;
	// Minimum horizontal speed² to count as "walking" for footstep / loop
	// gating. Below this the player is treated as standing still.
	[Export] private float _footstepMinSpeedSq = 0.25f;
	// Status effect applied while the player is in water or in unsheltered
	// rain. Authored data lives on the resource (duration, displayName, icon);
	// TickWetEffect arms / pauses the timer so the 30s dry-out only counts
	// while the player is actually drying.
	[Export] private StatusEffectData _wetEffectData;

	public Action<Node3D> onHighlightChanged;
	public Action<IInteractive> onInteractChanged;
	public Action<Player> OnWaterEnter;
	public Action<Player> OnWaterExit;

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
	readonly List<IInteractive> _interactiveCollisions = new();
	readonly List<TallGrass> _tallGrassCollisions = new();
	float _terrainSpeed = 1f;
	bool _grounded;
	bool _aiming;
	bool _sneaking;
	EWaterState _waterState = EWaterState.None;
	float _waterSurfaceY;
	int _waterOverlapCount;
	readonly WaterRippleEmitter _rippleEmitter = new();
	readonly FootstepEmitter _footstepEmitter = new();
	// Independent stride emitter for the shallow-water splash. Has its own
	// last-emit memory so the cadence resets cleanly when the player
	// transitions between dry land and a wet patch.
	readonly FootstepEmitter _shallowWaterFootstepEmitter = new();
	// Spawns persistent ground decals at the same stride cadence as
	// _footstepEmitter. Independent stride memory so footprint cadence stays
	// distinct from FX cadence if the two strides ever diverge.
	readonly FootprintEmitter _footprintEmitter = new();
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
	bool _jumpHeld;
	Inventory _inventory;
	// Languages the player has learned this run. Keyed by the shared
	// LanguageData resource instance, mirroring the WorldSimState.DiscoveredRegions
	// pattern. Signposts and mobs whose `language` is in this set are
	// comprehensible; everything else reads as gibberish.
	readonly HashSet<LanguageData> _learnedLanguages = new();
	ActionRunner _runner;
	float _health;
	float _armor;
	float _maxArmor;
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
	// Pending horizontal dash velocity. Set from ProcessInput on dash press;
	// consumed in _PhysicsProcess after the input-driven horizontal velocity
	// rebuild so the impulse isn't wiped. Y is always 0.
	Vector3 _dashImpulse;
	// Status effects (poison, heal-over-time, hot, wet, ...). Multiple
	// instances of the same StatusEffectData stack — each AddStatusEffect
	// appends a fresh state and ticks independently. The HUD groups by data
	// when rendering. Wired in Initialize once `_world` is known.
	StatusEffectController _statusEffects;
	// Live handle to the player's wet effect (null when dry). Reused across
	// re-wettings so the HUD shows a single Wet stack rather than rolling a
	// fresh icon every time the player enters/leaves rain.
	StatusEffectState _wetState;
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
	MovingLight _movingLight;
	StringName _oneShotAnim;
	// Wall-clock time at which the player most recently lost ground contact.
	// Drives the fall-anim grace window — running up/down hills momentarily
	// lifts off, and we don't want a one-frame !_grounded to spike the fall
	// animation. 0 means currently grounded (or never run a frame yet).
	ulong _airborneStartMs;


	public float visibility = 1f;
	// Current movement-noise output, in decibels. Sampled by mobs in their
	// mob-perceives-player tick to add a hearing contribution to perception.
	// 0 = silent (stationary); peaks at PlayerData.runDecibels at moveSpeed.
	// Mapped from Velocity in UpdateVisibility once per frame to keep the
	// per-mob perception tick a plain field read.
	public float CurrentDecibels { get; private set; }
	public bool IsAiming => _aiming;
	public bool IsSneaking => _sneaking;
	public EWaterState WaterState => _waterState;
	public World World => _world;
	public Inventory Inventory => _inventory;
	public IReadOnlyCollection<LanguageData> LearnedLanguages => _learnedLanguages;
	public bool HasLearnedLanguage(LanguageData language) => language == null || _learnedLanguages.Contains(language);
	public bool LearnLanguage(LanguageData language) => language != null && _learnedLanguages.Add(language);
	public ActionRunner Runner => _runner;
	public float Health => _health;
	public float MaxHealth => data?.maxHealth ?? 100f;
	public float Armor => _armor;
	public float MaxArmor => _maxArmor;
	public float Stamina => _stamina;
	public float MaxStamina => data?.maxStamina ?? 0f;
	public IReadOnlyList<StatusEffectState> StatusEffects => _statusEffects.StatusEffects;

	public IInteractive HighlightInteractive => _highlightInteractive;
	public IInteractive CurInteractive => _curInteractive;
	public int CurInteractiveActionIndex => _curInteractiveActionIndex;

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
		CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Mob);

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

	// Pure prediction — no state mutation. See Mob.GetHitType for the
	// networked-play motivation.
	private EHitResult GetHitType(HitInfo hit)
	{
		float incoming = hit.healthDamage;
		if (incoming <= 0f)
		{
			return EHitResult.None;
		}
		if (_armor > 0f)
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
		// Damage may interrupt an in-flight action (gated by profile +
		// per-tier canInterrupt). External interruption fires BEFORE damage
		// is applied so abortEvents can run on coherent pre-damage state.
		_runner?.TryInterrupt();
		_sneaking = false;

		float incomingDamage = hit.healthDamage;
		// Armor absorbs the entire hit when present — even an overflow drop
		// to zero leaves health untouched. The recharge timer is rearmed on
		// every absorbing hit; a hit that takes armor to zero arms the longer
		// recover window via _armorDepleted.
		if (_armor > 0f && incomingDamage > 0f)
		{
			_armor -= incomingDamage;
			ulong now = _world?.GameTimeMs ?? 0;
			if (_armor <= 0f)
			{
				_armor = 0f;
				_armorDepleted = true;
				_armorRechargeStartMs = now + (ulong)(data.armorRecoverTime * 1000f);
				SpawnWorldEffect(_armorDepletedEffect);
			}
			else
			{
				_armorDepleted = false;
				_armorRechargeStartMs = now + (ulong)(data.armorRechargeDelay * 1000f);
			}
			_armorRecharging = false;
			incomingDamage = 0f;
		}

		bool wasAlive = _health > 0f;
		_health = Mathf.Max(0f, _health - incomingDamage);
		if (_health <= 0f)
		{
			// Death blood + VO are fired on the alive→dead transition only —
			// a follow-up hit on an already-dead body shouldn't re-emit.
			if (wasAlive)
			{
				SpawnWorldEffect(_deathEffect);
				SpawnWorldEffect(_deathVoEffect);
			}
			PlayOneShot(EAnimation.Die);
		}
		else if (incomingDamage > 0f)
		{
			SpawnWorldEffect(_bloodDamageEffect);
			SpawnWorldEffect(_hurtVoEffect);
		}

		if (hit.statusEffects != null)
		{
			for (int i = 0; i < hit.statusEffects.Count; i++)
			{
				AddStatusEffect(hit.statusEffects[i]);
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
	public void PlayOneShot(EAnimation anim) => PlayOneShot(AnimationNames.Get(anim));

	public void PlayOneShot(StringName name)
	{
		if (_animator == null || name == default || !_animator.HasAnimation(name))
		{
			return;
		}
		_oneShotAnim = name;
		_animator.Play(name);
	}

	private void UpdateAnimation()
	{
		if (_animator == null)
		{
			return;
		}

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

		if (_oneShotAnim != default)
		{
			if (_animator.CurrentAnimation == _oneShotAnim && !_animator.Finished)
			{
				return;
			}
			_oneShotAnim = default;
		}

		StringName loopAnim;
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
			loopAnim = AnimationNames.Dead;
		}
		else if (_curInteractive != null)
		{
			// Interaction holds the player still (movement speed is forced to
			// 0 above) — show the interaction loop regardless of water/ground
			// state until the action completes or is cancelled.
			loopAnim = AnimationNames.Interacting;
		}
		else if (_waterState == EWaterState.Swimming)
		{
			loopAnim = PickMoveLoop(speedSq, intentMoving, AnimationNames.Swim, AnimationNames.SwimIdle);
		}
		else if (fallReady)
		{
			loopAnim = AnimationNames.Fall;
		}
		else if (_sneaking)
		{
			loopAnim = PickMoveLoop(speedSq, intentMoving, AnimationNames.Sneak, AnimationNames.SneakIdle);
		}
		else
		{
			loopAnim = PickMoveLoop(speedSq, intentMoving, AnimationNames.Run, AnimationNames.Idle);
		}
		_animator.Play(loopAnim);

		// Drive the anim-audio loop off the same loopAnim. Only idle / run /
		// swim_idle have audio; everything else (fall, dead, interacting,
		// active swim) is silent for the anim-loop layer.
		PackedScene animLoopTarget = null;
		if (_health > 0f)
		{
			if (loopAnim == AnimationNames.Idle) animLoopTarget = _idleLoopEffect;
			else if (loopAnim == AnimationNames.Run) animLoopTarget = _runLoopEffect;
			else if (loopAnim == AnimationNames.SwimIdle) animLoopTarget = _swimIdleLoopEffect;
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
	private StringName PickMoveLoop(float speedSq, bool intentMoving, StringName moveAnim, StringName idleAnim)
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
		StringName current = _animator.CurrentAnimation;
		if (current == moveAnim || current == idleAnim)
		{
			return current;
		}
		return idleAnim;
	}

	public void Heal(float amount)
	{
		if (amount <= 0f)
		{
			return;
		}
		_health = Mathf.Min(MaxHealth, _health + amount);
	}

	// Append a fresh state for `data`. Multiple instances of the same data
	// are intentional — the HUD shows them as one icon with a count, and each
	// instance ticks independently. Returns the new state so the caller (e.g.
	// the wet-after-swim trigger) can hold a handle and arm the timer later.
	public StatusEffectState AddStatusEffect(StatusEffectData data) => _statusEffects.Add(data);

	public void RemoveStatusEffect(StatusEffectState state) => _statusEffects.Remove(state);

	// WarmthZone (campfires, etc.) calls these on body enter/exit. Counter,
	// not bool, so two campfires whose zones overlap don't release the player
	// from one when they leave the other. Entering immediately clears any
	// in-flight wet effect — a player walking up to a fire dries off rather
	// than waiting out the timer. The zone's warmingTemperature is summed
	// into _warmthBonus so SampleEnvironmentTemperature can stack heat from
	// multiple overlapping fires.
	public void EnterWarmthZone(WarmthZone zone)
	{
		_warmthZoneCount++;
		if (zone != null)
		{
			_warmthBonus += zone.warmingTemperature;
		}
		if (_wetState != null)
		{
			RemoveStatusEffect(_wetState);
			_wetState = null;
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

	// Per-physics-tick wet state machine. Source conditions: swimming/wading,
	// or unsheltered while it's raining. While the player is wet AND in any
	// of those conditions, the timer stays paused (expireTimeMs == 0). When
	// they reach dry conditions the timer is armed for a full data.duration
	// window — re-entering rain mid-dry-out cancels the countdown back to 0
	// (matches the design: each dry-out runs the full 30s). Warmth zones are
	// handled separately by EnterWarmthZone, which clears the effect outright.
	private void TickWetEffect()
	{
		if (_wetEffectData == null)
		{
			return;
		}

		// Drop our handle if TickStatusEffects already pruned the expired effect.
		if (_wetState != null && !_statusEffects.Contains(_wetState))
		{
			_wetState = null;
		}

		// Inside a warmth zone the player is kept dry — skip both wet
		// application and timer arming. A wet effect already cleared on enter
		// in EnterWarmthZone; nothing to do until they leave.
		if (_warmthZoneCount > 0)
		{
			return;
		}

		bool wetSource = IsInWetConditions();
		if (wetSource)
		{
			if (_wetState == null)
			{
				_wetState = AddStatusEffect(_wetEffectData);
			}
			_wetState?.PauseTimer();
			return;
		}

		// Dry conditions: arm the countdown the first frame we transition out
		// of a wet source so the 30s starts fresh.
		if (_wetState != null && !_wetState.IsTimed)
		{
			_wetState.ArmTimer(_world?.GameTimeMs ?? 0);
		}
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
		_statusEffects.GetThermalResistances(out float coldResist, out float heatResist);
		AccumulateArmorResistance(EInventorySlot.ArmorHead, ref coldResist, ref heatResist);
		AccumulateArmorResistance(EInventorySlot.ArmorBody, ref coldResist, ref heatResist);
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

	private bool IsInWetConditions()
	{
		if (_waterState != EWaterState.None)
		{
			return true;
		}
		SkyController sky = SkyController.Current;
		if (sky == null || sky.Palette.RainIntensity <= 0f)
		{
			return false;
		}
		return IsSkyExposed();
	}

	// Single upward raycast against environment voxels. A clear shot to the
	// arbitrary high cap means the player has open sky overhead — anything in
	// the way (cave roof, balcony, tree canopy that registers as collidable)
	// counts as shelter. Cheap enough to run every physics tick (one ray);
	// the per-tick gating in IsInWetConditions skips it whenever it's not
	// raining or the player is already in water.
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
		_health = Mathf.Clamp(_health + delta, 0f, MaxHealth);
		if (_health <= 0f && wasAlive)
		{
			SpawnWorldEffect(_deathEffect);
			SpawnWorldEffect(_deathVoEffect);
			PlayOneShot(EAnimation.Die);
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
		_inventory = new Inventory(this, data);
		_inventory.onSlotChanged += OnInventorySlotChanged;
		_runner = new ActionRunner(this);
		_statusEffects = new StatusEffectController(this, world, ApplyStatusHealthDelta);
		_health = MaxHealth;

		if (spawnData != null)
		{
			if (spawnData.meleeWeaponData != null)
			{
				var melee = new WeaponState(spawnData.meleeWeaponData);
				_inventory.TryAdd(melee);
				_inventory.TryEquip(melee, EInventorySlot.WeaponLeft);
			}
			if (spawnData.rangedWeaponData != null)
			{
				var ranged = new WeaponState(spawnData.rangedWeaponData);
				_inventory.TryAdd(ranged);
				_inventory.TryEquip(ranged, EInventorySlot.WeaponRight);
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
			SpawnWorldEffect(_armorDepleted ? _armorRecoverStartEffect : _armorRechargeStartEffect);
		}
		_armor = Mathf.Min(_maxArmor, _armor + data.armorRechargeSpeed * dt);
		if (_armor >= _maxArmor)
		{
			_armorDepleted = false;
		}
	}

	// Spend stamina and arm the recharge delay. Returns false (without
	// charging) when there isn't enough in the pool, so callers can use this
	// as a gate for sprint / dodge / etc.
	public bool ConsumeStamina(float amount)
	{
		if (amount <= 0f)
		{
			return true;
		}
		if (_stamina < amount)
		{
			return false;
		}
		_stamina -= amount;
		ulong now = _world?.GameTimeMs ?? 0;
		_staminaRechargeStartMs = now + (ulong)(data.staminaRechargeDelay * 1000f);
		return true;
	}

	private void TickStamina(float dt)
	{
		float max = MaxStamina;
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
		TickArmor(dt);
		TickStamina(dt);
		_statusEffects.Tick(dt);
		TickWetEffect();
		TickBodyTemperature(dt);

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

		// Footstep effects. The dry-land emitter dispatches by EGroundType
		// (grass / stone / sand / etc.); the shallow-water emitter is its
		// own thing because shallow vs deep is an Area-trigger flag, not a
		// ground material. Both gate on grounded + moving but mutually
		// exclude each other via _waterState.
		Vector2 horizVel = new(Velocity.X, Velocity.Z);
		float horizSpeedSq = horizVel.LengthSquared();
		bool walkingDry = _grounded
			&& _waterState == EWaterState.None
			&& horizSpeedSq > _footstepMinSpeedSq;
		bool walkingShallow = _grounded
			&& _waterState == EWaterState.Shallow
			&& horizSpeedSq > _footstepMinSpeedSq;
		EGroundType ground = GroundTypeResolver.Resolve(_world?.WorldState, GlobalPosition);
		_footstepEmitter.Update(_world, GlobalPosition, walkingDry, _footstepStride, ground, _footstepEffects);
		_shallowWaterFootstepEmitter.Update(_world, GlobalPosition, walkingShallow, _footstepStride, _shallowWaterFootstepEffect);
		// Footprint decal cadence. Skipped while swimming (no contact) and
		// while wading (the splash already represents the disturbance) — only
		// dry-land contact leaves prints.
		_statusEffects.GetFootprintMultipliers(out float fpAlphaMul, out float fpDurMul);
		_footprintEmitter.Update(_world, GlobalPosition, GlobalRotation.Y, walkingDry, _footstepStride, ground, _footprintTexture, fpAlphaMul, fpDurMul, gated: false);

		// Movement-gated continuous loops. The water swim loop only plays
		// while actually swimming — shallow wading is covered by the
		// shallow-water footstep emitter above, so playing the swim loop
		// there too would double-up the audio.
		bool intentMoving = _inputMove.LengthSquared() > 0.0001f;
		bool moving = intentMoving || horizSpeedSq > _footstepMinSpeedSq;
		bool waterLoopActive = moving && _waterState == EWaterState.Swimming;
		// Tall-grass and water are mutually exclusive — when wading, the
		// shallow footsteps win so we don't double up on rustle + slosh.
		bool tallGrassLoopActive = moving && _tallGrassCollisions.Count > 0 && _waterState == EWaterState.None;
		UpdateLoopEffect(ref _waterMovementLoop, _waterMovementLoopEffect, waterLoopActive);
		UpdateLoopEffect(ref _tallGrassMovementLoop, _tallGrassMovementLoopEffect, tallGrassLoopActive);

		_aiming = Input.IsActionPressed("Aim") || (_inputLook != Vector3.Zero && InputDevice.Current == InputDevice.EDevice.Gamepad);

		float speed = data.moveSpeed;
		if (_sneaking)
		{
			speed = data.sneakSpeed;
		}
		speed *= _terrainSpeed;

		if (_waterState == EWaterState.Shallow)
		{
			speed *= data.shallowWaterSpeed;
		}
		else if (_waterState == EWaterState.Swimming)
		{
			speed = data.swimSpeed;
		}
		_statusEffects.GetMovementMultipliers(out float statusMoveMul, out float statusAnimMul);
		speed *= statusMoveMul;
		if (_animator != null)
		{
			_animator.effectSpeedMultiplier = statusAnimMul;
		}
		if (_curInteractive != null)
		{
			speed = 0;
		}

		Velocity = new Vector3(0, Velocity.Y, 0) + _inputMove * speed;

		// Dash overrides the input-driven horizontal velocity for this one
		// physics tick. Consume the impulse so subsequent ticks rebuild
		// velocity from input as normal.
		if (_dashImpulse.LengthSquared() > 0f)
		{
			Velocity = new Vector3(_dashImpulse.X, Velocity.Y, _dashImpulse.Z);
			_dashImpulse = Vector3.Zero;
		}

		if (_waterState == EWaterState.Swimming)
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

		if (_inputLook != Vector3.Zero)
		{
			Rotation = new Vector3(0, Mathf.Atan2(_inputLook.X, _inputLook.Z), 0);
		}
		else if (_inputMove != Vector3.Zero)
		{
			Rotation = new Vector3(0, Mathf.Atan2(_inputMove.X, _inputMove.Z), 0);
		}

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
		PushTouchedMobs();

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
			PackedScene landScene = hardLand ? _landHardEffect : _landEffect;
			SpawnWorldEffect(landScene);
			if (hardLand)
			{
				_sneaking = false;
			}
		}
		UpdateVisibility();

		// Update highlight interactive
		UpdateHighlightInteractive();

		UpdateAnimation();
	}

	public void ProcessMouseMotion(Vector2 mousePos, float cameraYaw)
	{
		_inputLook = new Vector3(mousePos.X, 0, mousePos.Y).Rotated(Vector3.Up, cameraYaw);
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

		// Look is device-gated. Gamepad sources from the right-stick axes here;
		// KBM sources from accumulated mouse motion via ProcessMouseMotion, so
		// we must NOT overwrite _inputLook on KBM frames — the axes are zero,
		// and overwriting would cancel out mouse-driven aim every frame.
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
		// canAbort). Toggling after the abort still flips _sneaking, so the
		// classic "tap Sneak to bail out of an attack and crouch" feel is
		// preserved — attacking cleared sneak when it started, and the abort
		// tap turns it back on.
		if (Input.IsActionJustPressed("Sneak"))
		{
			if (_runner != null && _runner.IsBusy)
			{
				_runner.TryAbort();
			}
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
			if (_grounded || _world.GameTimeMs < _coyoteTimeEndMs || (_waterState == EWaterState.Swimming && GlobalPosition.Y >= _waterSurfaceY - data.waterJumpOffset))
			{
				Velocity = new Vector3(Velocity.X, data.jumpSpeed, Velocity.Z);
				_grounded = false;
				_coyoteTimeEndMs = 0;
				_jumpHeld = true;
				PlayOneShot(EAnimation.Jump);
				SpawnWorldEffect(_jumpEffect);
			}
			else if (_waterState == EWaterState.Swimming)
			{
				Velocity = new Vector3(Velocity.X, data.swimVerticalSpeed, Velocity.Z);
			}
		}
		else if (!Input.IsActionPressed("Jump"))
		{
			_jumpHeld = false;
		}

		if (Input.IsActionJustPressed("Dash") && _stamina > 0f && data != null)
		{
			// Direction preference: active move input first (lets the player
			// dash sideways or backward independent of facing); fall back to
			// facing rotation so a stationary dash still goes somewhere.
			Vector3 dir;
			if (_inputMove.LengthSquared() > 0f)
			{
				dir = _inputMove.Normalized();
			}
			else
			{
				dir = new Vector3(Mathf.Sin(Rotation.Y), 0f, Mathf.Cos(Rotation.Y));
			}
			_dashImpulse = new Vector3(dir.X * data.dashSpeed, 0f, dir.Z * data.dashSpeed);

			// Spend stamina unconditionally — stamina is allowed to go
			// negative, and the recharge delay re-arms either way.
			_stamina -= data.dashStaminaCost;
			ulong now = _world?.GameTimeMs ?? 0;
			_staminaRechargeStartMs = now + (ulong)(data.staminaRechargeDelay * 1000f);
		}

		foreach (var (slot, actionName) in _weaponActions)
		{
			if (Input.IsActionJustPressed(actionName))
			{
				TryStartWeaponAction(slot);
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
			TryStartWeaponAction(slot);
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

	// Apply a horizontal impulse to any mob the player just slid into.
	// Magnitude scales with the player's planar speed and inversely with
	// the mob's mass — heavy mobs barely budge, light mobs scatter. Ignores
	// vertical motion so jumping onto a mob doesn't shove it sideways.
	// Called after MoveAndSlide because slide collisions are populated by
	// that call; running it before would walk a stale collision list.
	private void PushTouchedMobs()
	{
		if (data == null || data.mobPushStrength <= 0f)
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
		int count = GetSlideCollisionCount();
		for (int i = 0; i < count; i++)
		{
			using KinematicCollision3D c = GetSlideCollision(i);
			if (c?.GetCollider() is not Mob mob)
			{
				continue;
			}
			float mass = Mathf.Max(mob.Mass, 0.01f);
			Vector3 impulse = vel * (data.mobPushStrength / mass);
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
			PackedScene scene = (fallSpeed >= WaterPlungeSpeedThreshold && _waterPlungeEffect != null)
				? _waterPlungeEffect
				: _waterEnterSplashEffect;
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

		// Clamp sinking speed
		if (Velocity.Y < -data.waterSinkSpeed)
		{
			Velocity = new Vector3(Velocity.X, -data.waterSinkSpeed, Velocity.Z);
		}
	}

}
