using Godot;
using System;

public partial class GuiMainMenu : Node
{
	[Export] public Label versionLabel;
	[Signal] public delegate void OnNewGameEventHandler();
	[Signal] public delegate void OnLoadGameEventHandler(string savePath);

	public override void _Ready()
	{
		if (versionLabel != null)
		{
			versionLabel.Text = Version.Display;
		}
	}

	public void NewGameStandard()
	{
		EmitSignal(SignalName.OnNewGame);
	}

	public void LoadGame()
	{
		EmitSignal(SignalName.OnLoadGame, CVars.savePath.Value);
	}

}
