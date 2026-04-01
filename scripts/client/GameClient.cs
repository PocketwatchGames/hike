using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class GameClient : Node3D
{
	[Export] public Camera3D camera;
	[Export] public Hud hud;
	[Export] public Node2D worldHUD;

	public Action onInit;
	public Action<Vector3, string, ulong, float, Color> onHudText;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;

	public bool paused { get; private set; } = false;

	public void Init()
	{
		onHudText += OnHudTextRequested;
		UpdateCameraTransform();

		onInit?.Invoke();
	}

	void UpdateCameraTransform()
	{
		Vector3 localDir = new Vector3(
			Mathf.Cos(_cameraLatitude) * Mathf.Sin(_cameraLongitude),
			Mathf.Sin(_cameraLatitude),
			Mathf.Cos(_cameraLatitude) * Mathf.Cos(_cameraLongitude)
		);
		float distance = Mathf.Lerp(_maxCameraDistance, _minCameraDistance, _cameraZoom);

		camera.Position = planet.GlobalTransform.Basis * localDir * distance;
		camera.LookAt(Vector3.Zero, planet.GlobalTransform.Basis * Vector3.Up);
	}

	public override void _Process(double deltaTime)
	{
		if (ConsoleUI.IsOpen || paused)
		{
			return;
		}

		float cameraDt = (float)deltaTime; // camera movement is independent of game speed

		// // Camera orbit controls
		// if (Input.IsActionPressed("MoveLeft"))
		// {
		// 	_cameraLongitude -= cameraSpeed * cameraDt;
		// }
		// if (Input.IsActionPressed("MoveRight"))
		// {
		// 	_cameraLongitude += cameraSpeed * cameraDt;
		// }
		// if (Input.IsActionPressed("MoveUp"))
		// {
		// 	_cameraLatitude += cameraSpeed * cameraDt;
		// }
		// if (Input.IsActionPressed("MoveDown"))
		// {
		// 	_cameraLatitude -= cameraSpeed * cameraDt;
		// }

		UpdateCameraTransform();
	}

	public override void _PhysicsProcess(double delta)
	{
		double adjustedDT = delta * timeScales[timeScaleIndex];
		base._PhysicsProcess(adjustedDT);
		if (!paused && adjustedDT > 0)
		{
//			sim.Tick((ulong)(adjustedDT * 1000));
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
		SaveGame.Save();
	}

	public void QuitToMenu()
	{
		onQuitToMenu?.Invoke();
	}

}
