using Godot;

// One line of the controls help table. Also used for the header, which just
// gets the column titles instead of a label and two glyphs.
[GlobalClass]
public partial class ControlsRow : HBoxContainer
{
	[Export] Label _label;
	[Export] Label _keyboard;
	[Export] Label _gamepad;

	public void Fill(string label, string keyboard, string gamepad)
	{
		if (_label != null)
		{
			_label.Text = label;
		}
		if (_keyboard != null)
		{
			_keyboard.Text = keyboard;
		}
		if (_gamepad != null)
		{
			_gamepad.Text = gamepad;
		}
	}
}
