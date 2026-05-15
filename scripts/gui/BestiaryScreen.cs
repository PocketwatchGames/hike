using Godot;

// Bestiary tab rendered inside AlmanacScreen. Lists every mob type the
// player has discovered this run (WorldSimState.DiscoveredMobs) — one row
// per discovered mob species. Row ordering tracks SimData.Mobs so authors
// control the list shape rather than the order matching the order things
// were encountered in this run. Focusing a row populates the right-hand
// detail panel with that mob's information (starting with the name).
//
// View only — mobs are discovered by perceiving them past
// MobData.discoveredThreshold (see MobAI and Mob.Yell). The Almanac
// wrapper owns InputSuppressed / hud-visibility / ui_cancel handling;
// this screen just rebuilds when its tab is shown.
[GlobalClass]
public partial class BestiaryScreen : Control
{
	GameClient _gameClient;
	[Export] PackedScene _mobButtonScene;
	[Export] Control _mobListContainer;
	[Export] Control _mobPanel;
	[Export] Label _mobNameLabel;
	[Export] Label _noMobsLabel;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		ShowMobDetail(null);
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			Rebuild();
		}
	}

	// Walk SimData.Mobs, keep only the ones the player has discovered, and
	// stamp out one button per mob. The container also owns the "No
	// Creatures Discovered!" label as a sibling child — we only free
	// Button-typed children so the label survives.
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
		MobData firstMob = null;
		if (simData != null && worldSim != null && simData.Mobs != null)
		{
			for (int i = 0; i < simData.Mobs.Count; i++)
			{
				MobData mob = simData.Mobs[i];
				if (mob == null || !worldSim.DiscoveredMobs.Contains(mob))
				{
					continue;
				}
				Button b = CreateMobButton(mob);
				if (firstButton == null) { firstButton = b; firstMob = mob; }
			}
		}

		bool any = firstButton != null;
		if (_noMobsLabel != null)
		{
			_noMobsLabel.Visible = !any;
		}
		if (any)
		{
			// Populate the right-hand detail synchronously so the panel shows
			// the first entry immediately. The deferred GrabFocus moves
			// keyboard focus onto the button at end-of-frame; relying on its
			// FocusEntered signal to fill the panel would leave a blank state
			// in the meantime.
			ShowMobDetail(firstMob);
			firstButton.CallDeferred(Control.MethodName.GrabFocus);
		}
		else
		{
			ShowMobDetail(null);
		}
	}

	Button CreateMobButton(MobData mob)
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
		button.Text = mob.displayName.ToString();
		button.Icon = null;
		MobData captured = mob;
		button.FocusEntered += () => ShowMobDetail(captured);
		// Mouse hover grabs focus so the right-hand info view tracks the
		// cursor the same way D-pad navigation does.
		button.MouseEntered += button.GrabFocus;
		_mobListContainer.AddChild(button);
		return button;
	}

	// Bind the right-hand info panel to a single mob row. mob = null clears
	// everything (used at construction and when no mobs are discovered).
	// Additional fields (description, threat level, drops) can be added to
	// the .tscn and surfaced here without touching the rebuild loop.
	void ShowMobDetail(MobData mob)
	{
		if (_mobPanel != null)
		{
			_mobPanel.Visible = mob != null;
		}
		if (_mobNameLabel != null)
		{
			_mobNameLabel.Text = mob != null ? mob.displayName.ToString() : string.Empty;
		}
	}
}
