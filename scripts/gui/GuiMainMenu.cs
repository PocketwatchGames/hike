using Godot;
using System;

public partial class GuiMainMenu : Node
{
	[Export] public WorldGenData worldGenData;
	[Export] public SimData simData;
	[Export] public Label versionLabel;
	[Signal] public delegate void OnNewGameEventHandler(WorldGenData worldGenData, SimData simData, int seed);
	[Signal] public delegate void OnLoadGameEventHandler(string savePath);

	public override void _Ready()
	{
		if (versionLabel != null)
		{
			versionLabel.Text = Version.Display;
		}
	}

	public void NewGame()
	{
		EmitSignal(SignalName.OnNewGame, worldGenData, simData, 123);
	}

	public void LoadGame()
	{
		EmitSignal(SignalName.OnLoadGame, CVars.savePath.Value);
	}

}
