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

	// Per-effect accounting reused each refresh so timer countdowns don't churn
	// the GC. Panels are keyed by (data, applied upgrade slot), NOT data alone: a
	// forge upgrade applied to two different weapon slots (the same Venomous .tres
	// on both Melee and Ranged) is two independent upgrades, not a stack, so each
	// gets its own row instead of one row with a ×2 count. Ordinary effects carry
	// slot None, so they still group by data and stack normally. Panels are
	// instantiated only when a new key appears and freed when its last instance
	// expires; the per-frame path just pushes the fresh count + progress.
	readonly Dictionary<(StatusEffectData data, EUpgradeSlot slot), int> _counts = new();
	// Smallest remaining-lifetime fraction [0,1] across instances of each key
	// (the one closest to expiring) — drives the panel's timer bar.
	readonly Dictionary<(StatusEffectData data, EUpgradeSlot slot), float> _minProgress = new();
	// Upgrade tier per key, captured during the scan so a newly-created panel can
	// show the level pips + level-scaling rows. Only meaningful for upgrade keys
	// (slot != None); the level is immutable per instance, so set-once at creation.
	readonly Dictionary<(StatusEffectData data, EUpgradeSlot slot), int> _levels = new();
	readonly Dictionary<(StatusEffectData data, EUpgradeSlot slot), StatusEffectInfoPanel> _panels = new();
	readonly List<(StatusEffectData data, EUpgradeSlot slot)> _toRemove = new();
	// Keys in the order first seen while scanning the player's effects this frame,
	// then partitioned into _displayOrder (upgrades first, then ordinary effects)
	// so the container's child order matches. Both reused to avoid per-frame allocs.
	readonly List<(StatusEffectData data, EUpgradeSlot slot)> _seenOrder = new();
	readonly List<(StatusEffectData data, EUpgradeSlot slot)> _displayOrder = new();

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

	// Group the player's effects by (data, applied slot), drop panels whose key is
	// no longer held, instantiate panels for newly-appeared keys, refresh the HUD
	// count + timer on the rest, and order the container so forge upgrades list
	// first (each individually, by slot) ahead of ordinary status effects.
	void RefreshStatusEffects()
	{
		if (_statusEffectContainer == null || _statusEffectInfoScene == null)
		{
			return;
		}

		_counts.Clear();
		_minProgress.Clear();
		_levels.Clear();
		_seenOrder.Clear();
		ulong now = Sim.Current?.GameTimeMs ?? 0;
		double nowTod = Sim.Current?.TimeOfDayAbsolute ?? 0.0;
		IReadOnlyList<StatusEffectState> effects = _player.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectState s = effects[i];
			if (s?.data == null)
			{
				continue;
			}
			var key = (s.data, s.appliedUpgradeSlot);
			if (!_counts.TryGetValue(key, out int prev))
			{
				_seenOrder.Add(key);
			}
			_counts[key] = prev + 1;
			if (s.appliedUpgradeSlot != EUpgradeSlot.None)
			{
				_levels[key] = s.level;
			}
			if (s.IsTimed)
			{
				float progress = s.RemainingProgress(now, nowTod);
				if (!_minProgress.TryGetValue(key, out float prevProgress) || progress < prevProgress)
				{
					_minProgress[key] = progress;
				}
			}
		}

		// Drop panels whose key is no longer in the player's list.
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

		BuildDisplayOrder();

		// Add / refresh panels for currently-held effects, in display order.
		for (int i = 0; i < _displayOrder.Count; i++)
		{
			var key = _displayOrder[i];
			StatusEffectData data = key.data;
			int count = _counts[key];
			bool hasTimer = _minProgress.TryGetValue(key, out float progress);
			// Continuous-state effects (currently wet) override the timer-
			// based progress with a player-side value.
			float? custom = _player.GetStatusEffectProgress(data);
			if (custom.HasValue)
			{
				progress = custom.Value;
				hasTimer = true;
			}
			if (!_panels.TryGetValue(key, out StatusEffectInfoPanel panel))
			{
				panel = _statusEffectInfoScene.Instantiate<StatusEffectInfoPanel>();
				_statusEffectContainer.AddChild(panel);
				_panels[key] = panel;
				_levels.TryGetValue(key, out int level);
				panel.SetStatusEffect(data, count, progress, hasTimer, 0f, level, key.slot);
			}
			else
			{
				panel.RefreshHud(count, progress, hasTimer);
			}
			// Keep the container's child order aligned with _displayOrder. MoveChild
			// only when out of position, so the steady state (order unchanged) is free.
			if (panel.GetIndex() != i)
			{
				_statusEffectContainer.MoveChild(panel, i);
			}
		}
	}

	// Partition this frame's first-seen keys into display order: forge upgrades
	// first (each an individual row, ordered by applied slot bit — Melee, Ranged,
	// Armor), then ordinary effects in the order first seen. Insertion keeps
	// upgrades slot-sorted and both groups stable frame-to-frame (no reshuffle).
	void BuildDisplayOrder()
	{
		_displayOrder.Clear();
		for (int i = 0; i < _seenOrder.Count; i++)
		{
			var key = _seenOrder[i];
			if (key.slot == EUpgradeSlot.None)
			{
				continue;
			}
			int j = 0;
			while (j < _displayOrder.Count && (int)_displayOrder[j].slot <= (int)key.slot)
			{
				j++;
			}
			_displayOrder.Insert(j, key);
		}
		for (int i = 0; i < _seenOrder.Count; i++)
		{
			if (_seenOrder[i].slot == EUpgradeSlot.None)
			{
				_displayOrder.Add(_seenOrder[i]);
			}
		}
	}
}
