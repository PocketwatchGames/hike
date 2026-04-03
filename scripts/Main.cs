using Godot;
using System;

public partial class Main : Node
{
	[Export] public PackedScene MainMenuScene;
	[Export] public PackedScene GameScene;

	Node _currentScreen;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		CVarRegistry.Init();
		CVarRegistry.ExecFile(ProjectSettings.GlobalizePath("res://cvars.txt"));
		Loc.Init(CVars.language.Value);
		AddChild(new ConsoleUI());

		StartMainMenu();
	}

	void NewGame(Vector3 playerPosition, PackedScene playerScene, WorldGenData worldGenData)
	{
		_currentScreen.QueueFree();
		StartGame(playerPosition, playerScene, worldGenData);
	}

	void LoadGame(string savePath)
	{
		//_currentScreen.QueueFree();
		//var (worldState, cameraTileIndex, localTeam) = SaveGame.Load(savePath);
		//StartGame();
	}

	void StartGame(Vector3 playerPosition, PackedScene playerScene, WorldGenData worldGenData)
	{
		var worldState = new WorldState(worldGenData);
		_currentScreen = GameScene.Instantiate<Node>();
		AddChild(_currentScreen);
		(_currentScreen as GameClient).Init(playerPosition, playerScene, worldState);
		(_currentScreen as GameClient).onQuitToMenu += () =>
		{
			_currentScreen.QueueFree();
			StartMainMenu();
		};
	}

	void StartMainMenu()
	{
		_currentScreen = MainMenuScene.Instantiate<Node>();
		(_currentScreen as GuiMainMenu).OnNewGame += NewGame;
		(_currentScreen as GuiMainMenu).OnLoadGame += LoadGame;
		AddChild(_currentScreen);
	}

}
