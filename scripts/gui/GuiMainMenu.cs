using Godot;
using System;
using System.Collections.Generic;

public partial class GuiMainMenu : Node
{
	[Export] public WorldGenData worldGenData;
	[Export] public Label versionLabel;
	// The main button column, and the file/world picker that replaces it.
	[Export] public Control buttonPanel;
	[Export] public Control fileSelector;
	// Controller / keyboard binding list, shown over the menu in place of the
	// button column.
	[Export] public ControlsScreen controlsScreen;
	// Play opens this: pick a save slot to load, or an empty one to start a new
	// game in.
	[Export] public ProfileScreen profileScreen;
	[Export] public ItemList worldList;
	// Selectable worldgen templates shown in worldList; labels is the parallel
	// display text (same length as worldOptions).
	[Export] public WorldGenData[] worldOptions = System.Array.Empty<WorldGenData>();
	[Export] public string[] worldOptionLabels = System.Array.Empty<string>();
	// Directories scanned (non-recursively) for editor documents, per kind.
	[Export] public string[] sceneFileSearchDirs = { SubsceneFile.DEFAULT_SCENE_DIR };
	[Export] public string[] worldFileSearchDirs = { "user://", "res://resources/data/worlds/test_world/map/" };
	// Scanned (non-recursively) for world-map painter documents. A .tres here is
	// filtered by the class its header names, since the layer files, the brush
	// and the placements list share the directory and the extension.
	[Export] public string[] worldMapSearchDirs = { "res://resources/data/worlds/test_world/map/" };
	// An exported build's New Game list is every .hike under here, searched
	// recursively, and nothing else. What ships is decided by the export preset's
	// include filter, so that filter is the one list of playable worlds.
	[Export(PropertyHint.Dir)] public string shippedWorldRoot = "res://resources/data/worlds/";
	// Save target for a brand-new document; uniquified (scene_2.hikescene, ...)
	// when the file already exists so a new one never clobbers an old one. The
	// extension is also what tells the editor which kind it's opening.
	[Export] public string newScenePath = SubsceneFile.DEFAULT_SCENE_DIR + "scene.hikescene";
	[Export] public string newWorldPath = "user://world.hike";
	[Export] public string newSceneLabel = "New Scene";
	[Export] public string newWorldLabel = "New World";
	[Signal] public delegate void OnNewGameEventHandler(Vector3 playerPosition, WorldGenData worldGenData);
	[Signal] public delegate void OnLoadGameEventHandler(string savePath);
	[Signal] public delegate void OnStartEditorEventHandler(WorldGenData worldGenData, string worldFilePath);
	// documentPath is the WorldMapData the picker chose; empty keeps whatever the
	// painter scene authors as its default.
	[Signal] public delegate void OnStartPainterEventHandler(WorldGenData worldGenData, string documentPath);

	private enum SelectorMode
	{
		NewGame,
		Editor,
		Painter,
	}

	// The two fixed rows at the top of the editor list.
	private const int NEW_SCENE_INDEX = 0;
	private const int NEW_WORLD_INDEX = 1;

	private SelectorMode _mode = SelectorMode.NewGame;
	// Document path per worldList row, parallel to the items. Null means the row
	// is not a file: a worldgen template in new-game mode, or one of the two
	// "new document" rows in editor mode (whose path is minted on Continue).
	private readonly List<string> _documentPaths = new List<string>();

	public override void _Ready()
	{
		Input.MouseMode = Input.MouseModeEnum.Visible;
		if (versionLabel != null)
		{
			versionLabel.Text = Version.Display;
		}
		if (profileScreen != null)
		{
			profileScreen.SlotChosen += OnProfileSlotChosen;
			profileScreen.Closed += ShowButtons;
		}
		ShowButtons();
	}

	// --- Button panel ---------------------------------------------------

	public void ShowProfiles()
	{
		if (profileScreen == null)
		{
			ShowNewGameOptions();
			return;
		}
		if (buttonPanel != null)
		{
			buttonPanel.Visible = false;
		}
		if (fileSelector != null)
		{
			fileSelector.Visible = false;
		}
		profileScreen.Open();
	}

	// The slot becomes savepath either way, so a new game's autosaves land in
	// the slot it was started from.
	void OnProfileSlotChosen(string savePath, bool hasSave)
	{
		CVars.savePath.Value = savePath;
		if (hasSave)
		{
			LoadGame();
			return;
		}
		if (profileScreen != null)
		{
			profileScreen.Visible = false;
		}
		ShowNewGameOptions();
	}

	public void ShowNewGameOptions()
	{
		_mode = SelectorMode.NewGame;
		PopulateWorldGenList();
		ShowSelector();
	}

	public void ShowEditorOptions()
	{
		_mode = SelectorMode.Editor;
		PopulateEditorList();
		ShowSelector();
	}

	public void LoadGame()
	{
		EmitSignal(SignalName.OnLoadGame, CVars.savePath.Value);
	}

