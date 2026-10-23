using System.Collections.Generic;
using Godot;

// Side-panel on the inventory screen that displays the highlighted item's
// name, icon, and description. Hidden outright when nothing is highlighted —
// InventoryScreen routes focus changes here via InventoryPanel.onFocusedItemChanged.
[GlobalClass]
public partial class ItemInfoPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	[Export] private Label _descriptionLabel;
	[Export] private TextureRect _icon;
	// Gear's remaining durability; hidden for anything that never wears.
	[Export] private ProgressBar _durabilityBar;
	[Export] private Label _durabilityLabel;
	[Export] private PackedScene _actionPanel;
	[Export] private Control _actionPanelContainer;
	// Titled sub-panel (header + stat rows) for the weapon's Parry counter-strike,
	// the same widget the per-action Crit / Backstab contexts use.
	[Export] private PackedScene _contextPanelScene;
	[Export] private Control _statContainer;
	[Export] private PackedScene _statScene;
	[Export] private Control _statusContainer;
	[Export] private PackedScene _statusScene;
	// When false, action panels list only the attack names — the per-attack
	// stat rows (damage, range, cost) are suppressed. The party screen uses
	// this for a compact at-a-glance readout.
	[Export] private bool _showDetails = true;
	[Export] Control _reagentsRoot;
	[Export] Godot.Collections.Array<ItemSlotPanel> _reagentSlots;
	// How long the requirement takes at the bench, beside the reagents.
	[Export] Label _timeLabel;

	// forceIdentified overrides the world's identification gate so the item's
	// stats/description always show — the forge picker uses it so a freshly
	// minted piece reads in full before the player commits to it.
	//
	// reagents, when supplied, are the materials shown in the "Required Reagents"
	// row — a recipe's `inputs` for a preview, or what a repair costs. Left null,
	// the row hides. hours, when not negative, is the in-world time the bench
	// takes, shown with them.
	public void SetItem(ItemState item, bool forceIdentified = false, IReadOnlyList<MaterialCount> reagents = null, double hours = -1.0)
	{
		ItemData data = item?.data;
		if (data == null)
		{
			Visible = false;
			return;
		}
		SimState worldSim = Sim.Current?.WorldState?.SimState;
		bool identified = forceIdentified || worldSim == null || worldSim.IsItemIdentified(data);
		if (_nameLabel != null)
		{
			_nameLabel.Text = worldSim != null
				? worldSim.GetItemDisplayName(item)
				: WeaponNameGenerator.Compose(data.displayName.ToString(), item);
		}
		if (_descriptionLabel != null)
		{
			// Hide flavor text while the item is unidentified — revealing
			// the recipe of a "?" potion via the info panel would defeat
			// the reveal-on-use design.
			string flavor = data.description ?? string.Empty;
			_descriptionLabel.Text = identified ? flavor : string.Empty;
		}
		if (_icon != null)
		{
			_icon.Texture = data.inventorySprite;
		}
		UpdateDurability(item, identified);
		RebuildItemStats(item, identified);
		RebuildStatusEffects(item);
		RebuildActionPanels(item, identified);
		RebuildReagents(reagents, hours);
		if (_timeLabel != null)
		{
			_timeLabel.Visible = hours >= 0.0;
			_timeLabel.Text = hours >= 0.0 ? Loc.Format(Loc.Keys.craft_time, FormatHours(hours)) : string.Empty;
		}
		Visible = true;
	}

	// In-world hours as "2h 30m", "45m" or "3h", rounded to the minute.
	public static string FormatHours(double hours)
	{
		int minutes = (int)System.Math.Round(hours * 60.0);
		int h = minutes / 60;
		int m = minutes % 60;
		if (h == 0)
		{
			return Loc.Format(Loc.Keys.time_minutes, m);
		}
		return m == 0 ? Loc.Format(Loc.Keys.time_hours, h) : Loc.Format(Loc.Keys.time_hours_minutes, h, m);
	}

	// Fill the "Required Reagents" slots — one authored ItemSlotPanel per entry at its
	// required count; extra slots are cleared. The whole row (_reagentsRoot) hides
	// when there are no reagents to show.
	private void RebuildReagents(IReadOnlyList<MaterialCount> reagents, double hours)
	{
		if (_reagentsRoot != null)
		{
			_reagentsRoot.Visible = (reagents != null && reagents.Count > 0) || hours >= 0.0;
		}
		if (_reagentSlots == null)
		{
			return;
		}
		for (int i = 0; i < _reagentSlots.Count; i++)
		{
			ItemSlotPanel slot = _reagentSlots[i];
			if (slot == null)
			{
				continue;
			}
			ItemState reagent = null;
			if (reagents != null && i < reagents.Count)
			{
				MaterialCount ri = reagents[i];
				if (ri?.item != null && ri.count > 0)
				{
					reagent = ri.item.CreateState();
					reagent.SetCount(ri.count);
				}
			}
			slot.SetItem(reagent);
		}
	}

	// One StatusEffectInfoPanel per armed effect on the item. Reuses the
	// same panel PlayerStatsPanel uses for the player's status readout —
	// icon + name + bars (via embedded StatusEffectHud) + per-stat rows.
	// Buildup-only meters are skipped here, matching the slot view's policy:
	// this section shows what's *actually* affecting the item, not pre-arm
	// progress still climbing toward its threshold. Hidden / no-op if the
	// container or scene field isn't wired in the authoring .tscn.
	private void RebuildStatusEffects(ItemState item)
	{
		if (_statusContainer == null)
		{
			return;
		}
		foreach (Node child in _statusContainer.GetChildren())
		{
			if (child is StatusEffectInfoPanel existing)
			{
				existing.QueueFree();
			}
		}
		if (item == null || _statusScene == null)
		{
			return;
		}
		var effects = item.statusEffects.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectState state = effects[i];
			StatusEffectData data = state?.data;
			if (data == null)
			{
				continue;
			}
			StatusEffectInfoPanel panel = _statusScene.Instantiate<StatusEffectInfoPanel>();
			_statusContainer.AddChild(panel);
			// ContinuousArm effects (wet) — the meter IS the progress; bar
			// fills 0..1 with the intensity. ThresholdCross effects on items
			// (future timed enchants) — feed the remaining-time fraction.
			float progress;
			bool hasTimer;
			float buildup;
			if (data.buildupBehavior == EBuildupBehavior.ContinuousArm)
			{
				progress = Mathf.Clamp(item.statusEffects.GetBuildup(data), 0f, 1f);
				hasTimer = true;
				buildup = 0f;
			}
			else if (state.ShowsCountdownBar)
			{
				ulong now = Sim.Current?.GameTimeMs ?? 0;
						progress = state.RemainingProgress(now);
				hasTimer = true;
				buildup = 0f;
			}
			else
			{
				progress = 0f;
				hasTimer = false;
				buildup = 0f;
			}
			panel.SetStatusEffect(data, 1, progress, hasTimer, buildup);
		}
	}

	// Uses left of the max, rounded up so a piece reads 0 only once it is broken.
	private void UpdateDurability(ItemState item, bool identified)
	{
		if (_durabilityBar == null)
		{
			return;
		}
		if (!identified || item is not GearState gear || !gear.Wears)
		{
			_durabilityBar.Visible = false;
			return;
		}
		float max = gear.data.maxDurability;
		_durabilityBar.MinValue = 0;
		_durabilityBar.MaxValue = 1;
		_durabilityBar.Value = Mathf.Clamp(gear.durability / max, 0f, 1f);
		if (_durabilityLabel != null)
		{
			_durabilityLabel.Text = $"{GameClient.Current.statNames[EStatName.Durability]} {Mathf.CeilToInt(gear.durability)} / {max}";
		}
		_durabilityBar.Visible = true;
	}

	// Item-level stats live above the per-action panels. StatList picks
	// the appropriate generator per item type; each generator suppresses
	// neutral-value rows so a plain item reads as just its name + icon.
	private void RebuildItemStats(ItemState item, bool identified)
	{
		ClearStats();
		if (!identified)
		{
			return;
		}
		AddStats(StatList.Weight(item.data));
		AddStats(StatList.Food(item.data));
		AddStats(StatList.Spoilage(item));
		AddStats(StatList.Contents(item));
		AddStats(StatList.Ammo(item.data));
		AddStats(StatList.BreakChance(item.data));
		switch (item)
		{
			case ShieldState shield:
				AddStats(StatList.ShieldStats(shield.data));
				break;
			case ArmorState armor:
				AddStats(StatList.ArmorStats(armor));
				break;
			case LanternState:
				if (item.data is IUsableItem usable)
				{
					AddStats(StatList.ConsumableStats(usable.ActionProfile));
				}
				break;
		}
	}

	private void ClearStats()
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

	private void AddStats(IEnumerable<(string name, string value)> entries)
	{
		if (_statContainer == null || _statScene == null)
		{
			return;
		}
		foreach (var (name, value) in entries)
		{
			StatPanel stat = _statScene.Instantiate<StatPanel>();
			_statContainer.AddChild(stat);
			stat.SetText(name, value);
		}
	}

	// One ItemActionPanel per charged action. Cleared and rebuilt each
	// time the highlighted item changes; hidden entirely when the item
	// is unidentified (same reveal-on-use rule as the description text).
	private void RebuildActionPanels(ItemState item, bool identified)
	{
		if (_actionPanelContainer == null)
		{
			return;
		}
		foreach (Node child in _actionPanelContainer.GetChildren())
		{
			if (child is ItemActionPanel || child is ItemActionContextPanel)
			{
				child.QueueFree();
			}
		}
		if (!identified || _actionPanel == null)
		{
			return;
		}
		if (item is ShieldState shield)
		{
			BuildBlockCounterContext(shield.data);
			BuildParryContext(shield.data);
			return;
		}
		if (item is not WeaponState weapon)
		{
			return;
		}
		WeaponData data = weapon.data;
		ItemActionProfile profile = data?.actionProfile;
		if (profile?.chargedActions == null)
		{
			return;
		}
		for (int i = 0; i < profile.chargedActions.Count; i++)
		{
			ItemAction action = profile.chargedActions[i];
			if (action == null)
			{
				continue;
			}
			ItemActionPanel panel = _actionPanel.Instantiate<ItemActionPanel>();
			_actionPanelContainer.AddChild(panel);
			panel.SetAction(action, data, i, _showDetails);
		}
	}

	// The shield's Parry counter-strike, shown as its own titled context panel
	// (like the per-action Crit / Backstab contexts) rather than a flat stat row —
	// its riders (armor pen, knockback, Dizzy) only apply on a successful parry.
	// No-op when the shield can't parry or the context widget isn't wired.
	private void BuildParryContext(ShieldData shield)
	{
		DamageData counter = StatList.ParryCounter(shield);
		if (counter == null)
		{
			return;
		}
		ItemActionContextPanel.Populate(_contextPanelScene, _actionPanelContainer,
			GameClient.Current.statNames[EStatName.Parry], StatList.BaseDamage(counter));
	}

	// The shield's Thorns (damage dealt back on every block), as its own context
	// panel beside Parry.
	private void BuildBlockCounterContext(ShieldData shield)
	{
		DamageData counter = StatList.BlockCounter(shield);
		if (counter == null)
		{
			return;
		}
		ItemActionContextPanel.Populate(_contextPanelScene, _actionPanelContainer,
			GameClient.Current.statNames[EStatName.Thorns], StatList.BaseDamage(counter));
	}

}
