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
			// Hide flavor text while the item is unidentified — revealing the
			// recipe of a "?" potion via its description would defeat the
			// reveal-on-use design.
			_descriptionLabel.Text = identified ? (data.description ?? string.Empty) : string.Empty;
		}
		if (_icon != null)
		{
			_icon.Texture = data.inventorySprite;
		}
		UpdateLevelDisplay(item);
		Visible = true;
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
			_levelLabel.Text = (level + 1).ToString();
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
