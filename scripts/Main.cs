using Godot;
using System;

public partial class Main : Node
{
	[Export] public PackedScene MainMenuScene;
	[Export] public PackedScene GameScene;
	[Export] public PackedScene EditorScene;
	[Export] public WorldGenData DefaultWorldGenData;

	Node _currentScreen;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		CVarRegistry.Init();
		CVarRegistry.ExecFile(ProjectSettings.GlobalizePath("res://cvars.txt"));
		Loc.Init(CVars.language.Value);
		AddChild(new ConsoleUI());

		// Headless debug path: if `worldgen_debug_dump` is set, generate the
		// default world, dump height-field diagnostics to that directory, and
		// quit. Lets the world-gen algorithm be iterated on without the rest
		// of the game ever coming up.
		string debugDumpDir = CVars.worldgenDebugDump.Value;
		if (!string.IsNullOrEmpty(debugDumpDir))
		{
			WorldGen.Generate(DefaultWorldGenData);
			WorldGen.DumpDebug(ProjectSettings.GlobalizePath(debugDumpDir));
			GetTree().Quit();
			return;
		}

		StartMainMenu();
	}

	void NewGame(Vector3 playerPosition, PackedScene playerScene, PlayerSpawnData playerSpawnData, WorldGenData worldGenData)
	{
		_currentScreen.QueueFree();
		StartGame(playerPosition, playerScene, playerSpawnData, worldGenData);
	}

	void LoadGame(string savePath)
	{
		//_currentScreen.QueueFree();
		//var (worldState, cameraTileIndex, localTeam) = SaveGame.Load(savePath);
		//StartGame();
	}

	void StartGame(Vector3 playerPosition, PackedScene playerScene, PlayerSpawnData playerSpawnData, WorldGenData worldGenData)
	{
		// Upload the active world's kit palette to the terrain shader before
		// any chunk mesh is built. Disk-loaded worlds currently share the same
		// kit registry as WorldGen — a later .hike change will embed kit paths
		// in the file header so exported worlds can own their palette.
		ChunkMesh.SetKits(worldGenData.Kits);
		ChunkMesh.SetDetailGroups(worldGenData.DetailGroups);

		WorldState worldState;
		string worldFilePath = CVars.worldFile.Value;
		if (!string.IsNullOrEmpty(worldFilePath))
		{
			worldState = LoadWorldFromFile(worldFilePath);
			playerPosition = worldState.Spawn;
		}
		else
		{
			worldState = WorldGen.Generate(worldGenData);
			worldState.Spawn = playerPosition;
		}

		_currentScreen = GameScene.Instantiate<Node>();
		AddChild(_currentScreen);
		(_currentScreen as GameClient).Init(playerPosition, playerScene, playerSpawnData, worldState);
		(_currentScreen as GameClient).onQuitToMenu += () =>
		{
			_currentScreen.QueueFree();
			StartMainMenu();
		};
	}

	public static WorldState LoadWorldFromFile(string path)
	{
		var source = new WorldFileChunkSource(path);
		var worldState = new WorldState(source.Min, source.Max, source.SimData);
		worldState.Spawn = source.Spawn;

		foreach (Vector3I coord in source.EnumerateChunkCoords())
		{
			if (source.TryLoadChunk(coord, out ChunkState chunk, out System.Collections.Generic.List<EntitySimState> entities))
			{
				worldState._chunks[coord] = chunk;
				if (entities != null)
				{
					foreach (EntitySimState e in entities)
					{
						worldState.AddEntity(e);
					}
				}
			}
		}

		source.Dispose();
		return worldState;
	}

	void StartEditor(WorldGenData worldGenData)
	{
		_currentScreen.QueueFree();

		string worldFilePath = CVars.worldFile.Value;
		string osPath = ProjectSettings.GlobalizePath(worldFilePath);
		GD.Print($"[Editor] worldFile cvar='{worldFilePath}', osPath='{osPath}', exists={System.IO.File.Exists(osPath)}");
		WorldState worldState;
		if (!string.IsNullOrEmpty(worldFilePath) && System.IO.File.Exists(osPath))
		{
			try
			{
				GD.Print("[Editor] Loading world from file");
				worldState = LoadWorldFromFile(worldFilePath);
			}
			catch (System.Exception e)
			{
				GD.PrintErr($"[Editor] Failed to load world file: {e.Message}");
				GD.Print("[Editor] Creating empty world instead");
				worldState = WorldEditor.CreateEmptyWorld(worldGenData);
			}
		}
		else
		{
			GD.Print("[Editor] Creating empty world");
			worldState = WorldEditor.CreateEmptyWorld(worldGenData);
		}

		_currentScreen = EditorScene.Instantiate<Node>();
		AddChild(_currentScreen);
		(_currentScreen as WorldEditor).Init(worldState);
		(_currentScreen as WorldEditor).onQuitToMenu += () =>
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
		(_currentScreen as GuiMainMenu).OnStartEditor += StartEditor;
		AddChild(_currentScreen);
	}

}
