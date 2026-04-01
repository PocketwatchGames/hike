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

	public Action onInit;
	public Action<Vector3, string, ulong, float, Color> onHudText;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;

	public bool paused { get; private set; } = false;

	private static readonly double[] timeScales = { 1.0, 2.0, 4.0 };
	private int timeScaleIndex = 0;

	public void Init()
	{
		onHudText += OnHudTextRequested;
		onInit?.Invoke();
	}

	public override void _Process(double deltaTime)
	{
		if (ConsoleUI.IsOpen || paused)
		{
			return;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		double adjustedDT = delta * timeScales[timeScaleIndex];
		base._PhysicsProcess(adjustedDT);
		if (!paused && adjustedDT > 0)
		{
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
