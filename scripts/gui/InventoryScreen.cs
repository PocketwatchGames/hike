using Godot;
using System;

// Modal wrapper around InventoryPanel. Handles open/close lifecycle, the
// gameClient.InputSuppressed gate so gameplay input stops while the screen is
// up, and the ui_cancel close. All slot / verb logic lives in InventoryPanel —
// this script just shows/hides the CanvasLayer and binds the panel to the
// active player.
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] public GameClient gameClient;
	[Export] private InventoryPanel _panel;

	Action _onClose;
	Player _player;

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned += OnPlayerSpawned;
		}
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
		}
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
		// If the screen happens to already be visible when the player spawns
		// (load-order edge case), forward the bind immediately so the slots
		// populate without waiting for an Open() / Close() cycle.
		if (Visible)
		{
			_panel?.Bind(_player);
		}
	}

	public void Open(Action onClose = null)
	{
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

	void OnVisibilityChanged()
	{
		Input.MouseMode = Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
		if (Visible)
		{
			_panel?.Bind(_player);
		}
		else
		{
			_panel?.Unbind();
		}
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
