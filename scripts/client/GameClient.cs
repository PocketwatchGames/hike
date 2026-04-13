using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class GameClient : Node3D
{
	[Export] public GameCamera camera;
	[Export] public Hud hud;
	[Export] public Node2D worldHUD;
	[Export] public PackedScene hudTextScene;
	[Export] public PackedScene interactHudScene;
	[Export] public ShaderMaterial outlineMaterial;

	public Action onInit;
	public Action<Vector3, string, ulong, float, Color> onHudText;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;

	public bool paused { get; private set; } = false;

	Player _player;
	World _world;
	Vector2 _mousePosition;
	Sprite3D _highlightOverlay;
	InteractHUD _interactHUD;

	public override void _Ready()
	{
		_highlightOverlay = new Sprite3D();
		_highlightOverlay.Name = "HighlightOverlay";
		_highlightOverlay.MaterialOverride = outlineMaterial;
		_highlightOverlay.AlphaCut = SpriteBase3D.AlphaCutMode.Disabled;
		_highlightOverlay.Visible = false;
		AddChild(_highlightOverlay);
	}

	public async void Init(Vector3 playerPosition, PackedScene playerScene, PlayerSpawnData playerSpawnData, WorldState worldState)
	{
		onHudText += OnHudTextRequested;
		onInit?.Invoke();

		_world = new World();
		_world.onMobSpawned += OnMobSpawned;
		_world.onMobRemoved += OnMobRemoved;
		AddChild(_world);
		_world.Initialize(worldState, playerPosition, camera, () => _player?.GlobalPosition ?? playerPosition);

		while (!_world.IsSpawnChunkReady(playerPosition))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}

		_player = playerScene.Instantiate<Player>();
		_player.onHighlightChanged += OnPlayerHighlightChanged;
		_player.onInteractChanged += OnPlayerInteractChanged;
		AddChild(_player);
		_player.Initialize(_world, playerSpawnData, playerPosition, Vector3.Zero);

		_world.SetPlayer(_player);

		camera.Init(this);
		camera.SetInitialPosition(_player.GlobalPosition);

		if (camera.WaterCapPlane.MaterialOverride is ShaderMaterial waterCapMat)
		{
			_world.SetLightMapUniforms(waterCapMat);
		}
	}

	public override void _Process(double deltaTime)
	{
		if (_player == null || ConsoleUI.IsOpen || paused)
		{
			return;
		}
		_world.Tick(deltaTime);
		_player.ProcessInput(camera.Yaw);

		camera.UpdateCamera(deltaTime, _player.GlobalPosition);
		CullProps(camera.Clip);
	}

	public override void _PhysicsProcess(double delta)
	{
		base._PhysicsProcess(delta);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		base._UnhandledInput(e);

		if (e.IsActionPressed("TogglePause"))
		{
			TogglePause();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (paused)
		{
			return;
		}

		if (e is InputEventMouseMotion mouseMotion)
		{
			if (_player != null)
			{
				_mousePosition += mouseMotion.Relative;
				float mouseSensitivity = 0.1f;
				if (_mousePosition.LengthSquared() > 1.0f / (mouseSensitivity * mouseSensitivity)) // Prevent overflow from large mouse movements
				{
					_mousePosition = _mousePosition.Normalized() / mouseSensitivity;
				}
				_player.ProcessMouseMotion(_mousePosition, camera.Yaw);
			}
		}

		if (e.IsActionPressed("CameraLeft"))
		{
			camera.RotateLeft();
		}

		if (e.IsActionPressed("CameraRight"))
		{
			camera.RotateRight();
		}

		if (e.IsActionPressed("CameraDown"))
		{
			camera.ToggleClipAlways();
		}

	}

	void CullProps(float cameraClip)
	{
		foreach (List<Node3D> entities in _world.ActiveEntities.Values)
		{
			foreach (Node3D entity in entities)
			{
				entity.Visible = entity.GlobalPosition.Y < cameraClip;
			}
		}
	}

	void OnPlayerHighlightChanged(Node3D node)
	{
		RemoveHighlight();
		if (node != null)
		{
			ApplyHighlight(node);
		}
	}

	void ApplyHighlight(Node3D node)
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

	void RemoveHighlight()
	{
		_highlightOverlay.Visible = false;
		_highlightOverlay.Reparent(this, false);
	}

	static Sprite3D FindChildSprite(Node node)
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

	void OnHudTextRequested(Vector3 position, string text, ulong fadeMs, float verticalMovement, Color color)
	{
		HudText.Create(hudTextScene, _world, camera, position, text, fadeMs, verticalMovement, color, this);
	}

	void OnPlayerInteractChanged(IInteractive interactive)
	{
		if (_interactHUD != null)
		{
			_interactHUD.QueueFree();
			_interactHUD = null;
		}
		if (interactive != null && interactHudScene != null)
		{
			_interactHUD = InteractHUD.Create(interactHudScene, camera, _player, interactive, worldHUD);
		}
	}

	void OnMobSpawned(Mob mob)
	{
		if (mob.HudScene != null)
		{
			MobHUD.Create(mob.HudScene, camera, mob, worldHUD);
		}
	}

	void OnMobRemoved(Mob mob)
	{
	}

	public void TogglePause()
	{
		paused = !paused;
		onPauseToggled?.Invoke(paused);
	}

	public void Save()
	{
		SaveGame.Save(CVars.savePath.Value);
	}

	public void QuitToMenu()
	{
		onQuitToMenu?.Invoke();
	}

}
