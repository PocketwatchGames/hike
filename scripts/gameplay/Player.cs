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
	[Export] public float meleeRadius = 2f;
	[Export] public float meleeRange = 1f;
	[Export] public Area3D interactArea;

	public ShaderMaterial outlineMaterial;
	public World world;
	private IInteractive _curInteractive;
	private IInteractive _highlightInteractive;
	private readonly List<IInteractive> _interactiveCollisions = new();
	private readonly List<TallGrass> _tallGrassCollisions = new();
	private float _terrainSpeed = 1f;
	public bool grounded;

	private Sprite3D _highlightOverlay;
	private MeshInstance3D _debugMeleeSphere;
	Vector3 _inputMove = Vector3.Zero;
	Vector3 _inputLook = Vector3.Zero;
	bool _lastInputWasGamepad;

	public override void _Ready()
	{
		CollisionLayer = (uint)ECollisionLayer.Player;
		CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Mob);

		_highlightOverlay = new Sprite3D();
		_highlightOverlay.Name = "HighlightOverlay";
		_highlightOverlay.MaterialOverride = outlineMaterial;
		_highlightOverlay.AlphaCut = SpriteBase3D.AlphaCutMode.Disabled;
		_highlightOverlay.Visible = false;
		AddChild(_highlightOverlay);

		interactArea.BodyEntered += OnInteractBodyEntered;
		interactArea.BodyExited += OnInteractBodyExited;

		var sphereMesh = new SphereMesh();
		sphereMesh.Radius = meleeRadius;
		sphereMesh.Height = meleeRadius * 2f;
		var material = new StandardMaterial3D();
		material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		material.AlbedoColor = new Color(1f, 0f, 0f, 0.3f);
		material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		sphereMesh.Material = material;
		_debugMeleeSphere = new MeshInstance3D();
		_debugMeleeSphere.Mesh = sphereMesh;
		_debugMeleeSphere.Visible = false;
		_debugMeleeSphere.TopLevel = true;
		AddChild(_debugMeleeSphere);
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
		if (!grounded)
		{
			Velocity += Vector3.Down * world.SimData.Gravity * dt;
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

		// Step up: lift the player before moving so they can clear small obstacles
		Vector3 posBeforeStep = GlobalPosition;
		if (grounded)
		{
			GlobalPosition += Vector3.Up * stepHeight;
		}

		bool wasOnFloor = grounded;
		MoveAndSlide();

		// Step down: snap back to the ground after moving
		if (wasOnFloor)
		{
			KinematicCollision3D stepDownResult = MoveAndCollide(Vector3.Down * stepHeight);
			if (stepDownResult != null)
			{
				grounded = stepDownResult.GetNormal().Dot(Vector3.Up) > 0.5f;
			}
			else
			{
				// No ground found within step height — revert the lift
				GlobalPosition = new Vector3(
					GlobalPosition.X,
					posBeforeStep.Y,
					GlobalPosition.Z
				);
				grounded = false;
			}
		}
		else
		{
			grounded = IsOnFloor();
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
				RemoveHighlight();
				ApplyHighlight((Node3D)_highlightInteractive);
			}
		}

		if (Input.IsActionJustPressed("Jump"))
		{
			if (grounded)
			{
				Velocity = new Vector3(Velocity.X, jumpSpeed, Velocity.Z);
				grounded = false;
			}
		}

		if (Input.IsActionJustReleased("AttackMelee"))
		{
			PerformMeleeAttack();
		}

		if (Input.IsActionJustReleased("AttackRanged"))
		{
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
			RemoveHighlight();
			if (_highlightInteractive is Node3D highlightNode)
			{
				ApplyHighlight(highlightNode);
			}
		}
	}

	private void ApplyHighlight(Node3D node)
	{
		Sprite3D source = FindChildSprite(node);
		if (source == null || !source.Visible)
		{
			return;
		}

		_highlightOverlay.Texture = source.Texture;
		_highlightOverlay.Transform = source.Transform;
		_highlightOverlay.Offset = source.Offset;
		_highlightOverlay.PixelSize = source.PixelSize;
		_highlightOverlay.Billboard = source.Billboard;
		_highlightOverlay.TextureFilter = source.TextureFilter;
		outlineMaterial.SetShaderParameter("texture_albedo", source.Texture);
		_highlightOverlay.Reparent(node, false);
		_highlightOverlay.Visible = true;
	}

	private void RemoveHighlight()
	{
		_highlightOverlay.Visible = false;
		_highlightOverlay.Reparent(this, false);
	}

	private static Sprite3D FindChildSprite(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child is Sprite3D sprite && sprite.Visible)
			{
				return sprite;
			}
		}
		return null;
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
		var spaceState = GetWorld3D().DirectSpaceState;
		var shape = new SphereShape3D();
		shape.Radius = meleeRadius;

		var query = new PhysicsShapeQueryParameters3D();
		query.Shape = shape;
		Vector3 forward = GlobalTransform.Basis.Z;
		query.Transform = new Transform3D(Basis.Identity, GlobalPosition + forward * meleeRange);
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

		_debugMeleeSphere.GlobalPosition = GlobalPosition + forward * meleeRange;
		_debugMeleeSphere.Visible = true;
		GetTree().CreateTimer(0.15).Timeout += () => _debugMeleeSphere.Visible = false;
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