	public void ShowPainterOptions()
	{
		_mode = SelectorMode.Painter;
		PopulatePainterList();
		ShowSelector();
	}

	public void ShowControls()
	{
		if (controlsScreen == null)
		{
			return;
		}
		if (buttonPanel != null)
		{
			buttonPanel.Visible = false;
		}
		controlsScreen.Open(ShowButtons);
	}

	// --- File selector --------------------------------------------------

	public void SelectorBack()
	{
		// New Game is reached from a profile slot, so Back returns there.
		if (_mode == SelectorMode.NewGame && profileScreen != null)
		{
			ShowProfiles();
			return;
		}
		ShowButtons();
	}

	public void SelectorContinue()
	{
		if (_mode == SelectorMode.Editor)
		{
			StartEditor();
		}
		else if (_mode == SelectorMode.Painter)
		{
			StartPainter();
		}
		else
		{
			NewGameStandard();
		}
	}

	// ItemList double-click / Enter — same as pressing Continue.
	public void OnWorldActivated(int index)
	{
		SelectorContinue();
	}

	private void ShowSelector()
	{
		if (buttonPanel != null)
		{
			buttonPanel.Visible = false;
		}
		if (fileSelector != null)
		{
			fileSelector.Visible = true;
		}
	}

	private void ShowButtons()
	{
		if (profileScreen != null)
		{
			profileScreen.Visible = false;
		}
		if (fileSelector != null)
		{
			fileSelector.Visible = false;
		}
		if (buttonPanel != null)
		{
			buttonPanel.Visible = true;
		}
	}

	// --- Launch paths (also called directly by Main's autostart cvars) ---

	public void NewGameStandard()
	{
		// StartGame loads world_file when it's set and generates when it isn't,
		// so picking a row IS setting that cvar — a .hike row to its path, a
		// template row back to empty. Only a real selection writes it: autostart
		// calls this with no selector shown and must keep its CLI world_file.
		int index = SelectedIndex();
		if (_mode == SelectorMode.NewGame && index >= 0 && index < _documentPaths.Count)
		{
			CVars.worldFile.Value = _documentPaths[index] ?? "";
		}
		EmitSignal(SignalName.OnNewGame, new Vector3(0, 24, 0), SelectedWorldGen());
	}

	public void StartEditor()
	{
		EmitSignal(SignalName.OnStartEditor, worldGenData, SelectedWorldFile());
	}

	public void StartPainter()
	{
		EmitSignal(SignalName.OnStartPainter, worldGenData, SelectedDocumentPath());
	}

	// --- Selection ------------------------------------------------------

	private void PopulateWorldGenList()
	{
		if (worldList == null)
		{
			return;
		}
		worldList.Clear();
		_documentPaths.Clear();
		if (OS.HasFeature("template"))
		{
			AddDocumentsIn(shippedWorldRoot, WorldEditor.WORLD_FILE_EXTENSION, null, recursive: true);
			if (worldList.ItemCount > 0)
			{
				worldList.Select(0);
			}
			else
			{
				GD.PrintErr($"GuiMainMenu: no .{WorldEditor.WORLD_FILE_EXTENSION} under '{shippedWorldRoot}' in this build — "
					+ "add each shipped world to the export preset's non-resource include filter.");
			}
			return;
		}
		for (int i = 0; i < worldOptions.Length; i++)
		{
			_documentPaths.Add(null);   // a template generates; it has no file
			string label = i < worldOptionLabels.Length ? worldOptionLabels[i] : null;
			if (string.IsNullOrEmpty(label))
			{
				label = worldOptions[i]?.ResourceName;
			}
			if (string.IsNullOrEmpty(label))
			{
				label = worldOptions[i]?.ResourcePath.GetFile() ?? $"World {i}";
			}
			worldList.AddItem(label);
		}
		// Baked worlds play directly — the painter's .hike and anything saved
		// out of the editor, listed from the same dirs the editor picker scans.
		AddDocuments(worldFileSearchDirs, WorldEditor.WORLD_FILE_EXTENSION);
		if (worldList.ItemCount > 0)
		{
			worldList.Select(0);
		}
	}

	// The two "new document" entries, then every scene, then every world.
	// Scenes lead because they're the common case; index 0 stays selected so
	// Continue with no selection makes a new scene.
	private void PopulateEditorList()
	{
		if (worldList == null)
		{
			return;
		}
		worldList.Clear();
		_documentPaths.Clear();
		worldList.AddItem(newSceneLabel);
		_documentPaths.Add(null);
		worldList.AddItem(newWorldLabel);
		_documentPaths.Add(null);
		AddDocuments(sceneFileSearchDirs, WorldEditor.SCENE_FILE_EXTENSION);
		AddDocuments(worldFileSearchDirs, WorldEditor.WORLD_FILE_EXTENSION);
		worldList.Select(0);
	}

