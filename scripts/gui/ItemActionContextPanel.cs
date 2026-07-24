using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class ItemActionContextPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	[Export] private Control _statContainer;
	[Export] private PackedScene _statScene;

	// Instantiate a context panel from `scene`, parent it under `parent`, and
	// fill it with a header + stat rows. The single seam every titled context
	// shares — the per-action Crit / Backstab layers and the weapon-level Parry
	// counter alike. Each caller just supplies a header string and its (name,
	// value) rows; where the rows come from (a DamageDataModifier, a parry
	// DamageData) stays the caller's business.
	public static ItemActionContextPanel Populate(PackedScene scene, Control parent, string header, IEnumerable<(string name, string value)> stats)
	{
		if (scene == null || parent == null)
		{
			return null;
		}
		ItemActionContextPanel panel = scene.Instantiate<ItemActionContextPanel>();
		parent.AddChild(panel);
		panel.SetHeader(header);
		panel.ClearStats();
		foreach (var (name, value) in stats)
		{
			panel.AddStat(name, value);
		}
		return panel;
	}

	public void SetHeader(string name)
	{
		if (_nameLabel != null)
		{
			_nameLabel.Text = name ?? string.Empty;
			_nameLabel.Visible = !string.IsNullOrEmpty(name);
		}
	}

	public void ClearStats()
	{
		if (_statContainer == null)
		{
			return;
		}
		foreach (Node child in _statContainer.GetChildren())
		{
			if (child is StatPanel)
			{
				child.QueueFree();
			}
		}
	}

	public void AddStat(string label, string value)
	{
		if (_statContainer == null || _statScene == null)
		{
			return;
		}
		StatPanel stat = _statScene.Instantiate<StatPanel>();
		_statContainer.AddChild(stat);
		stat.SetText(label, value);
	}
}
