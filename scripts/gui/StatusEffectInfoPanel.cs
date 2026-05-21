using Godot;

// One full readout for a status effect — icon + name + per-stat row.
// Used by PlayerStatsPanel to show every effect currently on the player.
// Stat formatting is delegated to StatList.StatusEffectInfo so a tooltip
// or debug overlay can render the same per-effect block.
[GlobalClass]
public partial class StatusEffectInfoPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	[Export] private StatusEffectHud _statusEffectHud;
	[Export] private PackedScene _statPanelScene;
	[Export] private Control _statContainer;

	// Convenience overload for static contexts that don't have live state
	// (almanac entries, tooltips). Treats the effect as a single instance
	// with no active timer.
	public void SetStatusEffect(StatusEffectData effect)
	{
		SetStatusEffect(effect, 1, 0f, false);
	}

	public StatusEffectData Data { get; private set; }

	// Per-frame refresh path. Only the embedded HUD's count + timer change
	// over time — the stat row is purely authored data, so we skip the
	// instantiate/free churn and just push the live count/progress.
	public void RefreshHud(int count, float removalProgress, bool hasTimer)
	{
		if (_statusEffectHud != null && Data != null)
		{
			_statusEffectHud.Set(Data, count, removalProgress, hasTimer);
		}
	}

	// `count` / `removalProgress` / `hasTimer` drive the embedded
	// StatusEffectHud — see Hud.UpdateStatusEffects for the grouping math.
	public void SetStatusEffect(StatusEffectData effect, int count, float removalProgress, bool hasTimer)
	{
		if (effect == null)
		{
			return;
		}
		Data = effect;
		if (_nameLabel != null)
		{
			string name = effect.displayName.ToString();
			_nameLabel.Text = string.IsNullOrEmpty(name) ? effect.ResourceName : name;
		}
		if (_statusEffectHud != null)
		{
			_statusEffectHud.Set(effect, count, removalProgress, hasTimer);
		}
		if (_statContainer == null || _statPanelScene == null)
		{
			return;
		}
		// _statContainer is shared with the HUD + name label — only clear
		// previously-spawned StatPanel children so we don't blow away the
		// authored siblings.
		foreach (Node child in _statContainer.GetChildren())
		{
			if (child is StatPanel existing)
			{
				existing.QueueFree();
			}
		}
		foreach (var (name, value) in StatList.StatusEffectInfo(effect))
		{
			StatPanel stat = _statPanelScene.Instantiate<StatPanel>();
			_statContainer.AddChild(stat);
			stat.SetText(name, value);
		}
	}
}
