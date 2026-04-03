using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	[Export] public float stepHeight = 0.5f;
	[Export] public float moveSpeed = 7f;
	[Export] public float sneakSpeed = 3f;
	[Export] public Area3D interactArea;

	public ShaderMaterial outlineMaterial;
	public Vector2 InputDir { get; set; }
	public float CameraYaw { get; set; }

	private IInteractive _curInteractive;
	private IInteractive _highlightInteractive;
	private readonly List<IInteractive> _interactiveCollisions = new();
	private readonly List<TallGrass> _tallGrassCollisions = new();
	private float _terrainSpeed = 1f;

	private Sprite3D _highlightOverlay;

	public override void _Ready()
	{
		CollisionLayer = 2; // Layer 2 (bit 1) — players
		CollisionMask = 1;  // Collide with environment only

		_highlightOverlay = new Sprite3D();
		_highlightOverlay.Name = "HighlightOverlay";
		_highlightOverlay.MaterialOverride = outlineMaterial;
		_highlightOverlay.AlphaCut = SpriteBase3D.AlphaCutMode.Disabled;
		_highlightOverlay.Visible = false;
		AddChild(_highlightOverlay);

		interactArea.BodyEntered += OnInteractBodyEntered;
		interactArea.BodyExited += OnInteractBodyExited;
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
				RemoveHighlight();
				ApplyHighlight((Node3D)_highlightInteractive);
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
			if (child is Sprite3D sprite)
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

	private void UpdateTerrainSpeed()
	{
		_terrainSpeed = 1f;
		foreach (TallGrass grass in _tallGrassCollisions)
		{
			_terrainSpeed *= grass.speed;
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
}
