using Godot;

public partial class StatPanel : PanelContainer
{
	[Export] private Label _nameLabel;
	[Export] private Label _valueLabel;
	// The styled background wrapping the value label. Hidden when the
	// caller passes an empty value — some stats (status effects with no
	// duration, future name-only badges) only have a label and would look
	// awkward with an empty number-box hanging off the side.
	[Export] private Control _valueContainer;

	public void SetText(string name, string value)
	{
		if (_nameLabel != null)
		{
			_nameLabel.Text = name ?? string.Empty;
		}
		bool hasValue = !string.IsNullOrEmpty(value);
		if (_valueLabel != null)
		{
			_valueLabel.Text = value ?? string.Empty;
		}
		if (_valueContainer != null)
		{
			_valueContainer.Visible = hasValue;
		}
	}
}
