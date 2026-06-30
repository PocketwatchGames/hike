using Godot;
using System.Collections.Generic;

// Bestiary tab rendered inside AlmanacScreen. One PAGE per discovered mob type
// (SimData.Mobs order controls page order): the left list is type buttons, and
// selecting one fills the right panel with the type's name/portrait plus one
// BestiarySpeciesPanel per discovered species of that type (grouped from
// WorldSimState.DiscoveredSpecies by SpeciesData.mob). A type only gets a page
// once at least one of its species is discovered.
//
// View only — species are discovered by perceiving a mob past
// MobData.discoveredThreshold (see MobAI and Mob.Discover), and kills are
// counted in WorldSimState.RecordSpeciesKill on each player-credited death.
// The Almanac wrapper owns InputSuppressed / hud-visibility / ui_cancel
// handling; this screen just rebuilds when its tab is shown.
[GlobalClass]
public partial class BestiaryScreen : Control
{
	GameClient _gameClient;
	[Export] PackedScene _mobButtonScene;
	[Export] Control _mobListContainer;
	[Export] Control _mobPanel;
	[Export] Label _mobNameLabel;
	[Export] TextureRect _mobPortrait;
	[Export] Label _noMobsLabel;
	[Export] PackedScene _mobSpeciesScene;
	[Export] Control _mobStatsContainer;
	[Export] PackedScene _statScene;
	[Export] Control _mobSpeciesContainer;

