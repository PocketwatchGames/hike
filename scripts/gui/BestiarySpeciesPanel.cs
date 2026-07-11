using Godot;
using System.Collections.Generic;

// One row on a bestiary page: a single discovered species variant. Shows its
// name, portrait, the stat deltas that distinguish it — its own
// SpeciesData.modifiers plus the modifiers carried by its permanent
// statusEffects — and a compact readout of each weapon in its loadout
// (SpeciesData.weapons), all rendered as (name, value) rows via StatList into
// StatPanels. Instanced by BestiaryScreen, one per discovered species under the
// selected page (grouped by SpeciesData.mob).
[GlobalClass]
public partial class BestiarySpeciesPanel : PanelContainer
{
	[Export] Label _mobNameLabel;
	[Export] TextureRect _mobPortrait;
	[Export] PackedScene _statPanelScene;
	[Export] Control _statPanelContainer;

	// Bind this row to one discovered species. The base type (Species.mob)
	// supplies the page portrait fallback.
	public void Populate(SpeciesData species)
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

		RebuildStats(species);
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
