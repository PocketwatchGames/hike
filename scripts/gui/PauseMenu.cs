using Godot;
using System;

public partial class PauseMenu : PanelContainer
{
	[Export] public GameClient gameClient;
	[Export] public Label versionLabel;

	override public void _Ready()
	{
		Visible = gameClient.paused;
		gameClient.onPauseToggled += (p) => { Visible = p; };
		VisibilityChanged += OnVisibilityChanged;
		if (versionLabel != null)
		{
			versionLabel.Text = Version.Full;
		}
	}

	void OnVisibilityChanged()
	{
		Input.MouseMode = Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("TogglePause"))
		{
			gameClient.TogglePause();
			GetViewport().SetInputAsHandled();
		}
	}

	public void OnResumeButtonPressed()
	{
		gameClient.TogglePause();
	}
	public void OnQuitButtonPressed()
	{
		gameClient.QuitToMenu();
	}

	public void OnSaveButtonPressed()
	{
		gameClient.Save();
	}

}
