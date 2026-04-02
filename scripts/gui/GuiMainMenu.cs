using Godot;
using System;

public partial class GuiMainMenu : Node
{
	[Export] public PackedScene playerScene;
	[Export] public Label versionLabel;
	[Signal] public delegate void OnNewGameEventHandler(Vector3 playerPosition, PackedScene playerScene);
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
		EmitSignal(SignalName.OnNewGame, new Vector3(0,4,0), playerScene);
	}

	public void LoadGame()
	{
		EmitSignal(SignalName.OnLoadGame, CVars.savePath.Value);
	}

}
