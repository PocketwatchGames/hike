using Godot;
using Godot.Collections;
using System.Collections.Generic;

// One row on a bestiary page: a single discovered species variant. Shows its
// name, portrait, level + kill progress (per-species kills against the shared
// MobData.killsPerLevel thresholds), the stat deltas that distinguish it —
// its own SpeciesData.modifiers plus the modifiers carried by its permanent
// statusEffects — and a compact readout of each weapon in its loadout
// (SpeciesData.weapons), all rendered as (name, value) rows via StatList into
// StatPanels. Instanced by BestiaryScreen, one per discovered species under the
// selected page (grouped by SpeciesData.mob).
[GlobalClass]
public partial class BestiarySpeciesPanel : PanelContainer
{
	[Export] Label _mobNameLabel;
	[Export] TextureRect _mobPortrait;
	[Export] Label _mobLevelLabel;
	[Export] ProgressBar _mobKillProgressBar;
	[Export] Label _mobKillProgressLabel;
	[Export] PackedScene _statPanelScene;
	[Export] Control _statPanelContainer;

	// Bind this row to one discovered species and its progress entry. The base
	// type (Species.mob) supplies the page portrait / level thresholds fallback.
	public void Populate(SpeciesData species, MobBestiaryEntry entry)
	{
		if (species == null)
		{
			return;
		}
		MobData type = species.mob;

		if (_mobNameLabel != null)
		{
			string name = species.displayName?.ToString();
			_mobNameLabel.Text = string.IsNullOrEmpty(name)
				? type?.displayName.ToString() ?? string.Empty
				: name;
		}
		if (_mobPortrait != null)
		{
			Texture2D portrait = species.portrait ?? type?.bestiaryPortrait;
			_mobPortrait.Texture = portrait;
			_mobPortrait.Visible = portrait != null;
		}

		UpdateLevelProgress(entry?.Kills ?? 0, type?.killsPerLevel);
		RebuildStats(species);
	}

	// Compute the level + progress-bar fill from the per-species kill count
	// against the shared MobData.killsPerLevel. Empty thresholds = the entry
	// doesn't level — the level label and progress bar/label are hidden
	// entirely. At max level the bar is full and the label collapses to total
	// kills.
	void UpdateLevelProgress(int kills, Array<int> thresholds)
	{
		// Species that don't level (no thresholds) hide the whole progression
		// row rather than showing a meaningless full bar.
		bool levels = thresholds != null && thresholds.Count > 0;
		if (_mobLevelLabel != null)
		{
			_mobLevelLabel.Visible = levels;
		}
		if (_mobKillProgressBar != null)
		{
			_mobKillProgressBar.Visible = levels;
		}
		if (_mobKillProgressLabel != null)
		{
			_mobKillProgressLabel.Visible = levels;
		}
		if (!levels)
		{
			return;
		}

		int level = MobBestiaryEntry.ComputeLevel(kills, thresholds);
		int prevThreshold = 0;
		int nextThreshold = 0;
		bool atMax = level >= thresholds.Count;
		if (level > 0)
		{
			prevThreshold = thresholds[level - 1];
		}
		if (!atMax)
		{
			nextThreshold = thresholds[level];
		}

		if (_mobLevelLabel != null)
		{
			_mobLevelLabel.Text = $"Level: {level}";
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
				// Bar spans this level's range only — fills 0 → 1 between the
				// previous threshold and the next, so each level-up resets the
				// visible bar instead of inching toward the final tier across
				// the whole entry's lifetime.
				int span = Mathf.Max(1, nextThreshold - prevThreshold);
				_mobKillProgressBar.MaxValue = span;
				_mobKillProgressBar.Value = kills - prevThreshold;
			}
		}
		if (_mobKillProgressLabel != null)
		{
			_mobKillProgressLabel.Text = atMax ? kills.ToString() : $"{kills}/{nextThreshold}";
		}
	}

	// Stat deltas distinguishing this variant: its raw modifiers, then each
	// permanent status effect's contribution. StatList already renders both as
	// signed/scaled deltas off neutral and suppresses no-op rows, so a plain
	// species with no modifiers and no effects shows an empty stat list.
	void RebuildStats(SpeciesData species)
	{
		if (_statPanelContainer == null)
		{
			return;
		}
		foreach (Node child in _statPanelContainer.GetChildren())
		{
			if (child is StatPanel)
			{
				child.QueueFree();
			}
		}
		if (_statPanelScene == null)
		{
			return;
		}
		AddStats(StatList.Modifiers(species.modifiers));
		if (species.statusEffects != null)
		{
			foreach (StatusEffectData effect in species.statusEffects)
			{
				AddStats(StatList.StatusEffectInfo(effect));
			}
		}
		// Each weapon in the loadout: a name-only header row (StatPanel hides
		// the value box when it's empty) followed by its combat stats. Weapons
		// that don't attack (a pure buff cry) summarize to nothing and are
		// skipped so they leave no orphan header.
		if (species.weapons != null)
		{
			foreach (WeaponData weapon in species.weapons)
			{
				if (weapon == null)
				{
					continue;
				}
				var summary = new List<(string name, string value)>(StatList.WeaponSummary(weapon));
				if (summary.Count == 0)
				{
					continue;
				}
				AddStat(weapon.displayName.ToString(), string.Empty);
				AddStats(summary);
			}
		}
	}

	void AddStats(IEnumerable<(string name, string value)> entries)
	{
		foreach (var (name, value) in entries)
		{
			AddStat(name, value);
		}
	}

	void AddStat(string name, string value)
	{
		StatPanel stat = _statPanelScene.Instantiate<StatPanel>();
		_statPanelContainer.AddChild(stat);
		stat.SetText(name, value);
	}
}
