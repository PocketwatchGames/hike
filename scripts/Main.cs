using Godot;
using System;

public partial class Main : Node
{
	[Export] public PackedScene MainMenuScene;
	[Export] public PackedScene GameScene;
	[Export] public PackedScene EditorScene;
	[Export] public WorldGenData DefaultWorldGenData;

	// Default worldgen run parameters. Once a save/lobby UI exists, the seed
	// will come from there (or be rolled fresh per new game) and the size
	// will be authored as part of the world manifest. Hardcoded here for now
	// so a fresh boot deterministically reproduces the same world.
	private const int DEFAULT_WORLD_SEED = 12345;
	private static readonly Vector3I DEFAULT_WORLD_SIZE = new Vector3I(9, 3, 8);

	Node _currentScreen;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		CVarRegistry.Init();
		CVarRegistry.ExecFile(ProjectSettings.GlobalizePath("res://cvars.txt"));
		Loc.Init(CVars.language.Value);
		AddChild(new ConsoleUI());
		AddChild(new DiagnosticsOverlay());

		// Headless debug path: if `worldgen_debug_dump` is set, generate the
		// default world, dump height-field diagnostics to that directory, and
		// quit. Lets the world-gen algorithm be iterated on without the rest
		// of the game ever coming up.
		string debugDumpDir = CVars.worldgenDebugDump.Value;
		if (!string.IsNullOrEmpty(debugDumpDir))
		{
			WorldGen.Generate(DefaultWorldGenData, DEFAULT_WORLD_SEED, DEFAULT_WORLD_SIZE);
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
		//
		// Per-region kit divergence: each region contributes WorldGen.KITS_PER_REGION
		// slots to one flat global palette ([region0 surface, region0 cave, region0
		// underwater, region1 surface, …]). WorldGen stamps the resolved global
		// kitId per voxel based on the chunk's RegionIndex + slot, so the shader
		// just indexes into kit_tiles[] as before. DetailGroups stays on the first
		// region for now — only one detail palette is authored across regions.
		RegionGenData[] regions = worldGenData.Regions ?? System.Array.Empty<RegionGenData>();
		var flatKits = new EnvironmentKitData[regions.Length * WorldGen.KITS_PER_REGION];
		for (int r = 0; r < regions.Length; r++)
		{
			RegionGenData rg = regions[r];
			EnvironmentKitData[] rk = rg?.Kits ?? System.Array.Empty<EnvironmentKitData>();
			for (int s = 0; s < WorldGen.KITS_PER_REGION; s++)
			{
				flatKits[r * WorldGen.KITS_PER_REGION + s] = s < rk.Length ? rk[s] : null;
			}
		}
		ChunkMesh.SetKits(flatKits);
		RegionGenData firstRegion = regions.Length > 0 ? regions[0] : null;
		ChunkMesh.SetDetailGroups(firstRegion?.DetailGroups ?? System.Array.Empty<DetailGroupData>());

		WorldState worldState;
		string worldFilePath = CVars.worldFile.Value;
		if (!string.IsNullOrEmpty(worldFilePath))
		{
			worldState = LoadWorldFromFile(worldFilePath);
			playerPosition = worldState.Spawn;
		}
		else
		{
			worldState = WorldGen.Generate(worldGenData, DEFAULT_WORLD_SEED, DEFAULT_WORLD_SIZE);
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
		worldState.Regions = source.Regions;

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
