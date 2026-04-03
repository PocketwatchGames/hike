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

	public Action onInit;
	public Action<Vector3, string, ulong, float, Color> onHudText;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;

	public bool paused { get; private set; } = false;

	Vector2 _inputDir = Vector2.Zero;
	Player _player;
	VoxelWorld _voxelState;

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
		Vector2 inputDir = Vector2.Zero;
		inputDir.X -= Input.GetActionStrength("MoveLeft");
		inputDir.X += Input.GetActionStrength("MoveRight");
		inputDir.Y -= Input.GetActionStrength("MoveUp");
		inputDir.Y += Input.GetActionStrength("MoveDown");
		_inputDir = inputDir.LengthSquared() > 1 ? inputDir.Normalized() : inputDir;

		camera.UpdateCamera(deltaTime, _player.GlobalPosition);
		_voxelState.CullProps(camera.Clip);
	}

	public override void _PhysicsProcess(double delta)
	{
		base._PhysicsProcess(delta);
		if (_player != null && !paused)
		{
			_player.InputDir = _inputDir;
			_player.CameraYaw = camera.Yaw;
		}
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
