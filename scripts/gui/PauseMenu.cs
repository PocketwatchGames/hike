using Godot;
using System;

public partial class PauseMenu : PanelContainer
{
	[Export] public GameManager gameManager;
	[Export] public Label versionLabel;

	override public void _Ready()
	{
		Visible = gameManager.paused;
		gameManager.onPauseToggled += (p) => { Visible = p; };
		if (versionLabel != null)
		{
			versionLabel.Text = Version.Full;
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("TogglePause"))
		{
			gameManager.TogglePause();
			GetViewport().SetInputAsHandled();
		}
	}

	public void OnResumeButtonPressed()
	{
		gameManager.TogglePause();
	}
	public void OnQuitButtonPressed()
	{
		gameManager.QuitToMenu();
	}

	public void OnSaveButtonPressed()
	{
		gameManager.Save();
	}

}
