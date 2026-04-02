using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class GameClient : Node3D
{
	[Export] public Camera3D camera;
	[Export] public Hud hud;
	[Export] public Node2D worldHUD;
	[Export] public PackedScene hudTextScene;
	float _cameraPitchRadians => Mathf.DegToRad(cameraPitchDegrees);
	[Export] public float cameraPitchDegrees = -65;
	[Export] public float cameraDistance = 20;

	public Action onInit;
	public Action<Vector3, string, ulong, float, Color> onHudText;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;
	

	public bool paused { get; private set; } = false;

	private static readonly double[] timeScales = { 1.0, 2.0, 4.0 };
	private int timeScaleIndex = 0;

	Vector2 _inputDir = Vector2.Zero;
	float _cameraYaw = 45;
	Player _player;
	VoxelWorld _voxelWorld;

	public async void Init(Vector3 playerPosition, PackedScene playerScene)
	{
		onHudText += OnHudTextRequested;
		onInit?.Invoke();

		var worldData = new WorldData();

		_voxelWorld = new VoxelWorld();
		AddChild(_voxelWorld);
		_voxelWorld.SetCamera(camera);
		_voxelWorld.Initialize(worldData, playerPosition);

		while (!_voxelWorld.IsSpawnChunkReady(playerPosition))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}

		_player = playerScene.Instantiate<Player>();
		AddChild(_player);
		_player.GlobalPosition = playerPosition;
		_player.GlobalRotation = Vector3.Zero;

		_voxelWorld.SetPlayerPositionSource(() => _player.GlobalPosition);

		camera.GlobalRotation = new Vector3(_cameraPitchRadians, _cameraYaw, 0);
		camera.GlobalPosition = _player.GlobalPosition + camera.GlobalTransform.Basis.Z * cameraDistance;
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
		_cameraYaw = camera.GlobalRotation.Y;

		camera.GlobalRotation = new Vector3(_cameraPitchRadians, _cameraYaw, 0);
		camera.GlobalPosition = _player.GlobalPosition + camera.GlobalTransform.Basis.Z * cameraDistance;

	}

	public override void _PhysicsProcess(double delta)
	{
		float dt = (float)delta;
		base._PhysicsProcess(delta);
		if (_player != null && !paused && dt > 0)
		{
			Vector3 cameraRelativeMovement = new Vector3(_inputDir.X, 0, _inputDir.Y).Rotated(Vector3.Up, _cameraYaw);
			_player.Velocity = cameraRelativeMovement * 5;
			if (!_player.IsOnFloor())
			{
				_player.Velocity += Vector3.Down * 9.8f;
			}
			_player.MoveAndSlide();
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
