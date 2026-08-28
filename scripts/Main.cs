using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
	// HORIZONTAL extent in chunks (X, Z). World height is not a run knob — it is
	// fitted to the generated terrain (WorldGen.FitVerticalExtent).
	// The shoreline ocean falloff in WorldGen.BuildHeightMap is anchored to the
	// east world edge (distFromEastEdge = worldMaxX - wx), so the X extent sets
	// how far out the coast lands — no separate coastline knob to retune.
	private static readonly Vector2I DEFAULT_WORLD_SIZE = new Vector2I(18, 16);

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
		InputBindings.Apply();
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
			// Generate reads the flat block tables (Blocks.IsSolid and friends)
			// from its very first pass, and this path reaches it long before the
			// game-start binds below. Missing them, the dump died in
			// TagSubmergedKits on a null table rather than on anything to do with
			// terrain. ChunkMesh.SetTerrains is deliberately NOT called here — it
			// touches RenderingServer and generation has no use for it.
			Blocks.Bind();
			var debugRun = new WorldGen(defaultWorldGenData, DEFAULT_WORLD_SEED);
			debugRun.Generate(DEFAULT_WORLD_SIZE);
			debugRun.DumpDebug(ProjectSettings.GlobalizePath(debugDumpDir));
			GetTree().Quit();
			return;
		}

		// Same dump, terrain only — no chunks, lighting, props or roads. None of
		// those change the height field, so this is the loop for iterating on a
		// TerrainGenData.
		string terrainDumpDir = CVars.worldgenTerrainDump.Value;
		if (!string.IsNullOrEmpty(terrainDumpDir))
		{
			var terrainRun = new WorldGen(defaultWorldGenData, DEFAULT_WORLD_SEED);
			terrainRun.GenerateTerrainOnly(DEFAULT_WORLD_SIZE);
			terrainRun.DumpDebug(ProjectSettings.GlobalizePath(terrainDumpDir));
			GetTree().Quit();
			return;
		}

		// Headless shader check: parse every .gdshader and quit, without the
		// menu or a world. `--headless -- "shader_check 1"` is the fast "do the
		// shaders still compile" loop; a full autostart run is not needed.
		if (CVars.shaderCheck.Value)
		{
			_ = ShaderCheck.RunAndQuit(GetTree());
			return;
		}

		// Whether a transparent material can occlude another transparent one via
		// depth_draw_always — the assumption the waterfall/water sort rests on.
		// Needs a real rasterizer, so run this one WINDOWED.
		if (CVars.depthSortCheck.Value)
		{
			_ = DepthSortCheck.RunAndQuit(GetTree());
			return;
		}

		// Same idea for the block catalog: `--headless -- "block_check 1"`
		// validates it and dumps the resolved table without loading a world.
		if (CVars.blockCheck.Value)
		{
			BlockCheck.RunAndQuit(GetTree(), defaultWorldGenData);
			return;
		}

		// And for the authored data as a whole: reports [Tool]-closure gaps (the
		// silent editor data-loss bug) and any .tres that no longer loads.
		if (CVars.resourceCheck.Value)
		{
			ResourceCheck.RunAndQuit(GetTree());
			return;
		}

		// And for the water/land seam: `--headless -- "water_shore_check 1"`
		// meshes synthetic shorelines and reports terrain that sits under the
		// waterline with no water quad over it.
		if (CVars.waterShoreCheck.Value)
		{
			WaterShoreCheck.RunAndQuit(GetTree());
			return;
		}

		// And for a painted world-map document: `--headless -- "worldmap_check
		// res://.../world_map.tres"` reports its water and its cascades off the
		// layer images alone, without opening the painter or baking a .hike.
		if (!string.IsNullOrEmpty(CVars.worldMapCheck.Value))
		{
			WorldMapCheck.RunAndQuit(GetTree(), CVars.worldMapCheck.Value);
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
		// This world's kit palette, resolved ONCE here and handed to whatever
		// builds the world — the generator, the cache loader, or the .hike
		// loader. It is world state (WorldState.Kits), not process state; the
		// only piece of it that has to become process state is the detail-group
		// table below, because there is a single terrain material.
		//
		// ChunkMesh.SetTerrains touches RenderingServer (SetShaderParameter), so
		// it must run on the main thread. Building the palette is pure C# and
		// could move off-thread later if it ever gets expensive.
		KitPalette kitPalette = KitPalette.Build(worldGenData?.kitPalette);
		Blocks.Bind();
		ChunkMesh.SetTerrains();
		ChunkMesh.SetDetailGroups(kitPalette.DetailGroups);
		GD.Print($"[Load] Loading assets: {phaseSw.ElapsedMilliseconds}ms");
		phaseSw.Restart();

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

		// Only the third path actually generates anything; the label said
		// "Generating world" for all three, which is why a cache hit still read
		// as a full regeneration. Set after the cache probe, since that is what
		// decides which of the three this is.
		loadingScreen.SetProgress(0.05f, loadingFromFile || cacheHit ? "Loading world..." : "Generating world...");
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		try
		{
			if (loadingFromFile)
			{
				worldState = await RunOffThread(() => LoadWorldFromFile(worldFilePath, kitPalette));
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
					worldState = await RunOffThread(() => LoadWorldFromFile(loadPath, kitPalette));
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
				// The run owns the height field and the terrain generator (~2 MB
				// of scratch at the default world size). Kept only while the
				// console dump might ask for it — see WorldGen.LastRun.
				var run = new WorldGen(genData, DEFAULT_WORLD_SEED);
				worldState = await RunOffThread(() => run.Generate(DEFAULT_WORLD_SIZE));
				WorldGen.LastRun = CVars.worldgenKeepDebugData.Value ? run : null;
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
				EntityVoxelStamper.Stamp(worldState);
				LightEngine.Relight(worldState);

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
				// Sunlight is BAKED INTO THE FILE and is trusted as such — a
				// full ComputeSunlight here cost ~13s at the default world size
				// and WAS this whole load phase. What the file does not carry
				// still has to be rebuilt:
				//   - the canopy / SunOpaque occluder fields, which are not
				//     serialized (their effect is already in the sun bytes) but
				//     must be live so a later voxel edit re-propagates against
				//     foliage and roofs;
				//   - SkyExposure, which is derived rather than stored.
				// A change to the light pipeline reaches cached worlds through
				// LightEngine.LIGHT_VERSION in the cache fingerprint; a
				// hand-authored .hike keeps what it was baked with until it is
				// re-baked or `relight` is run.
				var stampSw = Stopwatch.StartNew();
				FoliageStamper.Stamp(worldState);
				EntityVoxelStamper.Stamp(worldState);
				long stampMs = stampSw.ElapsedMilliseconds;
				WorldState loaded = worldState;
				// A file with NO baked sunlight at all would otherwise load as a
				// pitch-black world with nothing in the log to say why — the one
				// failure mode trusting the file's light can produce, and it is
				// silent. Cheap to rule out: the scan stops at the first lit
				// voxel, so a normal world pays almost nothing. Any .hike baked
				// before the painter started baking light lands here.
				long relitMs = 0;
				await RunOffThread(() =>
				{
					LightEngine.ComputeSkyExposure(loaded);
					if (HasAnyBakedSunlight(loaded))
					{
						return true;
					}
					// Reported separately from the sky pass: this is the whole
					// cost of the load when it fires (a minute-plus on a large
					// world), and folding it into a neighbouring number hides
					// that the world is being repaired on every single load.
					var relightSw = Stopwatch.StartNew();
					GD.PrintErr("[Load] This world carries NO baked sunlight — relighting it now, which is why this "
						+ "load is slow. It was baked before lighting became part of a bake. Fix it ONCE, either way: "
						+ "re-bake it from its world-map document, or with the world open run "
						+ "`world_export <path>` to write the relit world back over the file.");
					LightEngine.ComputeSunlight(loaded);
					relitMs = relightSw.ElapsedMilliseconds;
					return true;
				});
				long skyMs = stampSw.ElapsedMilliseconds - stampMs - relitMs;
				GD.Print($"[Load]   post-load: occluder stamp={stampMs}ms skyExposure={skyMs}ms"
					+ (relitMs > 0 ? $" RELIGHT(unbaked world)={relitMs}ms" : ""));
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
		(_currentScreen as GameClient).Init(playerPosition, playerScene, worldState, loadingScreen);
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
			Exception inner = task.Exception?.GetBaseException();
			if (inner == null)
			{
				throw new Exception("background task failed");
			}
			// Rethrow via ExceptionDispatchInfo, not `throw inner` — the latter
			// resets the stack trace to this line and loses every frame inside
			// the worker, which is the only part worth reading.
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
		}
		return task.Result;
	}

	// True as soon as one lit voxel turns up. Not "is the light correct" — only
	// whether the file carries a sun field at all.
	static bool HasAnyBakedSunlight(WorldState world)
	{
		foreach (KeyValuePair<Vector3I, ChunkState> kv in world._chunks)
		{
			byte[,,] sun = kv.Value.Sunlight;
			foreach (byte b in sun)
			{
				if (b != 0)
				{
					return true;
				}
			}
		}
		return false;
	}

	// `kits` is the palette the caller intends this world to be read with. NULL
	// means "not building a playable world" (the subscene converter, which only
	// reads block ids) and skips the check below.
	public static WorldState LoadWorldFromFile(string path, KitPalette kits = null)
	{
		var openSw = Stopwatch.StartNew();
		var source = new WorldFileChunkSource(path);
		long headerMs = openSw.ElapsedMilliseconds;
		// Every TerrainId byte in this file is an index into the palette it was
		// BAKED with. Nothing stops that palette from having moved since, and
		// nothing about the stored bytes would look wrong if it had — they would
		// simply mean a different kit, and the world would come back re-textured
		// with no error anywhere. So the file records its slot names and we
		// refuse a world whose palette no longer matches, naming the slot.
		if (kits != null)
		{
			int bad = kits.FirstMismatch(source.KitSlots);
			if (bad >= 0)
			{
				string was = bad < source.KitSlots.Length ? source.KitSlots[bad] : "<past the end>";
				string now = bad < kits.Kits.Length ? kits.Kits[bad]?.ResourcePath ?? "<null>" : "<missing>";
				throw new InvalidDataException(
					$"'{path}' was baked against a different kit palette: slot {bad} was '{was}' and is "
					+ $"now '{now}'. Every TerrainId in the file indexes that table, so the world would "
					+ "load re-textured. The palette is APPEND-ONLY (KitPaletteData) — restore the slot, "
					+ "or re-bake the world.");
			}
			int badDetail = kits.FirstDetailMismatch(source.DetailSlots);
			if (badDetail >= 0)
			{
				throw new InvalidDataException(
					$"'{path}' was baked against a different DETAIL palette at slot {badDetail}. That "
					+ "palette is derived from the kits' defaultDetail, so repointing one moves it "
					+ "without moving the kit palette; every DetailGroup byte in the file indexes it. "
					+ "Restore the detail group, or re-bake the world.");
			}
		}
		var worldState = new WorldState(source.Min, source.Max, source.SimData, kits);
		worldState.Spawn = source.Spawn;
		worldState.Zones = source.Zones;
		worldState.Regions = source.Regions;
		foreach (KeyValuePair<string, Vector3> poi in source.PointsOfInterest)
		{
			worldState.PointsOfInterest[poi.Key] = poi.Value;
		}
		// This world's own quests, party and starting knowledge. Without it the
		// run took all three from whichever WorldGenData the menu had selected —
		// another world's content, and for a hand-painted world usually no
		// quests at all.
		worldState.BindStartContent(source.StartContent);

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
		GD.Print($"[Load]   file: header={headerMs}ms chunks={openSw.ElapsedMilliseconds - headerMs}ms");
		return worldState;
	}

	// documentPath is what the menu picked, or a fresh unused path it minted for
	// a new document. Its extension decides what the editor is editing — a
	// `.hikescene` opens as a scene, anything else as a world — so the two
	// document kinds need no separate signal.
	void StartEditor(WorldGenData worldGenData, string documentPath)
	{
		_currentScreen.QueueFree();

		bool isScene = WorldEditor.KindForPath(documentPath) == EEditorDocumentKind.Scene;
		if (!string.IsNullOrEmpty(documentPath) && !isScene)
		{
			// world_file means "the world the game loads" — a scene document
			// must not claim it.
			CVars.worldFile.Value = documentPath;
		}

		// Same palette bind StartGame does. The editor needs it too: int
		// .Terrain carries no fixed tile (the shader resolves one per voxel from
		// terrain_tiles), and the Tree / TallGrass brushes read the kit palette
		// under the cursor. Without this the editor renders and paints against
		// whatever a previous session happened to leave bound.
		KitPalette editorPalette = KitPalette.Build(worldGenData?.kitPalette);
		Blocks.Bind();
		ChunkMesh.SetTerrains();
		ChunkMesh.SetDetailGroups(editorPalette.DetailGroups);

		var editor = editorScene.Instantiate<WorldEditor>();

		string osPath = ProjectSettings.GlobalizePath(documentPath);
		WorldState worldState;
		bool includeEnv = false;
		if (!string.IsNullOrEmpty(documentPath) && System.IO.File.Exists(osPath))
		{
			try
			{
				worldState = isScene
					? editor.CreateSubsceneWorld(worldGenData, documentPath, out includeEnv)
					: LoadWorldFromFile(documentPath, editorPalette);
				if (!isScene)
				{
					// Same closing move the game's load path makes, and for the
					// same two reasons: SkyExposure is derived rather than
					// stored, and an edit's incremental relight needs the canopy
					// field live or it erases tree shadows around whatever was
					// edited — then saves that back into the .hike. (Sunlight
					// itself comes out of the file baked; nothing re-propagates
					// it here.) The stub paths do their own pairing.
					FoliageStamper.Stamp(worldState);
					EntityVoxelStamper.Stamp(worldState);
					LightEngine.ComputeSkyExposure(worldState);
				}
			}
			catch (System.Exception e)
			{
				GD.PrintErr($"[Editor] Failed to load '{documentPath}', falling back to an empty world: {e.Message}");
				worldState = editor.CreateEmptyWorld(worldGenData);
			}
		}
		else
		{
			worldState = editor.CreateEmptyWorld(worldGenData);
		}

		_currentScreen = editor;
		AddChild(editor);
		editor.Init(worldState, documentPath, includeEnv);
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
	void StartPainter(WorldGenData worldGenData, string documentPath)
	{
		_currentScreen.QueueFree();

		var painter = worldMapPainterScene.Instantiate<WorldMapPainter>();
		// The picked document replaces the one the scene authors as its default.
		// Before Init, which builds the state and every tool off it.
		if (!string.IsNullOrEmpty(documentPath))
		{
			var document = GD.Load<WorldMapData>(documentPath);
			if (document != null)
			{
				painter.data = document;
			}
			else
			{
				GD.PrintErr($"[Painter] '{documentPath}' is not a WorldMapData; opening the default document.");
			}
		}
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
