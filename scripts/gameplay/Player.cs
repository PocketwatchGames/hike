using Godot;
using System;
using System.Collections.Generic;

[GlobalClass]
public partial class Player : CharacterBody3D
{
	[Export] public float stepHeight = 0.5f;
	[Export] public float moveSpeed = 7f;
	[Export] public float sneakSpeed = 3f;
	[Export] public float jumpSpeed = 18f;
	[Export] public Area3D interactArea;

	public Action<Node3D> onHighlightChanged;

	World _world;
	IInteractive _curInteractive;
	IInteractive _highlightInteractive;
	readonly List<IInteractive> _interactiveCollisions = new();
	readonly List<TallGrass> _tallGrassCollisions = new();
	float _terrainSpeed = 1f;
	bool _grounded;
	readonly WeaponState[] _weapons = new WeaponState[(int)EItemSlot.Count];

	Vector3 _inputMove = Vector3.Zero;
	Vector3 _inputLook = Vector3.Zero;
	bool _lastInputWasGamepad;

	public override void _Ready()
	{
		CollisionLayer = (uint)ECollisionLayer.Player;
		CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Mob);

		interactArea.BodyEntered += OnInteractBodyEntered;
		interactArea.BodyExited += OnInteractBodyExited;
	}

	public void Initialize(PlayerSpawnData spawnData, World world)
	{
		_world = world;
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

		float speed = moveSpeed;
		if (Input.IsActionPressed("Sneak"))
		{
			speed = sneakSpeed;
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

		string[] weaponActions = new[] { "AttackMelee", "AttackRanged" };
		for (int i=0;i<(int)EItemSlot.Count;i++)
		{
			if (_weapons[i] != null)
			{
				ProcessWeapon(_weapons[i], Input.IsActionPressed(weaponActions[i]), dt);
			}
		}

		// Step up: lift the player before moving so they can clear small obstacles
		Vector3 posBeforeStep = GlobalPosition;
		if (_grounded)
		{
			GlobalPosition += Vector3.Up * stepHeight;
		}

		bool wasOnFloor = _grounded;
		MoveAndSlide();

		// Step down: snap back to the ground after moving
		if (wasOnFloor)
		{
			KinematicCollision3D stepDownResult = MoveAndCollide(Vector3.Down * stepHeight);
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

		// Update highlight interactive
		UpdateHighlightInteractive();

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
				_curInteractive = _highlightInteractive;
				_curInteractive.Complete();
				_curInteractive = null;
				onHighlightChanged?.Invoke(null);
				onHighlightChanged?.Invoke(_highlightInteractive as Node3D);
			}
		}

		if (Input.IsActionJustPressed("Jump"))
		{
			if (_grounded)
			{
				Velocity = new Vector3(Velocity.X, jumpSpeed, Velocity.Z);
				_grounded = false;
			}
		}

		if (Input.IsActionJustReleased("AttackMelee"))
		{
			PerformMeleeAttack();
		}

		if (Input.IsActionJustReleased("AttackRanged"))
		{
			PerformRangedAttack();
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

	private void OnInteractBodyEntered(Node3D body)
	{
		IInteractive interactive = body as IInteractive ?? body.GetParent() as IInteractive;
		if (interactive != null)
		{
			_interactiveCollisions.Add(interactive);
		}
	}

	private void OnInteractBodyExited(Node3D body)
	{
		IInteractive interactive = body as IInteractive ?? body.GetParent() as IInteractive;
		if (interactive != null)
		{
			_interactiveCollisions.Remove(interactive);
		}
	}

	private void PerformMeleeAttack()
	{
		WeaponState meleeWeapon = _weapons[(int)EItemSlot.Melee];
		var spaceState = GetWorld3D().DirectSpaceState;
		var shape = new SphereShape3D();
		shape.Radius = meleeWeapon.data.meleeRadius;

		var query = new PhysicsShapeQueryParameters3D();
		query.Shape = shape;
		Vector3 forward = GlobalTransform.Basis.Z;
		query.Transform = new Transform3D(Basis.Identity, GlobalPosition + forward * meleeWeapon.data.meleeRange);
		query.CollisionMask = (uint)ECollisionLayer.Mob;

		var results = spaceState.IntersectShape(query);
		foreach (var result in results)
		{
			var collider = result["collider"].Obj;
			if (collider is Mob mob)
			{
				mob.Hit();
			}
		}

		DebugSphere.Create(
			_world,
			new Color(1f, 0f, 0f, 0.3f),
			0.15f,
			GlobalPosition + forward * meleeWeapon.data.meleeRange,
			meleeWeapon.data.meleeRadius
		);
	}

	void ProcessWeapon(WeaponState weapon, bool inputPressed, float dt)
	{
		if (weapon == null || weapon.data == null)
		{
			return;
		}
	}



	private void PerformRangedAttack()
	{
		WeaponState rangedWeapon = _weapons[(int)EItemSlot.Ranged];
		if (rangedWeapon == null || rangedWeapon.data == null)
		{
			return;
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
