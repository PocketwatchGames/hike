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
	// Per-ground-type one-shot effect played at the player's feet while
	// walking/running on solid ground. Authored in the player .tscn; missing
	// keys silently emit nothing.
	[Export] private Godot.Collections.Dictionary<EGroundType, PackedScene> _footstepEffects;
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
	// an EAnimLoopState bucket; only one (or none) is active at a time.
	// Slots can be left null in the .tscn — the actor falls silent for that
	// state, which is the current player default until per-character idle /
	// run / swim_idle audio is authored.
	[Export] private PackedScene _idleLoopEffect;
	[Export] private PackedScene _runLoopEffect;
	[Export] private PackedScene _swimIdleLoopEffect;
	// Distance the player must travel in XZ between footstep effect emits.
	// Larger = slower step cadence.
	[Export] private float _footstepStride = 1.2f;
	// Minimum horizontal speed² to count as "walking" for footstep / loop
	// gating. Below this the player is treated as standing still.
	[Export] private float _footstepMinSpeedSq = 0.25f;

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
	readonly List<IInteractive> _interactiveCollisions = new();
	readonly List<TallGrass> _tallGrassCollisions = new();
	float _terrainSpeed = 1f;
	bool _grounded;
	bool _aiming;
	EWaterState _waterState = EWaterState.None;
	float _waterSurfaceY;
	int _waterOverlapCount;
	readonly WaterRippleEmitter _rippleEmitter = new();
	readonly FootstepEmitter _footstepEmitter = new();
	// Independent stride emitter for the shallow-water splash. Has its own
	// last-emit memory so the cadence resets cleanly when the player
	// transitions between dry land and a wet patch.
	readonly FootstepEmitter _shallowWaterFootstepEmitter = new();
	// Active loop instances. Null when the matching state isn't held; created
	// on the first frame state becomes active and Stop()'d when it ends. We
	// drop the reference at Stop() so the next activation creates a fresh
	// node rather than racing with the trailing-audio teardown.
	Fx _waterMovementLoop;
	Fx _tallGrassMovementLoop;
	// Single active anim-loop reference + the state it represents. Swapped
	// wholesale on transitions instead of cross-fading.
	Fx _animLoop;
	EAnimLoopState _animLoopState = EAnimLoopState.None;
	ulong _coyoteTimeEndMs;
	bool _jumpHeld;
	Inventory _inventory;
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
	CarrierLight _carrierLight;
	StringName _oneShotAnim;
	// Wall-clock time at which the player most recently lost ground contact.
	// Drives the fall-anim grace window — running up/down hills momentarily
	// lifts off, and we don't want a one-frame !_grounded to spike the fall
	// animation. 0 means currently grounded (or never run a frame yet).
	ulong _airborneStartMs;


	public float visibility = 1f;
	public EWaterState WaterState => _waterState;
	public World World => _world;
	public Inventory Inventory => _inventory;
	public ActionRunner Runner => _runner;
	public float Health => _health;
	public float MaxHealth => data?.maxHealth ?? 100f;
	public float Armor => _armor;
	public float MaxArmor => _maxArmor;

	public IInteractive HighlightInteractive => _highlightInteractive;
	public IInteractive CurInteractive => _curInteractive;
	public int CurInteractiveActionIndex => _curInteractiveActionIndex;
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
	}

	// Pure prediction — no state mutation. See Mob.GetHitType for the
	// networked-play motivation.
	private EHitResult GetHitType(DamageData damage)
	{
		if (damage == null)
		{
			return EHitResult.None;
		}
		float incoming = damage.healthDamage;
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

	private void OnHurtBoxHit(DamageData damage, Node source)
	{
		if (damage == null)
		{
			return;
		}

		// Damage may interrupt an in-flight action (gated by profile +
		// per-tier canInterrupt). External interruption fires BEFORE damage
		// is applied so abortEvents can run on coherent pre-damage state.
		_runner?.TryInterrupt();

		float incomingDamage = damage.healthDamage;
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
		else
		{
			loopAnim = PickMoveLoop(speedSq, intentMoving, AnimationNames.Run, AnimationNames.Idle);
		}
		_animator.Play(loopAnim);

		// Drive the anim-audio loop off the same loopAnim. Only idle / run /
		// swim_idle have audio; everything else (fall, dead, interacting,
		// active swim) is silent for the anim-loop layer.
		EAnimLoopState animLoopTarget = EAnimLoopState.None;
		if (_health > 0f)
		{
			if (loopAnim == AnimationNames.Idle) animLoopTarget = EAnimLoopState.Idle;
			else if (loopAnim == AnimationNames.Run) animLoopTarget = EAnimLoopState.Run;
			else if (loopAnim == AnimationNames.SwimIdle) animLoopTarget = EAnimLoopState.SwimIdle;
		}
		UpdateAnimLoop(animLoopTarget);
	}

	// Swap the active anim-loop wholesale on state change. No-op when target
	// matches the cached state, so this is safe to call every frame.
	private void UpdateAnimLoop(EAnimLoopState target)
	{
		if (target == _animLoopState)
		{
			return;
		}
		if (_animLoop != null)
		{
			_animLoop.Stop();
			_animLoop = null;
		}
		PackedScene scene = target switch
		{
			EAnimLoopState.Idle => _idleLoopEffect,
			EAnimLoopState.Run => _runLoopEffect,
			EAnimLoopState.SwimIdle => _swimIdleLoopEffect,
			_ => null,
		};
		if (scene != null)
		{
			_animLoop = Fx.Create(scene, this, Vector3.Zero);
		}
		_animLoopState = target;
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

	// Phase 4 ToggleCarrierLight handler hook. Spawns/despawns a CarrierLight
	// attached to the player. The player must be inside the scene tree by
	// this point (Initialize has run); attach the light as a child so it
	// follows the player's transform. The scene comes from the activating
	// torch's TorchData — different torches can carry different lights.
	public void SetCarrierLightActive(bool active, PackedScene scene = null)
	{
		if (active)
		{
			if (_carrierLight != null)
			{
				return;
			}
			if (scene == null)
			{
				return;
			}
			_carrierLight = scene.Instantiate<CarrierLight>();
			AddChild(_carrierLight);
		}
		else
		{
			if (_carrierLight == null)
			{
				return;
			}
			_carrierLight.Deactivate();
			_carrierLight.QueueFree();
			_carrierLight = null;
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
				foreach (ItemData id in spawnData.startingInventory)
				{
					if (id == null) { continue; }
					ItemState item = id.CreateState();
					item.stackCount = id.maxStack;
					_inventory.TryAdd(item);
				}
			}
		}

		// Start the player at full armor so freshly-spawned armor reads as
		// "ready" rather than charging up through the HUD on first frame.
		RecalculateMaxArmor();
		_armor = _maxArmor;
	}

	private void OnInventorySlotChanged(EInventorySlot slot)
	{
		if (slot == EInventorySlot.ArmorHead
			|| slot == EInventorySlot.ArmorBody
			|| slot == EInventorySlot.ArmorCloak
			|| slot == EInventorySlot.ArmorAccessory)
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
			AccumulateArmor(EInventorySlot.ArmorCloak, ref total);
			AccumulateArmor(EInventorySlot.ArmorAccessory, ref total);
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

		float speed = _aiming ? Mathf.Lerp(0.75f, 0.25f, (1f - _inputLook.Dot(_inputMove)) / 2) * data.moveSpeed : data.moveSpeed;
		if (Input.IsActionPressed("Sneak"))
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
		if (_curInteractive != null)
		{
			speed = 0;
		}

		Velocity = new Vector3(0, Velocity.Y, 0) + _inputMove * speed;

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
			MoveAndCollide(Vector3.Up * data.stepHeight);
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
			KinematicCollision3D stepDownResult = MoveAndCollide(Vector3.Down * data.stepHeight);
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
			PackedScene landScene = inboundFallSpeed >= LandHardSpeedThreshold ? _landHardEffect : _landEffect;
			SpawnWorldEffect(landScene);
		}
		UpdateVisibility();

		// Update highlight interactive
		UpdateHighlightInteractive();

		UpdateAnimation();

		// Aiming preview
		Vector3 aimOrigin = GlobalPosition + Vector3.Up;
		Vector3 aimEnd = aimOrigin + GlobalTransform.Basis.Z * 5f;
		DebugDraw.Line(aimOrigin, aimEnd, new Color(1f, 1f, 1f, 0.15f), 0.05f);
	}

	public void ProcessMouseMotion(Vector2 mousePos, float cameraYaw)
	{
		_inputLook = new Vector3(mousePos.X, 0, mousePos.Y).Rotated(Vector3.Up, cameraYaw);
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
	
	public void ProcessInput(float cameraYaw)
	{
		Vector2 move = Vector2.Zero;
		move.X -= Input.GetActionStrength("MoveLeft");
		move.X += Input.GetActionStrength("MoveRight");
		move.Y -= Input.GetActionStrength("MoveUp");
		move.Y += Input.GetActionStrength("MoveDown");
		move = move.LengthSquared() > 1 ? move.Normalized() : move;
		_inputMove = new Vector3(move.X, 0, move.Y).Rotated(Vector3.Up, cameraYaw);

		Vector2 look = Vector2.Zero;
		look.X -= Input.GetActionStrength("LookLeft");
		look.X += Input.GetActionStrength("LookRight");
		look.Y -= Input.GetActionStrength("LookUp");
		look.Y += Input.GetActionStrength("LookDown");
		look = look.LengthSquared() > 1 ? look.Normalized() : look;
		_inputLook = new Vector3(look.X, 0, look.Y).Rotated(Vector3.Up, cameraYaw);

		// Handle interact input
		if (Input.IsActionJustPressed("Interact"))
		{
			if (_curInteractive != null)
			{
				CancelInteract();
			} else if (_highlightInteractive != null && _highlightInteractive.CanActorInteract(this))
			{
				// Hold-for-radial verb selection is future UI work — the
				// runner currently always runs the interactive's DefaultVerb.
				if (TryStartInteractiveAction(_highlightInteractive))
				{
					_highlightInteractive = null;
					onHighlightChanged?.Invoke(null);
				}
			}
		}

		if (Input.IsActionJustPressed("Jump") || Input.IsActionJustPressed("Sneak"))
		{
			CancelInteract();
		}

		if (Input.IsActionJustPressed("ConsumableCycleLeft"))
		{
			_inventory?.CycleConsumable(-1);
		}
		if (Input.IsActionJustPressed("ConsumableCycleRight"))
		{
			_inventory?.CycleConsumable(+1);
		}

		// Sneak press doubles as the player-initiated abort key while a
		// runner action is in flight. Charging always cancels; Active
		// cancels only if the selected tier opts in via canAbort. The press
		// is not consumed — holding Sneak still applies sneak speed afterward,
		// which feels right ("tap to bail out and crouch").
		if (Input.IsActionJustPressed("Sneak") && _runner != null && _runner.IsBusy)
		{
			_runner.TryAbort();
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

		HandleWeaponInputs();
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
				if (interactive is Node3D node)
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
			KinematicCollision3D c = GetSlideCollision(i);
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
		float lightFactor = Mathf.Clamp(_world.GetPerceivedLight(GlobalPosition) / data.visibilityLightMax, 0, 1);

		float speedFactor = data.moveSpeed > 0f ? Mathf.Clamp(Mathf.Pow(Velocity.Length() / data.moveSpeed, data.visibilityMovementPower), data.visibilityMovementMin, 1f) : 1f;

		float camouflage = 0f;
		foreach (TallGrass grass in _tallGrassCollisions)
		{
			camouflage = Mathf.Max(camouflage, grass.camouflage);
		}

		visibility = Mathf.Clamp(lightFactor * speedFactor * (1.0f - camouflage), 0f, 1f);
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

	public void OnAutoLootCollision(AutoLoot loot)
	{
		loot.PickUp(this);
	}
}
