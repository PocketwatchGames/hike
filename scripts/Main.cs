using Godot;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

public partial class Main : Node
{
	[Export] public PackedScene mainMenuScene;
	[Export] public PackedScene gameScene;
	[Export] public PackedScene editorScene;
	[Export] public PackedScene worldMapPainterScene;
	[Export] public WorldGenData defaultWorldGenData;
	// Loading overlay shown across the new-game / load-game sequence. Lives at
	// the Main level (not inside game.tscn) so the screen is visible during
	// worldgen and scene-load — both phases that happen before GameClient
	// exists. Main drives the early progress; GameClient takes over once the
	// game scene is instantiated.
	[Export] public PackedScene loadingScreenScene;

	// Default worldgen run parameters. Fixed so a fresh boot deterministically
	// reproduces the same world.
	private const int DEFAULT_WORLD_SEED = 12345;
	// The shoreline ocean falloff in WorldGen.BuildHeightMap is anchored to the
	// east world edge (distFromEastEdge = worldMaxX - wx), so the X extent sets
	// how far out the coast lands — no separate coastline knob to retune.
	private static readonly Vector3I DEFAULT_WORLD_SIZE = new Vector3I(18, 3, 16);

	Node _currentScreen;

	// True once the OS has asked the app to close (window X / Alt+F4). Teardown
	// code reads this to skip freeing GPU resources that the final render frame
	// still samples — the process is exiting, so the driver reclaims them anyway,
	// and freeing them mid-shutdown spams "Texture is not a valid texture". The
	// only in-game exit path is the window close (the pause menu's "quit" only
	// returns to the main menu), so this flag covers a real app exit.
	public static bool IsQuitting { get; private set; }

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
		{
			IsQuitting = true;
		}
	}

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		CVarRegistry.Init();
		CVarRegistry.ExecFile(ProjectSettings.GlobalizePath("res://cvars.txt"));
		// Each engine arg after `--` is run as a console command, so cvars can be
		// set at launch without a persistent cvars.txt — e.g.
		// `Godot ... -- "autostart 1" "autoplay 1"`. Runs after the config file so
		// command-line overrides win.
		foreach (string arg in OS.GetCmdlineUserArgs())
		{
			CVarRegistry.ProcessCommand(arg);
		}
		AudioVolume.ApplyAll();
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
			WorldGen.Generate(defaultWorldGenData, DEFAULT_WORLD_SEED, DEFAULT_WORLD_SIZE);
			WorldGen.DumpDebug(ProjectSettings.GlobalizePath(debugDumpDir));
			GetTree().Quit();
			return;
		}

		// Headless / automated path: skip the menu and launch a new game
		// immediately via the menu's own standard-new-game path (reuses all the
		// existing NewGame wiring — StartMainMenu instantiates and connects the
		// menu, NewGameStandard emits OnNewGame → NewGame, which frees the menu).
		if (CVars.autostart.Value)
		{
			StartMainMenu();
			(_currentScreen as GuiMainMenu).NewGameStandard();
			if (CVars.autoplay.Value)
			{
				AddChild(new HeadlessBot());
			}
			return;
		}

		// Same skip-the-menu path for the world editor, so `-- "autostart_editor 1"`
		// drops straight into it (respects `world_file`, else the empty stub).
		if (CVars.autostartEditor.Value)
		{
			StartMainMenu();
			(_currentScreen as GuiMainMenu).StartEditor();
			return;
		}

		StartMainMenu();
	}

	async void NewGame(Vector3 playerPosition, PackedScene playerScene, WorldGenData worldGenData)
	{
		LoadingScreen loadingScreen = ShowLoadingScreen();
		MusicManager.Instance?.SetLoading(true);
		// Yield one frame so the overlay actually renders before the
		// menu's QueueFree (deferred end-of-frame) AND the upcoming
		// synchronous setup work. Without this, the screen could flash
		// the menu under the loading bar on the first frame.
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		_currentScreen.QueueFree();
		_currentScreen = null;

		await StartGame(loadingScreen, playerPosition, playerScene, worldGenData);
	}

	void LoadGame(string savePath)
	{
		//_currentScreen.QueueFree();
		//var (worldState, cameraTileIndex, localTeam) = SaveGame.Load(savePath);
		//StartGame();
	}

	LoadingScreen ShowLoadingScreen()
	{
		LoadingScreen loadingScreen = loadingScreenScene.Instantiate<LoadingScreen>();
		AddChild(loadingScreen);
		loadingScreen.Show("Loading...");
		return loadingScreen;
	}

	async Task StartGame(LoadingScreen loadingScreen, Vector3 playerPosition, PackedScene playerScene, WorldGenData worldGenData)
	{
		// Upload the active world's terrain + detail palettes to the terrain
		// shader / scatter system before any chunk mesh is built. Disk-loaded
		// worlds share the same kit registry as WorldGen.
		//
		// The kit palette is the deduplicated set of all SurfaceKit / CaveKit /
		// SubmergedKit / ShoreKit refs across the zones (TerrainKitData[]); the
		// runtime terrain palette uploaded to ChunkMesh is derived parallel to
		// it (same indices, terrain-per-slot pulled off `TerrainKitData.Terrain`).
		// The detail palette is the deduplicated set of DefaultDetail groups
		// carried by the kits. Two zones that share a kit cost one palette
		// slot, not two.
		var phaseSw = Stopwatch.StartNew();

		loadingScreen.SetProgress(0.02f, "Loading assets...");
		// Force the async-generated NoiseTexture2D ripple/cloud maps to finish
		// generating now — before worldgen pegs the CPU and before SetTerrains
		// builds the water material — so no material ever binds an unready
		// (invalid) texture. See WaterRipples.
		await WaterRipples.EnsureReady(this);
		WorldGen.BindActivePalettes(worldGenData);
		// ChunkMesh.SetTerrains touches RenderingServer (SetShaderParameter),
		// so it must run on the main thread. BindActivePalettes above is pure
		// C# and could move off-thread later if it ever gets expensive.
		ChunkMesh.SetTerrains(WorldGen.ActiveTerrainPalette);
		ChunkMesh.SetDetailGroups(WorldGen.ActiveDetailPalette);
		GD.Print($"[Load] Loading assets: {phaseSw.ElapsedMilliseconds}ms");
		phaseSw.Restart();

		loadingScreen.SetProgress(0.05f, "Generating world...");
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		// Worldgen and .hike loading both run off the main thread so the
		// loading bar can keep rendering and the audio fade keeps ticking
		// while the work runs. Both code paths today are pure C# (no Node
		// or RenderingServer access); GD.Print / GD.PrintErr from off-thread
		// are safe in Godot 4. Anyone adding new worldgen passes must keep
		// the no-Node-API rule or move that pass back to the main thread.
		WorldState worldState = null;
		string worldFilePath = CVars.worldFile.Value;
		bool loadingFromFile = !string.IsNullOrEmpty(worldFilePath);

		// Cache lookup runs only on the no-worldFile path — an explicit
		// worldFile override bypasses cache entirely. Fingerprint covers
		// WORLDGEN_VERSION + WorldFile.VERSION + the content of every
		// reachable .tres / .tscn / .hikescene from worldGenData. Compute
		// on the main thread because ResourceLoader.GetDependencies isn't
		// documented as thread-safe.
		string cachePath = null;
		bool cacheHit = false;
		if (!loadingFromFile && CVars.worldCacheEnabled.Value)
		{
			string fingerprint = WorldGenCache.ComputeFingerprint(worldGenData);
			cachePath = WorldGenCache.GetCachePath(DEFAULT_WORLD_SEED, DEFAULT_WORLD_SIZE, fingerprint);
			cacheHit = WorldGenCache.Exists(cachePath);
			GD.Print($"[Load] WorldGen cache {(cacheHit ? "HIT" : "MISS")}: {cachePath}");
		}

		if (loadingFromFile)
		{
			GD.Print($"[Load] Loading world from file: {worldFilePath}");
		}
		else if (!cacheHit)
		{
			string genDataPath = worldGenData?.ResourcePath;
			GD.Print($"[Load] Generating world (WorldGenData: {(string.IsNullOrEmpty(genDataPath) ? "<null>" : genDataPath)}, seed={DEFAULT_WORLD_SEED}, size={DEFAULT_WORLD_SIZE})");
		}

		try
		{
			if (loadingFromFile)
			{
				worldState = await RunOffThread(() => LoadWorldFromFile(worldFilePath));
				playerPosition = worldState.Spawn;
			}
			else if (cacheHit)
			{
				// Cache load failure (format drift, corrupted bytes, missing
				// SimData on disk) falls through to fresh generation rather
				// than bouncing the player to the main menu. Stale cache
				// files are recoverable.
				try
				{
					string loadPath = cachePath;
					worldState = await RunOffThread(() => LoadWorldFromFile(loadPath));
					playerPosition = worldState.Spawn;
				}
				catch (Exception e)
				{
					GD.PrintErr($"[Load] WorldGen cache load failed ({e.Message}) — regenerating");
					worldState = null;
				}
			}

			if (worldState == null)
			{
				WorldGenData genData = worldGenData;
				worldState = await RunOffThread(() => WorldGen.Generate(genData, DEFAULT_WORLD_SEED, DEFAULT_WORLD_SIZE));
				// WorldGen sets ws.Spawn from genData.playerSpawnPosition (surface-
				// resolved); read it back so the player lands at the authored start
				// area, mirroring the file/cache paths above.
				playerPosition = worldState.Spawn;

				// Foliage occluder stamping uses PackedScene.Instantiate to
				// snapshot each tree scene's FoliageCluster transforms — a
				// Node API call that can't run on the worldgen worker thread.
				// Stamp on the main thread, then re-run ComputeSunlight so
				// the canopy shadows are baked into the persisted sun field
				// before the world hits the cache.
				FoliageStamper.Stamp(worldState);
				LightEngine.ComputeSunlight(worldState);

				if (cachePath != null)
				{
					var saveSw = Stopwatch.StartNew();
					string savePath = cachePath;
					WorldState toSave = worldState;
					try
					{
						WorldGenCache.EnsureDir();
						await RunOffThread(() => { WorldFile.Write(savePath, toSave); return true; });
						GD.Print($"[Load] WorldGen cache saved: {saveSw.ElapsedMilliseconds}ms ({savePath})");
					}
					catch (Exception e)
					{
						GD.PrintErr($"[Load] WorldGen cache save failed: {e.Message}");
					}
				}
			}
			else
			{
				// Disk-loaded paths (worldFile or worldgen cache) come back
				// with sunlight already baked, but the baked bytes are only
				// as good as the FoliageStamper / LightEngine logic at the
				// time of the save. Re-stamp canopy and re-propagate so any
				// changes to the lighting pipeline reach previously-cached
				// worlds without needing a WORLDGEN_VERSION bump. Cost is
				// one ComputeSunlight pass on load (~sub-second at current
				// world sizes); the canopy field also needs to be live so
				// later voxel-edit re-propagation keeps foliage shadowing.
				FoliageStamper.Stamp(worldState);
				LightEngine.ComputeSunlight(worldState);
			}
		}
		catch (Exception e)
		{
			GD.PrintErr($"Worldgen failed: {e}");
			loadingScreen.QueueFree();
			StartMainMenu();
			return;
		}
		string sourceLabel = loadingFromFile
			? "World file loaded"
			: (cacheHit && worldState != null ? "WorldGen cache loaded" : "Worldgen complete");
		GD.Print($"[Load] {sourceLabel}: {phaseSw.ElapsedMilliseconds}ms");
		phaseSw.Restart();

		loadingScreen.SetProgress(0.5f, "Loading scene...");

		// Threaded scene load. ResourceLoader builds the PackedScene's
		// dependency graph (shaders, textures, sub-resources) on a worker
		// thread; the bar polls progress each frame. Instantiate() itself
		// must run on the main thread.
		string scenePath = gameScene.ResourcePath;
		Error reqErr = ResourceLoader.LoadThreadedRequest(scenePath);
		if (reqErr != Error.Ok)
		{
			GD.PrintErr($"LoadThreadedRequest failed for {scenePath}: {reqErr}");
			loadingScreen.QueueFree();
			StartMainMenu();
			return;
		}

		var progressArray = new Godot.Collections.Array();
		ResourceLoader.ThreadLoadStatus status;
		while (true)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			status = ResourceLoader.LoadThreadedGetStatus(scenePath, progressArray);
			float p = progressArray.Count > 0 ? (float)progressArray[0].AsDouble() : 0f;
			loadingScreen.SetProgress(0.5f + p * 0.1f);
			if (status != ResourceLoader.ThreadLoadStatus.InProgress)
			{
				break;
			}
		}

		if (status != ResourceLoader.ThreadLoadStatus.Loaded)
		{
			GD.PrintErr($"Game scene load ended in status {status}");
			loadingScreen.QueueFree();
			StartMainMenu();
			return;
		}

		var loadedScene = (PackedScene)ResourceLoader.LoadThreadedGet(scenePath);
		_currentScreen = loadedScene.Instantiate<Node>();
		AddChild(_currentScreen);
		GD.Print($"[Load] Scene loaded: {phaseSw.ElapsedMilliseconds}ms");
		loadingScreen.SetProgress(0.6f, "Building world...");
		(_currentScreen as GameClient).Init(playerPosition, playerScene, worldGenData, worldState, loadingScreen);
		// Hand the persistent music director the fresh session so it can
		// subscribe to combat/world events; it auto-detaches on quit.
		MusicManager.Instance?.BindGame(_currentScreen as GameClient);
		(_currentScreen as GameClient).onQuitToMenu += () =>
		{
			_currentScreen.QueueFree();
			StartMainMenu();
		};
	}

	// Runs `work` on a thread-pool thread and yields the main thread each
	// frame until it completes. Continuations after `await Task.Run` on
	// Godot don't automatically marshal back to the main thread (no
	// SynchronizationContext is installed), so we poll with a ProcessFrame
	// await — that guarantees the resumption point is on the main thread.
	async Task<T> RunOffThread<T>(Func<T> work)
	{
		Task<T> task = Task.Run(work);
		while (!task.IsCompleted)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		if (task.IsFaulted)
		{
			throw task.Exception?.GetBaseException() ?? new Exception("background task failed");
		}
		return task.Result;
	}

	public static WorldState LoadWorldFromFile(string path)
	{
		var source = new WorldFileChunkSource(path);
		var worldState = new WorldState(source.Min, source.Max, source.SimData);
		worldState.Spawn = source.Spawn;
		worldState.Zones = source.Zones;
		worldState.Regions = source.Regions;

		// Non-chunked globals (the companion) — filed into the persistent store
		// rather than a chunk bucket, mirroring how WorldFile.Write emitted them.
		foreach (EntitySimState e in source.PersistentEntities)
		{
			worldState.AddPersistentEntity(e);
		}

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

	// worldFilePath is the menu's picked world (or a fresh unused path for a
	// new world). It becomes `world_file`, which is what the editor's save
	// writes back to.
	void StartEditor(WorldGenData worldGenData, string worldFilePath)
	{
		_currentScreen.QueueFree();

		if (!string.IsNullOrEmpty(worldFilePath))
		{
			CVars.worldFile.Value = worldFilePath;
		}

		// Same palette bind StartGame does. The editor needs it too: VoxelType
		// .Terrain carries no fixed tile (the shader resolves one per voxel from
		// terrain_tiles), and the Tree / TallGrass brushes read the kit palette
		// under the cursor. Without this the editor renders and paints against
		// whatever a previous session happened to leave bound.
		WorldGen.BindActivePalettes(worldGenData);
		ChunkMesh.SetTerrains(WorldGen.ActiveTerrainPalette);
		ChunkMesh.SetDetailGroups(WorldGen.ActiveDetailPalette);

		// Instantiated before the world is built so CreateEmptyWorld can floor
		// the stub with the same terrain kit the Terrain brush paints.
		var editor = editorScene.Instantiate<WorldEditor>();

		string osPath = ProjectSettings.GlobalizePath(worldFilePath);
		WorldState worldState;
		if (!string.IsNullOrEmpty(worldFilePath) && System.IO.File.Exists(osPath))
		{
			try
			{
				worldState = LoadWorldFromFile(worldFilePath);
			}
			catch (System.Exception e)
			{
				GD.PrintErr($"[Editor] Failed to load world file, falling back to an empty world: {e.Message}");
				worldState = WorldEditor.CreateEmptyWorld(worldGenData, editor.brushPalette?.terrainBrushKit);
			}
		}
		else
		{
			worldState = WorldEditor.CreateEmptyWorld(worldGenData, editor.brushPalette?.terrainBrushKit);
		}

		_currentScreen = editor;
		AddChild(editor);
		editor.Init(worldState);
		editor.onQuitToMenu += () =>
		{
			_currentScreen.QueueFree();
			StartMainMenu();
		};
	}

	// Launch the world-map painting program — the first step in the authoring
	// chain. A pure 2D map editor: it paints the WorldMapData layer document and
	// bakes a WorldState / .hike on save. No live voxel world is built (the old
	// 3D fly-over preview was removed; it can return later as an on-demand
	// feature), so launching is instant and needs no palette / mesh binding.
	void StartPainter(WorldGenData worldGenData)
	{
		_currentScreen.QueueFree();

		var painter = worldMapPainterScene.Instantiate<WorldMapPainter>();
		_currentScreen = painter;
		AddChild(painter);
		painter.Init();
		painter.onQuitToMenu += () =>
		{
			painter.QueueFree();
			StartMainMenu();
		};
	}

	void StartMainMenu()
	{
		// Clears loading on every path back to the menu, including the
		// worldgen / scene-load failure early-returns in StartGame.
		MusicManager.Instance?.SetLoading(false);
		_currentScreen = mainMenuScene.Instantiate<Node>();
		(_currentScreen as GuiMainMenu).OnNewGame += NewGame;
		(_currentScreen as GuiMainMenu).OnLoadGame += LoadGame;
		(_currentScreen as GuiMainMenu).OnStartEditor += StartEditor;
		(_currentScreen as GuiMainMenu).OnStartPainter += StartPainter;
		AddChild(_currentScreen);
	}

}
