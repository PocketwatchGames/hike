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
	// The editor and the painter open documents through these rather than a
	// list the menu searched for: a document is wherever its author put it.
	// Filters and mode are set per use; the scene authors size and access.
	[Export] public FileDialog openDialog;
	[Export] public FileDialog newDialog;
	// Why an Open or a New was refused. A file dialog cannot say it itself.
	[Export] public AcceptDialog errorDialog;
	// Selectable worldgen templates shown in worldList; labels is the parallel
	// display text (same length as worldOptions).
	[Export] public WorldGenData[] worldOptions = System.Array.Empty<WorldGenData>();
	[Export] public string[] worldOptionLabels = System.Array.Empty<string>();
	// The New Game list is every .hike under here, searched recursively. In an
	// exported build that is all it lists, and what ships is decided by the
	// export preset's include filter, so that filter is the one list of
	// playable worlds.
	[Export(PropertyHint.Dir)] public string shippedWorldRoot = "res://resources/data/worlds/";
	// A development build's New Game list also offers the worlds the editor
	// saved here (non-recursive: user:// also holds saves and caches).
	[Export(PropertyHint.Dir)] public string userWorldDir = "user://";
	// Where the open / new dialogs start the first time in a session; after
	// that they reopen wherever the last pick of that kind was made.
	[Export(PropertyHint.Dir)] public string editorDialogStartDir = SubsceneFile.DEFAULT_SCENE_DIR;
	[Export(PropertyHint.Dir)] public string painterDialogStartDir = "res://resources/data/worlds/";
	// What New World Map copies: the shared kit references every document
	// needs (finish, terrain palette, sim data, default ground) and a default
	// size. The new document's start content is the author's to set.
	[Export] public WorldMapData newWorldMapTemplate;
	// Save target for a brand-new editor document; uniquified (scene_2.hikescene,
	// ...) when the file already exists so a new one never clobbers an old one.
	// The extension is also what tells the editor which kind it's opening.
	[Export] public string newScenePath = SubsceneFile.DEFAULT_SCENE_DIR + "scene.hikescene";
	[Export] public string newWorldPath = "user://world.hike";
	[Export] public string newSceneLabel = "New Scene";
	[Export] public string newWorldLabel = "New World";
	[Export] public string newWorldMapLabel = "New World Map…";
	[Export] public string openDocumentLabel = "Open…";
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

	// The fixed rows of the editor and painter lists. They are actions, not
	// files: the files are reached through the dialogs.
	private const int EDITOR_NEW_SCENE_INDEX = 0;
	private const int EDITOR_NEW_WORLD_INDEX = 1;
	private const int EDITOR_OPEN_INDEX = 2;
	private const int PAINTER_NEW_INDEX = 0;
	private const int PAINTER_OPEN_INDEX = 1;

	private const string WORLD_MAP_EXTENSION = "tres";

	// Last folder a dialog was used in, per tool. Static so it survives the
	// menu being freed and rebuilt between sessions of the tools.
	private static readonly Dictionary<SelectorMode, string> s_lastDialogDir = new Dictionary<SelectorMode, string>();

	private SelectorMode _mode = SelectorMode.NewGame;
	// Document path per New Game row, parallel to the items. Null means the row
	// is a worldgen template, which generates and has no file.
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
		PopulateActions(newSceneLabel, newWorldLabel, openDocumentLabel);
		ShowSelector();
	}

	public void LoadGame()
	{
		EmitSignal(SignalName.OnLoadGame, CVars.savePath.Value);
	}

	public void ShowPainterOptions()
	{
		_mode = SelectorMode.Painter;
		PopulateActions(newWorldMapLabel, openDocumentLabel);
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
		int index = SelectedIndex();
		if (_mode == SelectorMode.Editor)
		{
			if (index == EDITOR_OPEN_INDEX)
			{
				PopupOpenDialog("*.hikescene ; Scene", "*.hike ; World");
				return;
			}
			StartEditor();
		}
		else if (_mode == SelectorMode.Painter)
		{
			if (index == PAINTER_OPEN_INDEX)
			{
				PopupOpenDialog($"*.{WORLD_MAP_EXTENSION} ; World Map");
				return;
			}
			PopupNewWorldMapDialog();
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

	// A new editor document, or — reached with no selector shown, as autostart
	// does — whatever `world_file` names.
	public void StartEditor()
	{
		EmitSignal(SignalName.OnStartEditor, worldGenData, NewEditorDocumentPath());
	}

	// --- Dialogs ----------------------------------------------------------

	// The dialogs browse the real filesystem and hand back an OS path; the
	// tools want a Godot path, so a pick inside the project or user data folds
	// back to res:// or user://.
	private void PopupOpenDialog(params string[] filters)
	{
		if (openDialog == null)
		{
			return;
		}
		openDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
		openDialog.Filters = filters;
		openDialog.CurrentDir = ProjectSettings.GlobalizePath(DialogStartDir());
		openDialog.PopupCentered();
	}

	private void PopupNewWorldMapDialog()
	{
		if (newDialog == null)
		{
			return;
		}
		newDialog.FileMode = FileDialog.FileModeEnum.SaveFile;
		newDialog.Filters = new[] { $"*.{WORLD_MAP_EXTENSION} ; World Map" };
		newDialog.CurrentDir = ProjectSettings.GlobalizePath(DialogStartDir());
		newDialog.CurrentFile = "world_map." + WORLD_MAP_EXTENSION;
		newDialog.PopupCentered();
	}

	private string DialogStartDir()
	{
		if (s_lastDialogDir.TryGetValue(_mode, out string last) && DirAccess.DirExistsAbsolute(last))
		{
			return last;
		}
		return _mode == SelectorMode.Painter ? painterDialogStartDir : editorDialogStartDir;
	}

	public void OnOpenFileSelected(string osPath)
	{
		string path = ToGodotPath(osPath);
		s_lastDialogDir[_mode] = path.GetBaseDir();
		if (_mode == SelectorMode.Painter)
		{
			if (!path.StartsWith("res://"))
			{
				ShowError($"{path}\n\nA world map document must be inside the project folder.");
				return;
			}
			if (!IsResourceOfClass(path, nameof(WorldMapData)))
			{
				ShowError($"{path.GetFile()} is not a world map document.");
				return;
			}
			EmitSignal(SignalName.OnStartPainter, worldGenData, path);
			return;
		}
		string ext = path.GetExtension();
		if (!ext.Equals(WorldEditor.SCENE_FILE_EXTENSION, StringComparison.OrdinalIgnoreCase)
			&& !ext.Equals(WorldEditor.WORLD_FILE_EXTENSION, StringComparison.OrdinalIgnoreCase))
		{
			ShowError($"{path.GetFile()} is not a scene (.{WorldEditor.SCENE_FILE_EXTENSION}) "
				+ $"or a world (.{WorldEditor.WORLD_FILE_EXTENSION}).");
			return;
		}
		EmitSignal(SignalName.OnStartEditor, worldGenData, path);
	}

	// A document is its folder (every layer is a fixed name beside the .tres),
	// so a new one needs a folder no other document already claims — otherwise
	// it would open, paint and bake over that document's layers.
	public void OnNewFileSelected(string osPath)
	{
		string path = ToGodotPath(osPath);
		if (!path.GetExtension().Equals(WORLD_MAP_EXTENSION, StringComparison.OrdinalIgnoreCase))
		{
			path += "." + WORLD_MAP_EXTENSION;
		}
		string dir = path.GetBaseDir();
		s_lastDialogDir[_mode] = dir;
		if (!path.StartsWith("res://"))
		{
			ShowError($"{path}\n\nA world map document must be inside the project folder.");
			return;
		}
		if (newWorldMapTemplate == null)
		{
			ShowError("No new-world-map template is set on the main menu.");
			return;
		}
		string claimant = DocumentFileIn(dir);
		if (claimant != null)
		{
			ShowError($"{dir} already holds a world map ({claimant}).\n\n"
				+ "A world map is its folder: make a new, empty folder for this one.");
			return;
		}
		var document = (WorldMapData)newWorldMapTemplate.Duplicate();
		Error err = ResourceSaver.Save(document, path);
		if (err != Error.Ok)
		{
			ShowError($"Could not write {path}: {err}");
			return;
		}
		EmitSignal(SignalName.OnStartPainter, worldGenData, path);
	}

	// The first file in dir that makes it some document's folder: a WorldMapData,
	// or a layer one left behind. Null when the folder is free.
	private static string DocumentFileIn(string dir)
	{
		if (!DirAccess.DirExistsAbsolute(dir))
		{
			return null;
		}
		foreach (string fileName in DirAccess.GetFilesAt(dir))
		{
			if (Array.IndexOf(WorldMapData.LAYER_FILES, fileName) >= 0)
			{
				return fileName;
			}
			if (fileName.GetExtension().Equals(WORLD_MAP_EXTENSION, StringComparison.OrdinalIgnoreCase)
				&& IsResourceOfClass(dir.PathJoin(fileName), nameof(WorldMapData)))
			{
				return fileName;
			}
		}
		return null;
	}

	private void ShowError(string message)
	{
		GD.PrintErr($"GuiMainMenu: {message}");
		if (errorDialog != null)
		{
			errorDialog.DialogText = message;
			errorDialog.PopupCentered();
		}
	}

	private static string ToGodotPath(string osPath)
	{
		string path = osPath.Replace('\\', '/');
		string userDir = ProjectSettings.GlobalizePath("user://").Replace('\\', '/').TrimEnd('/');
		if (path.StartsWith(userDir + "/", StringComparison.OrdinalIgnoreCase))
		{
			return "user://" + path.Substring(userDir.Length + 1);
		}
		return ProjectSettings.LocalizePath(path);
	}

	// --- Lists ------------------------------------------------------------

	private void PopulateWorldGenList()
	{
		if (worldList == null)
		{
			return;
		}
		worldList.Clear();
		_documentPaths.Clear();
		if (!OS.HasFeature("template"))
		{
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
			AddWorldFilesIn(userWorldDir, recursive: false);
		}
		AddWorldFilesIn(shippedWorldRoot, recursive: true);
		if (worldList.ItemCount > 0)
		{
			worldList.Select(0);
		}
		else if (OS.HasFeature("template"))
		{
			GD.PrintErr($"GuiMainMenu: no .{WorldEditor.WORLD_FILE_EXTENSION} under '{shippedWorldRoot}' in this build — "
				+ "add each shipped world to the export preset's non-resource include filter.");
		}
	}

	private void PopulateActions(params string[] labels)
	{
		if (worldList == null)
		{
			return;
		}
		worldList.Clear();
		_documentPaths.Clear();
		foreach (string label in labels)
		{
			worldList.AddItem(label);
		}
		worldList.Select(0);
	}

	// DirAccess rather than System.IO: an exported build's res:// is inside the
	// .pck, which only Godot's file API can see.
	private void AddWorldFilesIn(string dir, bool recursive)
	{
		if (string.IsNullOrEmpty(dir) || !DirAccess.DirExistsAbsolute(dir))
		{
			return;
		}
		foreach (string fileName in DirAccess.GetFilesAt(dir))
		{
			if (!fileName.GetExtension().Equals(WorldEditor.WORLD_FILE_EXTENSION, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			// PathJoin, not manual concat: trimming slashes off a bare "user://"
			// leaves "user:", which is a bogus path.
			string path = dir.PathJoin(fileName);
			// A world's folder names it better than its .hike does — every
			// painted world bakes to the same file name.
			worldList.AddItem(path.StartsWith(shippedWorldRoot) ? path.Substring(shippedWorldRoot.Length) : fileName);
			_documentPaths.Add(path);
		}
		if (recursive)
		{
			foreach (string subDir in DirAccess.GetDirectoriesAt(dir))
			{
				AddWorldFilesIn(dir.PathJoin(subDir), recursive: true);
			}
		}
	}

	// Reads the header line rather than loading: loading a world-map document
	// pulls in its whole resource graph to answer a yes/no question.
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

	// --- Selection ------------------------------------------------------

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

	// The two "new" rows mint an unused path so a new document's save has
	// somewhere to go without overwriting a real file. Outside the editor list
	// (autostart) the existing `world_file` cvar decides.
	private string NewEditorDocumentPath()
	{
		if (_mode != SelectorMode.Editor)
		{
			return CVars.worldFile.Value;
		}
		return UnusedDocumentPath(SelectedIndex() == EDITOR_NEW_WORLD_INDEX ? newWorldPath : newScenePath);
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
