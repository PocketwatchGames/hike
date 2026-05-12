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

	public void SetItem(ItemState item)
	{
		ItemData data = item?.data;
		if (data == null)
		{
			Visible = false;
			return;
		}
		if (_nameLabel != null)
		{
			_nameLabel.Text = data.displayName.ToString();
		}
		if (_descriptionLabel != null)
		{
			_descriptionLabel.Text = data.description ?? string.Empty;
		}
		if (_icon != null)
		{
			_icon.Texture = data.inventorySprite;
		}
		Visible = true;
	}
}
