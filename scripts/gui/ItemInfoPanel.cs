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
	[Export] private ProgressBar _levelProgress;
	[Export] private Label _levelLabel;
	[Export] private PackedScene _actionPanel;
	[Export] private Control _actionPanelContainer;
	[Export] private Control _statContainer;
	[Export] private PackedScene _statScene;

	public void SetItem(ItemState item)
	{
		ItemData data = item?.data;
		if (data == null)
		{
			Visible = false;
			return;
		}
		WorldSimState worldSim = World.Current?.WorldState?.SimState;
		bool identified = worldSim == null || worldSim.IsItemIdentified(data);
		if (_nameLabel != null)
		{
			_nameLabel.Text = worldSim != null
				? worldSim.GetItemDisplayName(data)
				: data.displayName.ToString();
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
		UpdateLevelDisplay(item);
		RebuildItemStats(item, identified);
		RebuildActionPanels(item, identified);
		Visible = true;
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
		switch (item)
		{
			case WeaponState weapon:
				AddStats(StatList.Ammo(weapon));
				break;
			case ArmorState armor:
				AddStats(StatList.ArmorStats(armor));
				break;
			case ConsumableState consumable:
				AddStats(StatList.ConsumableStats(consumable));
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
			if (child is ItemActionPanel existing)
			{
				existing.QueueFree();
			}
		}
		if (!identified || _actionPanel == null)
		{
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
			panel.SetAction(action, data, i);
		}
	}

	private void UpdateLevelDisplay(ItemState item)
	{
		int maxLevel = item.data?.maxLevel ?? 0;
		bool levels = maxLevel > 0;
		if (_levelProgress != null)
		{
			_levelProgress.Visible = levels;
		}
		if (_levelLabel != null)
		{
			_levelLabel.Visible = levels;
		}
		if (!levels)
		{
			return;
		}

		int level;
		int exp;
		switch (item)
		{
			case WeaponState w:
				level = w.level;
				exp = w.exp;
				break;
			case ArmorState a:
				level = a.level;
				exp = a.exp;
				break;
			default:
				level = 0;
				exp = 0;
				break;
		}

		if (_levelLabel != null)
		{
			_levelLabel.Text = $"Level {level + 1}";
		}
		if (_levelProgress != null)
		{
			var thresholds = World.Current?.SimData?.ExpPerLevel;
			int cap = thresholds != null ? System.Math.Min(maxLevel, thresholds.Count) : 0;
			float ratio;
			if (thresholds == null || level >= cap)
			{
				ratio = 1f;
			}
			else
			{
				int prev = level > 0 ? thresholds[level - 1] : 0;
				int next = thresholds[level];
				int span = next - prev;
				ratio = span > 0 ? Mathf.Clamp((exp - prev) / (float)span, 0f, 1f) : 1f;
			}
			_levelProgress.MinValue = 0;
			_levelProgress.MaxValue = 1;
			_levelProgress.Value = ratio;
		}
	}
}
