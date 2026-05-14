using Godot;
using System;

// Modal merchant screen. Wired into the game scene as a sibling of
// CookingScreen and hidden by default. Open() suppresses gameplay input,
// hides the HUD, and releases the mouse; Close() restores them and fires
// the caller-provided onClose callback. Body is intentionally empty for
// now — buy/sell panels will plug in later.
[GlobalClass]
public partial class MerchantScreen : Control
{
	[Export] public GameClient gameClient;

	Action _onClose;
	Player _player;

	public override void _Ready()
	{
		Visible = false;
	}

	public void Open(Player player, Action onClose = null)
	{
		_player = player;
		_onClose = onClose;
		if (gameClient != null)
		{
			gameClient.InputSuppressed = true;
			if (gameClient.hud != null)
			{
				gameClient.hud.Visible = false;
			}
		}
		Input.MouseMode = Input.MouseModeEnum.Visible;
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
			GameClient client = gameClient;
			Callable.From(() => client.InputSuppressed = false).CallDeferred();
			if (gameClient.hud != null)
			{
				gameClient.hud.Visible = true;
			}
		}
		Input.MouseMode = Input.MouseModeEnum.Captured;
		Action cb = _onClose;
		_onClose = null;
		_player = null;
		cb?.Invoke();
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
	}
}