	// Every WorldMapData the painter can open. No "new document" row: a document
	// is a .tres plus the layer files it names, so making one is an authoring
	// step in the editor rather than something a picker can mint.
	private void PopulatePainterList()
	{
		if (worldList == null)
		{
			return;
		}
		worldList.Clear();
		_documentPaths.Clear();
		AddDocuments(worldMapSearchDirs, "tres", nameof(WorldMapData));
		if (worldList.ItemCount > 0)
		{
			worldList.Select(0);
		}
	}

	// scriptClass, when given, keeps only the .tres files whose header names that
	// class — read as one line rather than loaded, since loading a world-map
	// document pulls in its whole WorldGenData graph and the picker is listing
	// files, not opening them.
	private void AddDocuments(string[] searchDirs, string extension, string scriptClass = null)
	{
		foreach (string dir in searchDirs)
		{
			AddDocumentsIn(dir, extension, scriptClass, recursive: false);
		}
	}

	// DirAccess / FileAccess rather than System.IO: an exported build's res:// is
	// inside the .pck, which only Godot's file API can see.
	private void AddDocumentsIn(string dir, string extension, string scriptClass, bool recursive)
	{
		if (string.IsNullOrEmpty(dir) || !DirAccess.DirExistsAbsolute(dir))
		{
			return;
		}
		foreach (string fileName in DirAccess.GetFilesAt(dir))
		{
			if (!fileName.GetExtension().Equals(extension, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			// PathJoin, not manual concat: trimming slashes off a bare "user://"
			// leaves "user:", which is a bogus path.
			string path = dir.PathJoin(fileName);
			if (scriptClass != null && !IsResourceOfClass(path, scriptClass))
			{
				continue;
			}
			worldList.AddItem(fileName);
			_documentPaths.Add(path);
		}
		if (recursive)
		{
			foreach (string subDir in DirAccess.GetDirectoriesAt(dir))
			{
				AddDocumentsIn(dir.PathJoin(subDir), extension, scriptClass, recursive: true);
			}
		}
	}

	private static bool IsResourceOfClass(string path, string scriptClass)
	{
		using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
		if (file == null)
		{
			GD.PrintErr($"GuiMainMenu: could not read '{path}' ({Godot.FileAccess.GetOpenError()})");
			return false;
		}
		return file.GetLine().Contains($"script_class=\"{scriptClass}\"");
	}

	private int SelectedIndex()
	{
		if (worldList == null)
		{
			return -1;
		}
		int[] selected = worldList.GetSelectedItems();
		return selected.Length > 0 ? selected[0] : -1;
	}

	private WorldGenData SelectedWorldGen()
	{
		// Template rows lead the list, so a row with no document is worldOptions at
		// the same index. An exported build lists no templates at all.
		int index = SelectedIndex();
		if (_mode == SelectorMode.NewGame && index >= 0 && index < worldOptions.Length
			&& index < _documentPaths.Count && _documentPaths[index] == null)
		{
			return worldOptions[index];
		}
		// Reached by a picked .hike row (which carries no template) and by a
		// direct launch (autostart never opens the selector). A world file still
		// needs a WorldGenData for the terrain/block palette bind, so honour the
		// world_gen_index cvar and otherwise fall back to the menu's default.
		int forced = CVars.worldGenIndex.Value;
		if (forced >= 0 && forced < worldOptions.Length)
		{
			return worldOptions[forced];
		}
		return worldGenData ?? (worldOptions.Length > 0 ? worldOptions[0] : null);
	}

	// Path the editor should open, and — through its extension — which kind of
	// document it opens. Empty selection (autostart) keeps the existing
	// `world_file` cvar behavior; the two "new" rows mint an unused path so a
	// new document's save has somewhere to go without overwriting a real file.
	private string SelectedWorldFile()
	{
		if (_mode != SelectorMode.Editor)
		{
			return CVars.worldFile.Value;
		}
		int index = SelectedIndex();
		if (index >= 0 && index < _documentPaths.Count && _documentPaths[index] != null)
		{
			return _documentPaths[index];
		}
		return UnusedDocumentPath(index == NEW_WORLD_INDEX ? newWorldPath : newScenePath);
	}

	// The picked file, or empty for no selection — which is what a direct launch
	// (no selector shown) passes, and means "keep the scene's own default".
	private string SelectedDocumentPath()
	{
		int index = SelectedIndex();
		return index >= 0 && index < _documentPaths.Count ? _documentPaths[index] ?? "" : "";
	}

	private string UnusedDocumentPath(string template)
	{
		if (string.IsNullOrEmpty(template))
		{
			return "";
		}
		string dir = template.GetBaseDir();
		string stem = template.GetFile().GetBaseName();
		string ext = template.GetExtension();
		string candidate = template;
		int suffix = 2;
		while (System.IO.File.Exists(ProjectSettings.GlobalizePath(candidate)))
		{
			candidate = $"{dir}/{stem}_{suffix}.{ext}";
			suffix++;
		}
		return candidate;
	}
}
