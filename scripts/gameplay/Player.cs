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
	// Per-ground-type one-shot effect played at the player's feet while
	// walking/running on solid ground. Authored in the player .tscn; missing
	// keys silently emit nothing.
	[Export] private Godot.Collections.Dictionary<EGroundType, PackedScene> _footstepEffects;

	public Action<Node3D> onHighlightChanged;
	public Action<IInteractive> onInteractChanged;
	public Action<Player> OnWaterEnter;
	public Action<Player> OnWaterExit;

	World _world;
	IInteractive _curInteractive;
	IInteractive _highlightInteractive;
	ulong _interactCompleteTimeMs;
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
	ulong _coyoteTimeEndMs;
	bool _jumpHeld;
	Inventory _inventory;
	ActionRunner _runner;
	float _health;
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

	public IInteractive HighlightInteractive => _highlightInteractive;
	public float ClientInteractProgress
	{
		get
		{
			if (_curInteractive == null || _world == null)
			{
				return 0f;
			}
			ulong interactTimeMs = _curInteractive.GetInteractTime(this);
			if (interactTimeMs == 0)
			{
				return 0f;
			}
			ulong now = _world.GameTimeMs;
			if (now >= _interactCompleteTimeMs)
			{
				return 1f;
			}
			ulong remaining = _interactCompleteTimeMs - now;
			return 1f - (float)remaining / interactTimeMs;
		}
	}

	Vector3 _inputMove = Vector3.Zero;
	Vector3 _inputLook = Vector3.Zero;

	void SetCurInteractive(IInteractive value)
	{
		if (_curInteractive != value)
		{
			_curInteractive = value;
			onInteractChanged?.Invoke(value);
		}
	}


	public override void _Ready()
	{
		CollisionLayer = (uint)ECollisionLayer.Player;
		CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Mob);

		interactArea.AreaEntered += OnInteractAreaEntered;
		interactArea.AreaExited += OnInteractAreaExited;

		if (_hurtBox != null)
		{
			_hurtBox.OnHit = OnHurtBoxHit;
		}
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

		_health = Mathf.Max(0f, _health - damage.healthDamage);
		if (_health <= 0f)
		{
			PlayOneShot(EAnimation.Die);
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
	}

	const ulong FallGraceMs = 400;

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

		// Footstep effects. Gated on grounded + on land (water is handled by
		// the ripple emitter above) + actually moving — Velocity carries
		// horizontal speed even before MoveAndSlide later in this method.
		const float FootstepStride = 0.6f;
		const float FootstepMinSpeedSq = 0.25f;
		Vector2 horizVel = new(Velocity.X, Velocity.Z);
		bool walking = _grounded
			&& _waterState == EWaterState.None
			&& horizVel.LengthSquared() > FootstepMinSpeedSq;
		EGroundType ground = GroundTypeResolver.Resolve(_world?.WorldState, GlobalPosition);
		_footstepEmitter.Update(_world, GlobalPosition, walking, FootstepStride, ground, _footstepEffects);

		if (_curInteractive != null)
		{
			if (_curInteractive.CanActorInteract(this))
			{
				if (_world.GameTimeMs >= _interactCompleteTimeMs)
				{
					_curInteractive.Complete();
					SetCurInteractive(null);
					_highlightInteractive = null;
					onHighlightChanged?.Invoke(null);
				}
			}
			else
			{
				SetCurInteractive(null);
				_highlightInteractive = null;
				onHighlightChanged?.Invoke(null);
			}
		}

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
			else
			{
				// Either no collision, or hit a non-floor surface (the side of
				// a mob capsule, a steep slope). In both cases the lift didn't
				// land on real ground — revert Y so the player doesn't get
				// deposited mid-air against the obstacle's flank every frame.
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
				// Action-runner path: if the interactive provides action
				// profiles, run the default verb's profile through the
				// runner. Hold-for-radial verb selection is future UI work.
				if (TryStartInteractiveAction(_highlightInteractive))
				{
					_highlightInteractive = null;
					onHighlightChanged?.Invoke(null);
				}
				else
				{
					SetCurInteractive(_highlightInteractive);
					ulong interactTimeMs = _curInteractive.GetInteractTime(this);
					if (interactTimeMs == 0)
					{
						_curInteractive.Complete();
						SetCurInteractive(null);
						_highlightInteractive = null;
						onHighlightChanged?.Invoke(null);
					}
					else
					{
						_interactCompleteTimeMs = _world.GameTimeMs + interactTimeMs;
					}
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
