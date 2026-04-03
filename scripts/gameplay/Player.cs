using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	[Export] public float stepHeight = 0.5f;
	[Export] public float moveSpeed = 7f;
	[Export] public float sneakSpeed = 3f;
	[Export] public Color outlineColor = new(1f, 1f, 0.8f, 1f);
	[Export] public float outlineWidth = 2f;

	public Vector2 InputDir { get; set; }
	public float CameraYaw { get; set; }

	private IInteractive _curInteractive;
	private IInteractive _highlightInteractive;
	private readonly List<IInteractive> _interactiveCollisions = new();
	private readonly List<TallGrass> _tallGrassCollisions = new();
	private float _terrainSpeed = 1f;

	private static Shader _stencilShader;
	private static Shader _outlineShader;
	private Sprite3D _highlightSprite;
	private Material _highlightOriginalMaterial;

	public override void _Ready()
	{
		CollisionLayer = 2; // Layer 2 (bit 1) — players
		CollisionMask = 1;  // Collide with environment only

		_stencilShader = GD.Load<Shader>("res://shaders/highlight_stencil.gdshader");
		_outlineShader = GD.Load<Shader>("res://shaders/highlight_outline.gdshader");

		var interactArea = new Area3D();
		interactArea.CollisionLayer = 0;
		interactArea.CollisionMask = 4; // Layer 3 (bit 2) — interactive areas

		var shape = new SphereShape3D();
		shape.Radius = 1.5f;
		var collisionShape = new CollisionShape3D();
		collisionShape.Shape = shape;
		collisionShape.Position = new Vector3(0f, 0.75f, 0f);
		interactArea.AddChild(collisionShape);

		interactArea.AreaEntered += OnInteractAreaEntered;
		interactArea.AreaExited += OnInteractAreaExited;

		AddChild(interactArea);

		var terrainArea = new Area3D();
		terrainArea.CollisionLayer = 0;
		terrainArea.CollisionMask = 8; // Layer 4 (bit 3) — terrain modifiers

		var terrainShape = new SphereShape3D();
		terrainShape.Radius = 0.3f;
		var terrainCollision = new CollisionShape3D();
		terrainCollision.Shape = terrainShape;
		terrainCollision.Position = new Vector3(0f, 0.5f, 0f);
		terrainArea.AddChild(terrainCollision);

		terrainArea.AreaEntered += OnTerrainAreaEntered;
		terrainArea.AreaExited += OnTerrainAreaExited;

		AddChild(terrainArea);
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

		Vector3 cameraRelativeMovement = new Vector3(InputDir.X, 0, InputDir.Y).Rotated(Vector3.Up, CameraYaw);
		Velocity = cameraRelativeMovement * speed;
		if (!IsOnFloor())
		{
			Velocity += Vector3.Down * 9.8f;
		}

		// Step up: lift the player before moving so they can clear small obstacles
		bool wasOnFloor = IsOnFloor();
		Vector3 posBeforeStep = GlobalPosition;
		if (wasOnFloor)
		{
			GlobalPosition += Vector3.Up * stepHeight;
		}

		MoveAndSlide();

		// Step down: snap back to the ground after moving
		if (wasOnFloor)
		{
			KinematicCollision3D stepDownResult = MoveAndCollide(Vector3.Down * stepHeight, true);
			if (stepDownResult != null)
			{
				GlobalPosition = stepDownResult.GetPosition();
			}
			else if (IsOnFloor())
			{
				// No collision within step height — already on floor, leave as-is
			}
			else
			{
				// No ground found within step height — revert the lift
				GlobalPosition = new Vector3(
					GlobalPosition.X,
					posBeforeStep.Y,
					GlobalPosition.Z
				);
			}
		}

		// Update highlight interactive
		UpdateHighlightInteractive();

		// Handle interact input
		if (Input.IsActionJustReleased("Interact") && _highlightInteractive != null)
		{
			if (_highlightInteractive.CanActorInteract(this))
			{
				_curInteractive = _highlightInteractive;
				_curInteractive.Complete();
				_curInteractive = null;
			}
		}
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
		Sprite3D sprite = FindChildSprite(node);
		if (sprite == null)
		{
			return;
		}

		_highlightSprite = sprite;
		_highlightOriginalMaterial = sprite.MaterialOverride;

		var outlineMat = new ShaderMaterial();
		outlineMat.Shader = _outlineShader;
		outlineMat.SetShaderParameter("texture_albedo", sprite.Texture);
		outlineMat.SetShaderParameter("outline_color", outlineColor);
		outlineMat.SetShaderParameter("outline_width", outlineWidth);

		var stencilMat = new ShaderMaterial();
		stencilMat.Shader = _stencilShader;
		stencilMat.SetShaderParameter("texture_albedo", sprite.Texture);
		stencilMat.NextPass = outlineMat;

		sprite.MaterialOverride = stencilMat;
	}

	private void RemoveHighlight()
	{
		if (_highlightSprite != null)
		{
			_highlightSprite.MaterialOverride = _highlightOriginalMaterial;
			_highlightSprite = null;
			_highlightOriginalMaterial = null;
		}
	}

	private static Sprite3D FindChildSprite(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child is Sprite3D sprite)
			{
				return sprite;
			}
		}
		return null;
	}

	private void OnInteractAreaEntered(Area3D area)
	{
		Node parent = area.GetParent();
		if (parent is IInteractive interactive)
		{
			_interactiveCollisions.Add(interactive);
		}
	}

	private void OnInteractAreaExited(Area3D area)
	{
		Node parent = area.GetParent();
		if (parent is IInteractive interactive)
		{
			_interactiveCollisions.Remove(interactive);
		}
	}

	private void UpdateTerrainSpeed()
	{
		_terrainSpeed = 1f;
		foreach (TallGrass grass in _tallGrassCollisions)
		{
			_terrainSpeed *= grass.speed;
		}
	}

	private void OnTerrainAreaEntered(Area3D area)
	{
		if (area is TallGrass tallGrass)
		{
			_tallGrassCollisions.Add(tallGrass);
		}
	}

	private void OnTerrainAreaExited(Area3D area)
	{
		if (area is TallGrass tallGrass)
		{
			_tallGrassCollisions.Remove(tallGrass);
		}
	}
}
