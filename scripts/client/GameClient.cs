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
	[Export] public ShaderMaterial outlineMaterial;

	public Action onInit;
	public Action<Vector3, string, ulong, float, Color> onHudText;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;

	public bool paused { get; private set; } = false;

	Player _player;
	VoxelWorld _voxelState;
	Vector2 _mousePosition;

	public async void Init(Vector3 playerPosition, PackedScene playerScene, WorldState worldState)
	{
		onHudText += OnHudTextRequested;
		onInit?.Invoke();

		_voxelState = new VoxelWorld();
		AddChild(_voxelState);
		_voxelState.SetCamera(camera);
		_voxelState.Initialize(worldState, playerPosition);

		while (!_voxelState.IsSpawnChunkReady(playerPosition))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}

		_player = playerScene.Instantiate<Player>();
		_player.outlineMaterial = outlineMaterial;
		AddChild(_player);
		_player.GlobalPosition = playerPosition;
		_player.GlobalRotation = Vector3.Zero;

		_voxelState.SetPlayerPositionSource(() => _player.GlobalPosition);

		camera.Init(this);
		camera.SetInitialPosition(_player.GlobalPosition);
	}

	public override void _Process(double deltaTime)
	{
		if (_player == null || ConsoleUI.IsOpen || paused)
		{
			return;
		}
		_player.ProcessInput(camera.Yaw);

		camera.UpdateCamera(deltaTime, _player.GlobalPosition);
		_voxelState.CullProps(camera.Clip);
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

	void OnHudTextRequested(Vector3 position, string text, ulong fadeMs, float verticalMovement, Color color)
	{
		HudText.Create(hudTextScene, camera, position, text, fadeMs, verticalMovement, color, this);
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
