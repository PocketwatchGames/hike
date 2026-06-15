using Godot;
using System;

public partial class GuiMainMenu : Node
{
	[Export] public PackedScene playerScene;
	[Export] public PlayerSpawnData playerSpawnData;
	[Export] public WorldGenData worldGenData;
	[Export] public Label versionLabel;
	[Signal] public delegate void OnNewGameEventHandler(Vector3 playerPosition, PackedScene playerScene, PlayerSpawnData playerSpawnData, WorldGenData worldGenData);
	[Signal] public delegate void OnLoadGameEventHandler(string savePath);
	[Signal] public delegate void OnStartEditorEventHandler(WorldGenData worldGenData);
	[Signal] public delegate void OnStartPainterEventHandler(WorldGenData worldGenData);

	public override void _Ready()
	{
		Input.MouseMode = Input.MouseModeEnum.Visible;
		if (versionLabel != null)
		{
			versionLabel.Text = Version.Display;
		}
	}

	public void NewGameStandard()
	{
		EmitSignal(SignalName.OnNewGame, new Vector3(0,24,0), playerScene, playerSpawnData, worldGenData);
	}

	public void LoadGame()
	{
		EmitSignal(SignalName.OnLoadGame, CVars.savePath.Value);
	}

	public void StartEditor()
	{
		EmitSignal(SignalName.OnStartEditor, worldGenData);
	}

	public void StartPainter()
	{
		EmitSignal(SignalName.OnStartPainter, worldGenData);
	}
}
