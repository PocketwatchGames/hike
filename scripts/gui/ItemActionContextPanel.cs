using Godot;

[GlobalClass]
public partial class ItemActionContextPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	[Export] private Control _statContainer;
	[Export] private PackedScene _statScene;

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
