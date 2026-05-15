using Godot;
using Godot.Collections;

// Bestiary tab rendered inside AlmanacScreen. Lists every mob type the
// player has discovered this run (WorldSimState.DiscoveredMobs) — one row
// per discovered mob species. Row ordering tracks SimData.Mobs so authors
// control the list shape rather than the order matching the order things
// were encountered in this run. Focusing a row populates the right-hand
// detail panel with that mob's information (name, level, kill progress).
//
// View only — mobs are discovered by perceiving them past
// MobData.discoveredThreshold (see MobAI and Mob.Yell), and kills are
// counted in WorldSimState.RecordMobKill on each player-credited death.
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
	[Export] Label _mobLevelLabel;
	[Export] ProgressBar _mobKillProgressBar;
	[Export] Label _mobKillProgressLabel;
	[Export] Label _noMobsLabel;

	// One-shot focus hint set by AlmanacScreen.Open when the caller wants
	// a specific row preselected (announcement shortcut). Consumed and
	// cleared on the next Rebuild so subsequent opens (no focus arg) fall
	// back to the first discovered row.
	MobData _pendingFocusMob;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public void SetPendingFocus(MobData mob)
	{
		_pendingFocusMob = mob;
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
		Button focusButton = null;
		MobData focusMob = null;
		MobData pending = _pendingFocusMob;
		_pendingFocusMob = null;
		if (simData != null && worldSim != null && simData.Mobs != null)
		{
			for (int i = 0; i < simData.Mobs.Count; i++)
			{
				MobData mob = simData.Mobs[i];
				if (mob == null || !worldSim.DiscoveredMobs.ContainsKey(mob))
				{
					continue;
				}
				Button b = CreateMobButton(mob);
				if (firstButton == null) { firstButton = b; firstMob = mob; }
				if (pending != null && mob == pending) { focusButton = b; focusMob = mob; }
			}
		}

		Button targetButton = focusButton ?? firstButton;
		MobData targetMob = focusButton != null ? focusMob : firstMob;
		bool any = targetButton != null;
		if (_noMobsLabel != null)
		{
			_noMobsLabel.Visible = !any;
		}
		if (any)
		{
			// Populate the right-hand detail synchronously so the panel shows
			// the chosen entry immediately. The deferred GrabFocus moves
			// keyboard focus onto the button at end-of-frame; relying on its
			// FocusEntered signal to fill the panel would leave a blank state
			// in the meantime.
			ShowMobDetail(targetMob);
			targetButton.CallDeferred(Control.MethodName.GrabFocus);
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
		if (_mobPortrait != null)
		{
			Texture2D portrait = mob?.bestiaryPortrait;
			_mobPortrait.Texture = portrait;
			_mobPortrait.Visible = portrait != null;
		}
		UpdateLevelProgress(mob);
	}

	// Compute the level + progress-bar fill from the per-species kill count
	// against MobData.killsPerLevel. Empty thresholds = the entry doesn't
	// level (stays at level 0, bar tracks total kills with no target).
	// At max level the bar is full and the label collapses to total kills.
	void UpdateLevelProgress(MobData mob)
	{
		WorldSimState worldSim = _gameClient?.World?.WorldState?.SimState;
		int kills = 0;
		if (mob != null && worldSim != null
			&& worldSim.DiscoveredMobs.TryGetValue(mob, out MobBestiaryEntry entry))
		{
			kills = entry.Kills;
		}

		Array<int> thresholds = mob?.killsPerLevel;
		int level = MobBestiaryEntry.ComputeLevel(kills, thresholds);
		int prevThreshold = 0;
		int nextThreshold = 0;
		bool atMax;
		if (thresholds == null || thresholds.Count == 0)
		{
			// Unleveled species — bar shows the running kill total with no
			// target. Treated as "at max" so the bar fills (there's no next
			// rank to chase) and the label shows just the number.
			atMax = true;
		}
		else
		{
			atMax = level >= thresholds.Count;
			if (level > 0)
			{
				prevThreshold = thresholds[level - 1];
			}
			if (!atMax)
			{
				nextThreshold = thresholds[level];
			}
		}

		if (_mobLevelLabel != null)
		{
			_mobLevelLabel.Text = mob != null ? $"Level: {level}" : string.Empty;
		}
		if (_mobKillProgressBar != null)
		{
			_mobKillProgressBar.MinValue = 0;
			if (atMax)
			{
				_mobKillProgressBar.MaxValue = 1;
				_mobKillProgressBar.Value = 1;
			}
			else
			{
				// Bar spans this level's range only — fills 0 → 1 between
				// the previous threshold and the next, so each level-up
				// resets the visible bar instead of inching toward the
				// final tier across the whole entry's lifetime.
				int span = Mathf.Max(1, nextThreshold - prevThreshold);
				_mobKillProgressBar.MaxValue = span;
				_mobKillProgressBar.Value = kills - prevThreshold;
			}
		}
		if (_mobKillProgressLabel != null)
		{
			if (mob == null)
			{
				_mobKillProgressLabel.Text = string.Empty;
			}
			else if (atMax)
			{
				_mobKillProgressLabel.Text = kills.ToString();
			}
			else
			{
				_mobKillProgressLabel.Text = $"{kills}/{nextThreshold}";
			}
		}
	}
}
