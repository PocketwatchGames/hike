using Godot;
using System;
using System.Collections.Generic;

[GlobalClass]
public partial class Player : CharacterBody3D
{
	[Export] public PlayerData data;
	[Export] public Area3D interactArea;
	[Export] private HurtBox _hurtBox;

	public Action<Node3D> onHighlightChanged;
	public Action<IInteractive> onInteractChanged;

	World _world;
	IInteractive _curInteractive;
	IInteractive _highlightInteractive;
	ulong _interactCompleteTimeMs;
	readonly List<IInteractive> _interactiveCollisions = new();
	readonly List<TallGrass> _tallGrassCollisions = new();
	float _terrainSpeed = 1f;
	bool _grounded;
	bool _aiming;

	public float visibility = 1f;

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
	bool _lastInputWasGamepad;

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

	private void OnHurtBoxHit(DamageData data, Node source)
	{
		GD.Print($"Player hit for {data?.healthDamage} from {source?.Name}");
	}

	public void Initialize(World world, PlayerSpawnData spawnData, Vector3 position, Vector3 rotation)
	{
		_world = world;
		GlobalPosition = position;
		Rotation = rotation;
		_grounded = false;
		_weapons[(int)EItemSlot.Melee] = new WeaponState(spawnData.meleeWeaponData);
		_weapons[(int)EItemSlot.Ranged] = new WeaponState(spawnData.rangedWeaponData);
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

		_aiming = Input.IsActionPressed("Aim") || (_inputLook != Vector3.Zero && _lastInputWasGamepad);

		float speed = _aiming ? Mathf.Lerp(0.75f, 0.25f, (1f - _inputLook.Dot(_inputMove)) / 2) * data.moveSpeed : data.moveSpeed;
		if (Input.IsActionPressed("Sneak"))
		{
			speed = data.sneakSpeed;
		}
		speed *= _terrainSpeed;

		Velocity = new Vector3(0, Velocity.Y, 0) + _inputMove * speed;

		if (!_grounded)
		{
			Velocity += Vector3.Down * _world.SimData.Gravity * dt;
		} else
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

		for (int i=0;i<(int)EItemSlot.Count;i++)
		{
			if (_weapons[i] != null)
			{
				ProcessWeapon(_weapons[i], i, Input.IsActionPressed(_weaponActions[i]), dt);
			}
		}

		// Step up: lift the player before moving so they can clear small obstacles
		Vector3 posBeforeStep = GlobalPosition;
		if (_grounded)
		{
			GlobalPosition += Vector3.Up * data.stepHeight;
		}

		bool wasOnFloor = _grounded;
		MoveAndSlide();

		// Step down: snap back to the ground after moving
		if (wasOnFloor)
		{
			KinematicCollision3D stepDownResult = MoveAndCollide(Vector3.Down * data.stepHeight);
			if (stepDownResult != null)
			{
				_grounded = stepDownResult.GetNormal().Dot(Vector3.Up) > 0.5f;
			}
			else
			{
				// No ground found within step height — revert the lift
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
		UpdateVisibility();

		// Update highlight interactive
		UpdateHighlightInteractive();

		// Aiming preview
		Vector3 aimOrigin = GlobalPosition + Vector3.Up;
		Vector3 aimEnd = aimOrigin + GlobalTransform.Basis.Z * 5f;
		DebugBox.Create(
			_world,
			new Color(1f, 1f, 1f, 0.15f),
			0.05f,
			aimOrigin,
			aimEnd,
			0.1f,
			0.1f
		);
	}
	
	public void ProcessMouseMotion(Vector2 mousePos, float cameraYaw)
	{
		_inputLook = new Vector3(mousePos.X, 0, mousePos.Y).Rotated(Vector3.Up, cameraYaw);

		_lastInputWasGamepad = false;
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
		if (Input.IsActionJustReleased("Interact"))
		{
			if (_highlightInteractive != null && _highlightInteractive.CanActorInteract(this))
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

		if (Input.IsActionJustPressed("Jump"))
		{
			if (_grounded)
			{
				Velocity = new Vector3(Velocity.X, data.jumpSpeed, Velocity.Z);
				_grounded = false;
			}
		}

		for (int i = 0; i < (int)EItemSlot.Count; i++)
		{
			if (_weapons[i] == null)
			{
				continue;
			}
			if (Input.IsActionJustPressed(_weaponActions[i]))
			{
				WeaponPress(_weapons[i], i);
			}
			if (Input.IsActionJustReleased(_weaponActions[i]))
			{
				WeaponRelease(_weapons[i], i);
			}
		}
		_lastInputWasGamepad = move != Vector2.Zero || look != Vector2.Zero;
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

	private void UpdateVisibility()
	{
		float lightFactor = Mathf.Clamp(_world.WorldState.GetLightLevelWorld(GlobalPosition) / ((float)LightEngine.MAX_LIGHT * data.visibilityLightMax), 0, 1);

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

	public void OnLootCollision(Loot loot)
	{
		loot.PickUp();
	}
}
