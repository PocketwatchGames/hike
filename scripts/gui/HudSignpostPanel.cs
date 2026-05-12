using Godot;
using System;

// HUD panel that appears when the player interacts with a Signpost. Opened
// via Hud.ShowSignpost(text); dismissed when the player presses Interact
// again — GameClient consumes that press before the player processes input,
// so the close press doesn't also trigger a fresh interaction.
[GlobalClass]
public partial class HudSignpostPanel : Control
{
	[Export] public Label label;
	[Export] public GameClient gameClient;

	public bool IsOpen => Visible;

	Action _onClose;

	public override void _Ready()
	{
		Visible = false;
	}

	public void Open(string text, Action onClose = null)
	{
		if (label != null)
		{
			label.Text = text ?? string.Empty;
		}
		_onClose = onClose;
		if (gameClient != null)
		{
			gameClient.InputSuppressed = true;
		}
		Visible = true;
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		if (gameClient != null)
		{
			gameClient.InputSuppressed = false;
		}
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}
}
