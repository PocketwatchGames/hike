using Godot;
using System;

public partial class GuiMainMenu : Node
{
	[Export] public PackedScene playerScene;
	[Export] public PlayerSpawnData playerSpawnData;
	[Export] public WorldGenData worldGenData;
	[Export] public Label versionLabel;
	[Export] public ItemList worldList;
	// Selectable worldgen templates shown in worldList; labels is the parallel
	// display text (same length as worldOptions).
	[Export] public WorldGenData[] worldOptions = System.Array.Empty<WorldGenData>();
	[Export] public string[] worldOptionLabels = System.Array.Empty<string>();
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
		PopulateWorldList();
	}

	private void PopulateWorldList()
	{
		if (worldList == null)
		{
			return;
		}
		worldList.Clear();
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

	private WorldGenData SelectedWorldGen()
	{
		if (worldList != null && worldOptions.Length > 0)
		{
			int[] selected = worldList.GetSelectedItems();
			if (selected.Length > 0 && selected[0] < worldOptions.Length)
			{
				return worldOptions[selected[0]];
			}
			return worldOptions[0];
		}
		return worldGenData;
	}

	public void NewGameStandard()
	{
		EmitSignal(SignalName.OnNewGame, new Vector3(0,24,0), playerScene, playerSpawnData, SelectedWorldGen());
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
