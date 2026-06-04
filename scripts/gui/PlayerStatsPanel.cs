using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class PlayerStatsPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	[Export] private Label _descriptionLabel;
	[Export] private Control _statContainer;
	[Export] private PackedScene _statScene;
	[Export] private PackedScene _statusEffectInfoScene;
	[Export] private Control _statusEffectContainer;

	Player _player;

	// Per-data accounting reused each refresh so timer countdowns don't
	// churn the GC. _panels is the live tracking dict — one entry per
	// distinct StatusEffectData currently held by the player. Panels are
	// instantiated only when a new effect appears and freed when the last
	// instance of that data expires; the per-frame path just pushes the
	// fresh count + progress to the existing widgets.
	readonly Dictionary<StatusEffectData, int> _counts = new();
	readonly Dictionary<StatusEffectData, ulong> _shortestRemainingMs = new();
	readonly Dictionary<StatusEffectData, StatusEffectInfoPanel> _panels = new();
	readonly List<StatusEffectData> _toRemove = new();

	// Per-frame stat snapshot. Built fresh into _currentStats, compared
	// positionally to the live StatPanel children — same count + positions
	// means in-place SetText (no instantiate / free churn), shape change
	// means full rebuild.
	readonly List<(string name, string value)> _currentStats = new();

	public void SetPlayer(Player player)
	{
		_player = player;
		if (player == null)
		{
			ClearStatusEffectPanels();
			ClearStatPanels();
			return;
		}
		// Name is fixed for the run, so set it once here rather than every
		// per-frame Refresh.
		if (_nameLabel != null)
		{
			_nameLabel.Text = player.PlayerName;
		}
		Refresh();
	}

	public override void _Process(double delta)
	{
		if (!Visible || _player == null)
		{
			return;
		}
		Refresh();
	}

	void ClearStatusEffectPanels()
	{
		foreach (var kv in _panels)
		{
			kv.Value.QueueFree();
		}
		_panels.Clear();
	}

	void ClearStatPanels()
	{
		if (_statContainer == null)
		{
			return;
		}
		foreach (Node child in _statContainer.GetChildren())
		{
			if (child is StatPanel existing)
			{
				existing.QueueFree();
			}
		}
	}

	void Refresh()
	{
		RefreshStats();
		RefreshStatusEffects();
	}

	// In-place update path. Equipping a piece of armor changes which stats
	// surface (e.g., Cold Resist appears) so the StatPanel count can shift
	// across frames — handle the structure change with a full rebuild, but
	// the common case (same stats, ticking values) just rewrites the text.
	void RefreshStats()
	{
		if (_statContainer == null || _statScene == null)
		{
			return;
		}
		_currentStats.Clear();
		foreach (var entry in StatList.PlayerStats(_player))
		{
			_currentStats.Add(entry);
		}

		Godot.Collections.Array<Node> children = _statContainer.GetChildren();
		int statChildCount = 0;
		for (int i = 0; i < children.Count; i++)
		{
			if (children[i] is StatPanel)
			{
				statChildCount++;
			}
		}

		if (statChildCount != _currentStats.Count)
		{
			ClearStatPanels();
			for (int i = 0; i < _currentStats.Count; i++)
			{
				StatPanel stat = _statScene.Instantiate<StatPanel>();
				_statContainer.AddChild(stat);
				stat.SetText(_currentStats[i].name, _currentStats[i].value);
			}
			return;
		}

		int statIndex = 0;
		for (int i = 0; i < children.Count; i++)
		{
			if (children[i] is StatPanel stat)
			{
				stat.SetText(_currentStats[statIndex].name, _currentStats[statIndex].value);
				statIndex++;
			}
		}
	}

	// Mirrors Hud.UpdateStatusEffects: group player's effects by data,
	// drop panels whose data is no longer held, instantiate panels for
	// newly-appeared data, and refresh the HUD count + timer on the rest.
	void RefreshStatusEffects()
	{
		if (_statusEffectContainer == null || _statusEffectInfoScene == null)
		{
			return;
		}

		_counts.Clear();
		_shortestRemainingMs.Clear();
		ulong now = World.Current?.GameTimeMs ?? 0;
		IReadOnlyList<StatusEffectState> effects = _player.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectState s = effects[i];
			if (s?.data == null)
			{
				continue;
			}
			_counts.TryGetValue(s.data, out int prev);
			_counts[s.data] = prev + 1;
			if (s.IsTimed)
			{
				ulong remaining = s.RemainingMs(now);
				if (!_shortestRemainingMs.TryGetValue(s.data, out ulong prevR) || remaining < prevR)
				{
					_shortestRemainingMs[s.data] = remaining;
				}
			}
		}

		// Drop panels whose data is no longer in the player's list.
		_toRemove.Clear();
		foreach (var kv in _panels)
		{
			if (!_counts.ContainsKey(kv.Key))
			{
				kv.Value.QueueFree();
				_toRemove.Add(kv.Key);
			}
		}
		for (int i = 0; i < _toRemove.Count; i++)
		{
			_panels.Remove(_toRemove[i]);
		}

		// Add / refresh panels for currently-held effects.
		foreach (var kv in _counts)
		{
			StatusEffectData data = kv.Key;
			int count = kv.Value;
			bool hasTimer = _shortestRemainingMs.TryGetValue(data, out ulong remaining);
			float progress = 0f;
			if (hasTimer && data.duration > 0f)
			{
				progress = remaining / (data.duration * 1000f);
			}
			// Continuous-state effects (currently wet) override the timer-
			// based progress with a player-side value.
			float? custom = _player.GetStatusEffectProgress(data);
			if (custom.HasValue)
			{
				progress = custom.Value;
				hasTimer = true;
			}
			if (!_panels.TryGetValue(data, out StatusEffectInfoPanel panel))
			{
				panel = _statusEffectInfoScene.Instantiate<StatusEffectInfoPanel>();
				_statusEffectContainer.AddChild(panel);
				_panels[data] = panel;
				panel.SetStatusEffect(data, count, progress, hasTimer);
			}
			else
			{
				panel.RefreshHud(count, progress, hasTimer);
			}
		}
	}
}
