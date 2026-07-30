using Godot;
using System;
using System.Collections.Generic;

public partial class GuiMainMenu : Node
{
	[Export] public PackedScene playerScene;
	[Export] public WorldGenData worldGenData;
	[Export] public Label versionLabel;
	// The main button column, and the file/world picker that replaces it.
	[Export] public Control buttonPanel;
	[Export] public Control fileSelector;
	[Export] public ItemList worldList;
	// Selectable worldgen templates shown in worldList; labels is the parallel
	// display text (same length as worldOptions).
	[Export] public WorldGenData[] worldOptions = System.Array.Empty<WorldGenData>();
	[Export] public string[] worldOptionLabels = System.Array.Empty<string>();
	// Directories scanned (non-recursively) for editor documents, per kind.
	[Export] public string[] sceneFileSearchDirs = { SubsceneFile.DEFAULT_SCENE_DIR };
	[Export] public string[] worldFileSearchDirs = { "user://", "res://resources/data/worldmap/" };
	// Save target for a brand-new document; uniquified (scene_2.hikescene, ...)
	// when the file already exists so a new one never clobbers an old one. The
	// extension is also what tells the editor which kind it's opening.
	[Export] public string newScenePath = SubsceneFile.DEFAULT_SCENE_DIR + "scene.hikescene";
	[Export] public string newWorldPath = "user://world.hike";
	[Export] public string newSceneLabel = "New Scene";
	[Export] public string newWorldLabel = "New World";
	[Signal] public delegate void OnNewGameEventHandler(Vector3 playerPosition, PackedScene playerScene, WorldGenData worldGenData);
	[Signal] public delegate void OnLoadGameEventHandler(string savePath);
	[Signal] public delegate void OnStartEditorEventHandler(WorldGenData worldGenData, string worldFilePath);
	[Signal] public delegate void OnStartPainterEventHandler(WorldGenData worldGenData);

	private enum SelectorMode
	{
		NewGame,
		Editor,
	}

	// The two fixed rows at the top of the editor list.
	private const int NEW_SCENE_INDEX = 0;
	private const int NEW_WORLD_INDEX = 1;

	private SelectorMode _mode = SelectorMode.NewGame;
	// Editor-mode entries parallel to worldList items; null = one of the two
	// "new document" rows, whose path is minted on Continue.
	private readonly List<string> _editorWorldPaths = new List<string>();

	public override void _Ready()
	{
		Input.MouseMode = Input.MouseModeEnum.Visible;
		if (versionLabel != null)
		{
			versionLabel.Text = Version.Display;
		}
		ShowButtons();
	}

	// --- Button panel ---------------------------------------------------

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

	public void StartPainter()
	{
		EmitSignal(SignalName.OnStartPainter, worldGenData);
	}

	// --- File selector --------------------------------------------------

	public void SelectorBack()
	{
		ShowButtons();
	}

	public void SelectorContinue()
	{
		if (_mode == SelectorMode.Editor)
		{
			StartEditor();
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
		EmitSignal(SignalName.OnNewGame, new Vector3(0, 24, 0), playerScene, SelectedWorldGen());
	}

	public void StartEditor()
	{
		EmitSignal(SignalName.OnStartEditor, worldGenData, SelectedWorldFile());
	}

	// --- Selection ------------------------------------------------------

	private void PopulateWorldGenList()
	{
		if (worldList == null)
		{
			return;
		}
		worldList.Clear();
		_editorWorldPaths.Clear();
		for (int i = 0; i < worldOptions.Length; i++)
		{
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
		if (worldOptions.Length > 0)
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
		_editorWorldPaths.Clear();
		worldList.AddItem(newSceneLabel);
		_editorWorldPaths.Add(null);
		worldList.AddItem(newWorldLabel);
		_editorWorldPaths.Add(null);
		AddDocuments(sceneFileSearchDirs, WorldEditor.SCENE_FILE_EXTENSION);
		AddDocuments(worldFileSearchDirs, WorldEditor.WORLD_FILE_EXTENSION);
		worldList.Select(0);
	}

	private void AddDocuments(string[] searchDirs, string extension)
	{
		foreach (string dir in searchDirs)
		{
			if (string.IsNullOrEmpty(dir))
			{
				continue;
			}
			string osDir = ProjectSettings.GlobalizePath(dir);
			if (!System.IO.Directory.Exists(osDir))
			{
				continue;
			}
			foreach (string osPath in System.IO.Directory.GetFiles(osDir, $"*.{extension}"))
			{
				string fileName = System.IO.Path.GetFileName(osPath);
				worldList.AddItem(fileName);
				// PathJoin, not manual concat: trimming slashes off a bare
				// "user://" leaves "user:", which globalizes to a bogus path.
				_editorWorldPaths.Add(dir.PathJoin(fileName));
			}
		}
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
		int index = SelectedIndex();
		if (_mode == SelectorMode.NewGame && index >= 0 && index < worldOptions.Length)
		{
			return worldOptions[index];
		}
		// Direct launch (autostart) never opens the selector, so honour the
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
		if (index >= 0 && index < _editorWorldPaths.Count && _editorWorldPaths[index] != null)
		{
			return _editorWorldPaths[index];
		}
		return UnusedDocumentPath(index == NEW_WORLD_INDEX ? newWorldPath : newScenePath);
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
