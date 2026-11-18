using Godot;

// One position of a species' rune sequence on its bestiary row: the rune's
// icon once learned, a blank square until then.
[GlobalClass]
public partial class RuneSlot : PanelContainer
{
	[Export] TextureRect _icon;

	public void SetRune(RuneData rune)
	{
		if (_icon == null)
		{
			return;
		}
		_icon.Texture = rune?.icon;
		_icon.Visible = rune?.icon != null;
	}
}
