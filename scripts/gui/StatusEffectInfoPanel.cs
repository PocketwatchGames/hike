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
	// Optional star-pip row for a forge upgrade's tier. Shown only when
	// SetStatusEffect is given a non-zero upgradeLevel; other panels leave it collapsed.
	[Export] private Control _levelStarsContainer;
	[Export] private Godot.Collections.Array<TextureRect> _levelStars = new();
	// Optional multiline description label. Populated from
	// StatusEffectData.description when wired in the scene; left null in
	// scenes that just want the icon + stat rows. Hidden when the effect
	// has no authored description so empty entries don't leave a gap.
	[Export] private Label _descriptionLabel;

	// Convenience overload for static contexts that don't have live state
	// (almanac entries, tooltips). Treats the effect as a single instance
	// with no active timer.
	public void SetStatusEffect(StatusEffectData effect)
	{
		SetStatusEffect(effect, 1, 0f, false);
	}

	public StatusEffectData Data { get; private set; }

	// Light `upgradeLevel` of the star pips and reveal the row (hidden at level 0).
	// Only slotted forge upgrades pass a level; ordinary effects leave it collapsed.
	void UpdateLevelStars(int upgradeLevel)
	{
		if (_levelStarsContainer != null)
		{
			_levelStarsContainer.Visible = upgradeLevel > 0;
		}
		for (int i = 0; i < _levelStars.Count; i++)
		{
			if (_levelStars[i] != null)
			{
				_levelStars[i].Visible = i < upgradeLevel;
			}
		}
	}

	// Per-frame refresh path. Only the embedded HUD's count + timer change
	// over time — the stat row is purely authored data, so we skip the
	// instantiate/free churn and just push the live count/progress.
	// `buildupProgress` defaults to 0 so the inventory's stats screen
	// (which doesn't currently track buildup state) compiles unchanged;
	// callers that have a live meter pass it through to surface the bar.
	public void RefreshHud(int count, float removalProgress, bool hasTimer, float buildupProgress = 0f)
	{
		if (_statusEffectHud != null && Data != null)
		{
			_statusEffectHud.Set(Data, count, removalProgress, hasTimer, buildupProgress);
		}
	}

	// `count` / `removalProgress` / `hasTimer` drive the embedded
	// StatusEffectHud — see Hud.UpdateStatusEffects for the grouping math.
	// `upgradeLevel` / `upgradeSlot` are set only for slotted forge upgrades: they
	// light the tier pips and append the level-derived combat-scaling rows (outgoing
	// damage+buildup for a weapon slot, damage reduction for Armor). Defaults leave
	// the pips hidden and add no extra rows, so ordinary-effect callers are unchanged.
	public void SetStatusEffect(StatusEffectData effect, int count, float removalProgress, bool hasTimer, float buildupProgress = 0f, int upgradeLevel = 0, EUpgradeSlot upgradeSlot = EUpgradeSlot.None)
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
			_statusEffectHud.Set(effect, count, removalProgress, hasTimer, buildupProgress);
		}
		if (_descriptionLabel != null)
		{
			string desc = effect.description ?? string.Empty;
			_descriptionLabel.Text = desc;
			_descriptionLabel.Visible = desc.Length > 0;
		}
		UpdateLevelStars(upgradeLevel);
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
		foreach (var (name, value) in StatList.UpgradeLevelInfo(upgradeLevel, upgradeSlot))
		{
			StatPanel stat = _statPanelScene.Instantiate<StatPanel>();
			_statContainer.AddChild(stat);
			stat.SetText(name, value);
		}
	}
}