	// One-shot focus hint set by AlmanacScreen.Open when the caller wants a
	// specific species' page preselected (announcement shortcut). Consumed and
	// cleared on the next Rebuild so subsequent opens (no focus arg) fall back
	// to the first discovered page.
	SpeciesData _pendingFocusSpecies;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public void SetPendingFocus(SpeciesData species)
	{
		_pendingFocusSpecies = species;
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		ShowPageDetail(null);
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			Rebuild();
		}
	}

	// Walk SimData.Mobs (page order), keep only the types with at least one
	// discovered species, and stamp out one button per page. The container also
	// owns the "No Creatures Discovered!" label as a sibling child — we only
	// free Button-typed children so the label survives.
	void Rebuild()
	{
		if (_mobListContainer == null)
		{
			return;
		}
		foreach (Node child in _mobListContainer.GetChildren())
		{
			if (child is Button)
			{
				child.QueueFree();
			}
		}

		SimData simData = _gameClient?.World?.SimData;
		WorldSimState worldSim = _gameClient?.World?.WorldState?.SimState;
		Button firstButton = null;
		MobData firstType = null;
		Button focusButton = null;
		MobData focusType = null;
		MobData pendingType = _pendingFocusSpecies?.mob;
		_pendingFocusSpecies = null;
		if (simData != null && worldSim != null && simData.mobs != null)
		{
			for (int i = 0; i < simData.mobs.Count; i++)
			{
				MobData type = simData.mobs[i];
				if (type == null || !TypeHasDiscoveredSpecies(worldSim, type))
				{
					continue;
				}
				Button b = CreatePageButton(type);
				if (firstButton == null) { firstButton = b; firstType = type; }
				if (pendingType != null && type == pendingType) { focusButton = b; focusType = type; }
			}
		}

		Button targetButton = focusButton ?? firstButton;
		MobData targetType = focusButton != null ? focusType : firstType;
		bool any = targetButton != null;
		if (_noMobsLabel != null)
		{
			_noMobsLabel.Visible = !any;
		}
		if (any)
		{
			// Populate the right-hand detail synchronously so the panel shows the
			// chosen page immediately. The deferred GrabFocus moves keyboard
			// focus onto the button at end-of-frame; relying on its FocusEntered
			// signal to fill the panel would leave a blank state in the meantime.
			ShowPageDetail(targetType);
			targetButton.CallDeferred(Control.MethodName.GrabFocus);
		}
		else
		{
			ShowPageDetail(null);
		}
	}

	Button CreatePageButton(MobData type)
	{
		if (_mobButtonScene == null || _mobListContainer == null)
		{
			return null;
		}
		Button button = _mobButtonScene.Instantiate<Button>();
		if (button == null)
		{
			return null;
		}
		button.Text = type.displayName.ToString();
		button.Icon = null;
		MobData captured = type;
		button.FocusEntered += () => ShowPageDetail(captured);
		// Mouse hover grabs focus so the right-hand info view tracks the cursor
		// the same way D-pad navigation does.
		button.MouseEntered += button.GrabFocus;
		_mobListContainer.AddChild(button);
		return button;
	}

	// Bind the right-hand page to a single mob type: its name + portrait, then a
	// BestiarySpeciesPanel for each of its discovered species. type = null clears
	// everything (used at construction and when no species are discovered).
	void ShowPageDetail(MobData type)
	{
		if (_mobPanel != null)
		{
			_mobPanel.Visible = type != null;
		}
		if (_mobNameLabel != null)
		{
			_mobNameLabel.Text = type != null ? type.displayName.ToString() : string.Empty;
		}
		if (_mobPortrait != null)
		{
			Texture2D portrait = type?.bestiaryPortrait;
			_mobPortrait.Texture = portrait;
			_mobPortrait.Visible = portrait != null;
		}
		RebuildStatPanels(type);
		RebuildSpeciesPanels(type);
	}

	// Fill the stats container with one StatPanel per base-species stat (health,
	// armor, speed, vision) — the bestiary mirror of PlayerStatsPanel. type =
	// null just clears the existing panels.
	void RebuildStatPanels(MobData type)
	{
		if (_mobStatsContainer == null)
		{
			return;
		}
		foreach (Node child in _mobStatsContainer.GetChildren())
		{
			if (child is StatPanel)
			{
				child.QueueFree();
			}
		}
		if (type == null || _statScene == null)
		{
			return;
		}
		foreach ((string name, string value) in StatList.MobStats(type))
		{
			StatPanel stat = _statScene.Instantiate<StatPanel>();
			_mobStatsContainer.AddChild(stat);
			stat.SetText(name, value);
		}
	}

	void RebuildSpeciesPanels(MobData type)
	{
		if (_mobSpeciesContainer == null)
		{
			return;
		}
		foreach (Node child in _mobSpeciesContainer.GetChildren())
		{
			if (child is BestiarySpeciesPanel)
			{
				child.QueueFree();
			}
		}
		WorldSimState worldSim = _gameClient?.World?.WorldState?.SimState;
		if (type == null || worldSim == null || _mobSpeciesScene == null)
		{
			return;
		}
		foreach (SpeciesData species in DiscoveredSpeciesForType(worldSim, type))
		{
			worldSim.DiscoveredSpecies.TryGetValue(species, out MobBestiaryEntry entry);
			BestiarySpeciesPanel panel = _mobSpeciesScene.Instantiate<BestiarySpeciesPanel>();
			_mobSpeciesContainer.AddChild(panel);
			panel.Populate(species, entry);
		}
	}

	static bool TypeHasDiscoveredSpecies(WorldSimState worldSim, MobData type)
	{
		foreach (KeyValuePair<SpeciesData, MobBestiaryEntry> kvp in worldSim.DiscoveredSpecies)
		{
			if (kvp.Key?.mob == type)
			{
				return true;
			}
		}
		return false;
	}

	// Discovered species of one type, ordered by display label so rows are
	// stable across opens rather than tracking dictionary iteration order.
	static List<SpeciesData> DiscoveredSpeciesForType(WorldSimState worldSim, MobData type)
	{
		var list = new List<SpeciesData>();
		foreach (KeyValuePair<SpeciesData, MobBestiaryEntry> kvp in worldSim.DiscoveredSpecies)
		{
			if (kvp.Key?.mob == type)
			{
				list.Add(kvp.Key);
			}
		}
		list.Sort((a, b) => string.Compare(Label(a), Label(b), System.StringComparison.OrdinalIgnoreCase));
		return list;
	}

	static string Label(SpeciesData species)
	{
		if (species == null) { return string.Empty; }
		string name = species.displayName?.ToString();
		return string.IsNullOrEmpty(name) ? species.mob?.displayName.ToString() ?? string.Empty : name;
	}
}
