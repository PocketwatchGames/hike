using Godot;

// HUD panel that appears when the player interacts with a Signpost. Shown
// via Hud.ShowSignpost(text); dismissed when the player presses Interact
// again — GameClient consumes that press before the player processes input,
// so the close press doesn't also trigger a fresh interaction.
[GlobalClass]
public partial class HudSignpostPanel : Control
{
	[Export] public Label label;

	public bool IsOpen => Visible;

	public override void _Ready()
	{
		Visible = false;
	}

	public void Show(string text)
	{
		if (label != null)
		{
			label.Text = text ?? string.Empty;
		}
		Visible = true;
	}

	public void Close()
	{
		Visible = false;
	}
}
